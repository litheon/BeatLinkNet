using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;

namespace BeatLink.Data;

/// <summary>
/// A listener that wants to closely follow track playback for a particular player, registered
/// with <see cref="TimeFinder.AddTrackPositionListener"/>. (This remains an interface rather than
/// an event because the <see cref="TimeFinder"/> keeps per-listener state recording the last
/// update each listener was sent, and delivers an immediate initial callback on registration.)
/// </summary>
public interface ITrackPositionListener
{
    /// <summary>
    /// Called when there has been a significant change in movement since the last update sent to
    /// this listener.
    /// </summary>
    /// <param name="update">the latest information about the current track position and playback
    /// state and speed, or <c>null</c> if we can no longer determine that information</param>
    void MovementChanged(TrackPositionUpdate? update);
}

/// <summary>
/// A track position listener which also wants to know as soon as a new beat is reported by the
/// player it is following, along with the specific position update that resulted from the beat.
/// </summary>
public interface ITrackPositionBeatListener : ITrackPositionListener
{
    /// <summary>
    /// Called as soon as a new beat is reported by the player being followed, even if the
    /// position information has not changed significantly.
    /// </summary>
    /// <param name="beat">the beat packet that was received</param>
    /// <param name="position">the latest track position information, reflecting the beat</param>
    void NewBeat(Beat beat, TrackPositionUpdate position);
}

/// <summary>
/// <para>Watches the beat packets and transport information contained in player status updates to
/// infer the current track playback position based on the most recent information available, the
/// time at which that was received, and the playback pitch and direction that was in effect at
/// that time.</para>
///
/// <para>Takes advantage of
/// <a href="https://djl-analysis.deepsymmetry.org/djl-analysis/beats.html#absolute-position-packets">precise
/// position packets</a> from the CDJ-3000 to support a much more precise and robust tracking of
/// playback (and idle) position for those players.</para>
///
/// <para>Can only operate properly when track metadata and beat grids are available, as these are
/// necessary to convert beat numbers into track positions.</para>
/// </summary>
/// <remarks>
/// Ported from Deep Symmetry's beat-link <c>TimeFinder</c>, by James Elliott.
/// </remarks>
public sealed class TimeFinder : LifecycleParticipant
{
    private static readonly ILogger Logger = BeatLinkLogging.CreateLogger<TimeFinder>();

    /// <summary>
    /// Keeps track of the latest position information we have from each player, indexed by player
    /// number. When nothing is known for a player, the entry will be missing.
    /// </summary>
    private readonly ConcurrentDictionary<int, TrackPositionUpdate> _positions = new();

    /// <summary>
    /// Keeps track of the latest device update reported by each player, indexed by player number.
    /// </summary>
    private readonly ConcurrentDictionary<int, DeviceUpdate> _updates = new();

    /// <summary>
    /// Our device-lost handler watches for devices to disappear from the network, so we can
    /// discard all information about them.
    /// </summary>
    private void OnDeviceLost(object? sender, DeviceAnnouncement announcement)
    {
        if (announcement.DeviceNumber == 25 && announcement.DeviceName == "NXS-GW")
        {
            Logger.LogDebug("Ignoring departure of Kuvo gateway, which fight each other and come and go constantly, especially in CDJ-3000s.");
            return;
        }
        Logger.LogInformation("Clearing position information in response to the loss of a device, {Announcement}", announcement);
        _positions.TryRemove(announcement.DeviceNumber, out _);
        _updates.TryRemove(announcement.DeviceNumber, out _);
    }

    /// <summary>
    /// Keep track of whether we are running.
    /// </summary>
    private volatile bool _running;

    /// <summary>
    /// Check whether we are currently running: <c>true</c> if track playback positions are being
    /// kept track of for all active players.
    /// </summary>
    public override bool IsRunning => _running;

    /// <summary>
    /// Get the latest track position reports available for all visible players.
    /// </summary>
    /// <exception cref="InvalidOperationException">if the TimeFinder is not running</exception>
    public IReadOnlyDictionary<int, TrackPositionUpdate> LatestPositions
    {
        get
        {
            EnsureRunning();
            return new Dictionary<int, TrackPositionUpdate>(_positions);
        }
    }

    /// <summary>
    /// Get the latest device updates (either beats or status updates) available for all visible
    /// players.
    /// </summary>
    /// <exception cref="InvalidOperationException">if the TimeFinder is not running</exception>
    public IReadOnlyDictionary<int, DeviceUpdate> LatestUpdates
    {
        get
        {
            EnsureRunning();
            return new Dictionary<int, DeviceUpdate>(_updates);
        }
    }

    /// <summary>
    /// Get the latest information we have for the specified player. This incorporates both status
    /// updates and beat packets we have received from the player, and so is the most definitive
    /// source of time and tempo information from the player.
    /// </summary>
    /// <param name="player">the player number whose position information is desired</param>
    /// <returns>the consolidated track position information we have for the specified player, or
    /// <c>null</c> if we do not have enough information to calculate it</returns>
    /// <exception cref="InvalidOperationException">if the TimeFinder is not running</exception>
    public TrackPositionUpdate? GetLatestPositionFor(int player)
    {
        EnsureRunning();
        return _positions.GetValueOrDefault(player);
    }

    /// <summary>
    /// Get the latest information we have for the player that sent the supplied status update.
    /// The result incorporates both status updates and beat packets we have received from the
    /// player, and so is the most definitive source of time and tempo information from the player.
    /// </summary>
    /// <param name="update">the device update from a player whose position information is desired</param>
    /// <returns>the consolidated track position information we have for the specified player, or
    /// <c>null</c> if we do not have enough information to calculate it</returns>
    /// <exception cref="InvalidOperationException">if the TimeFinder is not running</exception>
    public TrackPositionUpdate? GetLatestPositionFor(DeviceUpdate update)
    {
        ArgumentNullException.ThrowIfNull(update);
        return GetLatestPositionFor(update.DeviceNumber);
    }

    /// <summary>
    /// Get the beat or status update reported by the specified player, whichever is most recent.
    /// This is available even when we do not have a beat grid available in order to calculate
    /// detailed track position information.
    /// </summary>
    /// <param name="player">the player number whose most recent status is desired</param>
    /// <returns>the latest beat or status reported by the specified player, or <c>null</c> if we
    /// have not heard any</returns>
    /// <exception cref="InvalidOperationException">if the TimeFinder is not running</exception>
    public DeviceUpdate? GetLatestUpdateFor(int player)
    {
        EnsureRunning();
        return _updates.GetValueOrDefault(player);
    }

    /// <summary>
    /// Figure out, based on how much time has elapsed since we received an update, and the
    /// playback position, speed, and direction at the time of that update, where the player will
    /// be now.
    /// </summary>
    /// <param name="update">the most recent update received from a player</param>
    /// <param name="currentTimestamp">the nanosecond timestamp representing when we want to
    /// interpolate the track's position</param>
    /// <returns>the playback position we believe that player has reached now</returns>
    private static long InterpolateTimeSinceUpdate(TrackPositionUpdate update, long currentTimestamp)
    {
        if (!update.Playing)
        {
            return update.Milliseconds;
        }
        var elapsedMillis = (currentTimestamp - update.Timestamp) / 1_000_000;
        var moved = (long)Math.Round(update.Pitch * elapsedMillis, MidpointRounding.AwayFromZero);
        if (update.Reverse)
        {
            return update.Milliseconds - moved;
        }
        return update.Milliseconds + moved;
    }

    /// <summary>
    /// Checks whether a CDJ status update seems to be close enough to a cue that if we just jumped
    /// there (or just loaded the track) it would be a reasonable assumption that we jumped to the
    /// cue.
    /// </summary>
    /// <param name="update">the status update to check for proximity to hot cues and memory points</param>
    /// <param name="beatGrid">the beat grid of the track being played</param>
    /// <returns>a matching memory point if we had a cue list available and were within a beat of
    /// one, or <c>null</c></returns>
    private static CueList.Entry? FindAdjacentCue(CdjStatus update, BeatGrid beatGrid)
    {
        if (!MetadataFinder.Instance.IsRunning)
        {
            return null;
        }
        var metadata = MetadataFinder.Instance.GetLatestMetadataFor(update);
        var newBeat = update.BeatNumber;
        if (metadata?.CueList != null)
        {
            foreach (var entry in metadata.CueList.Entries)
            {
                var entryBeat = beatGrid.FindBeatAtTime(entry.CueTime);
                if (Math.Abs(newBeat - entryBeat) < 2)
                {
                    return entry;  // We have found a cue we likely jumped to
                }
                if (entryBeat > newBeat)
                {
                    break;  // We have moved past our location, no point scanning further.
                }
            }
        }
        return null;
    }

    /// <summary>
    /// Sanity-check a new non-beat update, make sure we are still interpolating a sensible
    /// position, and correct as needed.
    /// </summary>
    /// <param name="lastTrackUpdate">the most recent digested update received from a player</param>
    /// <param name="newDeviceUpdate">a new status update from the player</param>
    /// <param name="beatGrid">the beat grid for the track that is playing, in case we have jumped</param>
    /// <returns>the playback position we believe that player has reached at that point in time</returns>
    private long InterpolateTimeFromUpdate(TrackPositionUpdate lastTrackUpdate, CdjStatus newDeviceUpdate,
        BeatGrid beatGrid)
    {
        var beatNumber = newDeviceUpdate.BeatNumber;
        var noLongerPlaying = !newDeviceUpdate.IsPlaying;

        // If we have just stopped, see if we are near a cue (assuming that information is
        // available), and if so, the best assumption is that the DJ jumped to that cue.
        if (lastTrackUpdate.Playing && noLongerPlaying)
        {
            var jumpedTo = FindAdjacentCue(newDeviceUpdate, beatGrid);
            if (jumpedTo != null)
            {
                return jumpedTo.CueTime;
            }
        }

        // Handle the special case where we were not playing either in the previous or current
        // update, but the DJ might have jumped to a different place in the track.
        if (!lastTrackUpdate.Playing)
        {
            if (lastTrackUpdate.BeatNumber == beatNumber && noLongerPlaying)
            {
                // Haven't moved.
                return lastTrackUpdate.Milliseconds;
            }
            if (noLongerPlaying)
            {
                // Have jumped without playing.
                if (beatNumber < 0)
                {
                    return -1;  // We don't know the position anymore; weird to get into this state and still have a grid?
                }
                // As a heuristic, assume we are right before the beat?
                return TimeOfBeat(beatGrid, beatNumber, newDeviceUpdate);
            }
        }

        // One way or another, we are now playing.
        var elapsedMillis = (newDeviceUpdate.Timestamp - lastTrackUpdate.Timestamp) / 1_000_000;
        var moved = (long)Math.Round(lastTrackUpdate.Pitch * elapsedMillis, MidpointRounding.AwayFromZero);
        var interpolated = lastTrackUpdate.Reverse ?
            Math.Max(lastTrackUpdate.Milliseconds - moved, 0) : lastTrackUpdate.Milliseconds + moved;
        if (Math.Abs(beatGrid.FindBeatAtTime(interpolated) - beatNumber) < 2)
        {
            return interpolated;  // Our calculations still look plausible
        }
        // The player has jumped or drifted somewhere unexpected, correct.
        if (newDeviceUpdate.IsPlayingForwards)
        {
            return TimeOfBeat(beatGrid, beatNumber, newDeviceUpdate);
        }
        return beatGrid.GetTimeWithinTrack(Math.Min(beatNumber + 1, beatGrid.BeatCount));
    }

    /// <summary>
    /// Get the best guess we have for the current track position on the specified player.
    /// </summary>
    /// <param name="player">the player number whose position is desired</param>
    /// <returns>the milliseconds into the track that we believe playback has reached, or -1 if we
    /// don't know</returns>
    public long GetTimeFor(int player)
    {
        if (_positions.TryGetValue(player, out var update))
        {
            return InterpolateTimeSinceUpdate(update, Util.CurrentTimestampNanos());
        }
        return -1;  // We don't know.
    }

    /// <summary>
    /// Get the best guess we have for the current track position on the player that sent the
    /// specified update.
    /// </summary>
    /// <param name="update">the device update from a player whose position is desired</param>
    /// <returns>the milliseconds into the track that we believe playback has reached, or -1 if we
    /// don't know</returns>
    public long GetTimeFor(DeviceUpdate update)
    {
        ArgumentNullException.ThrowIfNull(update);
        return GetTimeFor(update.DeviceNumber);
    }

    /// <summary>
    /// Keeps track of the listeners that have registered interest in closely following track
    /// playback for a particular player. The keys are the listener interface, and the values are
    /// the last update that was sent to that listener. If no information was known during the last
    /// update, the special value <see cref="NoInformation"/> is used to represent it, rather than
    /// trying to store a <c>null</c> value in the map.
    /// </summary>
    private readonly ConcurrentDictionary<ITrackPositionListener, TrackPositionUpdate> _trackPositionListeners = new();

    /// <summary>
    /// Keeps track of the player numbers that registered track position listeners are interested in.
    /// </summary>
    private readonly ConcurrentDictionary<ITrackPositionListener, int> _listenerPlayerNumbers = new();

    /// <summary>
    /// This is used to represent the fact that we have told a listener that there is no
    /// information for it, since we can't actually store a <c>null</c> value in a
    /// <see cref="ConcurrentDictionary{TKey,TValue}"/>.
    /// </summary>
    private static readonly TrackPositionUpdate NoInformation =
        new(0, 0, 0, false, false, 0, false, null, false, false);

    /// <summary>
    /// <para>Add a listener that wants to closely follow track playback for a particular player.
    /// The listener will be called as soon as there is an initial
    /// <see cref="TrackPositionUpdate"/> for the specified player, and whenever there is an
    /// unexpected change in playback position, speed, or state on that player.</para>
    ///
    /// <para>To help the listener orient itself, it is sent a
    /// <see cref="ITrackPositionListener.MovementChanged"/> message immediately upon registration
    /// to report the current playback position, even if none is known (in which case it will be
    /// called with the value <c>null</c>).</para>
    ///
    /// <para>If the same listener was previously registered (for example, to listen to a different
    /// player), this call replaces the former registration with the new one.</para>
    /// </summary>
    /// <param name="player">the player number that the listener is interested in</param>
    /// <param name="listener">the interface that will be called when there are changes in track
    /// playback on the player</param>
    public void AddTrackPositionListener(int player, ITrackPositionListener listener)
    {
        ArgumentNullException.ThrowIfNull(listener);
        _listenerPlayerNumbers[listener] = player;
        var currentPosition = _positions.GetValueOrDefault(player);
        _trackPositionListeners[listener] = currentPosition ?? NoInformation;
        listener.MovementChanged(currentPosition);  // If this throws an exception, the caller will catch it.
    }

    /// <summary>
    /// Remove a listener that was following track playback movement.
    /// </summary>
    /// <param name="listener">the interface that will no longer be called for changes in track playback</param>
    public void RemoveTrackPositionListener(ITrackPositionListener listener)
    {
        ArgumentNullException.ThrowIfNull(listener);
        _trackPositionListeners.TryRemove(listener, out _);
        _listenerPlayerNumbers.TryRemove(listener, out _);
    }

    /// <summary>
    /// Backing storage for <see cref="Slack"/>.
    /// </summary>
    private long _slack = 50;

    /// <summary>
    /// How many milliseconds is our interpolated time allowed to drift from what is being reported
    /// by a player before we consider it a significant enough change to report to listeners that
    /// are trying to closely track a player's playback position.
    /// </summary>
    public long Slack
    {
        get => Interlocked.Read(ref _slack);
        set => Interlocked.Exchange(ref _slack, value);
    }

    /// <summary>
    /// Check whether we have diverged from what we would predict from the last update that was
    /// sent to a particular track position listener.
    /// </summary>
    /// <param name="lastUpdate">the last update that was sent to the listener</param>
    /// <param name="currentUpdate">the latest update available for the same player</param>
    /// <returns><c>true</c> if the listener will have diverged by more than our permitted amount
    /// of slack, and so should be updated</returns>
    private bool InterpolationsDisagree(TrackPositionUpdate lastUpdate, TrackPositionUpdate currentUpdate)
    {
        var now = Util.CurrentTimestampNanos();
        var skew = Math.Abs(InterpolateTimeSinceUpdate(lastUpdate, now) - InterpolateTimeSinceUpdate(currentUpdate, now));
        var tolerance = lastUpdate.Playing ? Slack : 0;  // If we are not playing, any difference is real, from a precise update.
        if (tolerance > 0 && skew > tolerance)
        {
            Logger.LogDebug("InterpolationsDisagree: updates arrived {Millis} ms apart, last {LastKind}interpolates to {LastTime}, current {CurrentKind}interpolates to {CurrentTime}, skew {Skew}",
                (currentUpdate.Timestamp - lastUpdate.Timestamp) / 1_000_000,
                lastUpdate.FromBeat ? "(beat) " : "", InterpolateTimeSinceUpdate(lastUpdate, now),
                currentUpdate.FromBeat ? "(beat) " : "", InterpolateTimeSinceUpdate(currentUpdate, now), skew);
        }
        return skew > tolerance;
    }

    /// <summary>
    /// Check whether the pitches of two updates differ by enough to be worth reporting, using a
    /// looser tolerance when comparing a precise-position update with a beat, since beats send
    /// pitch differently in that situation.
    /// </summary>
    /// <param name="lastUpdate">the last update that was sent to a listener</param>
    /// <param name="currentUpdate">the latest update available for the same player</param>
    /// <returns><c>true</c> if the difference is significant</returns>
    private static bool PitchesDiffer(TrackPositionUpdate lastUpdate, TrackPositionUpdate currentUpdate)
    {
        var delta = Math.Abs(lastUpdate.Pitch - currentUpdate.Pitch);
        if (lastUpdate.Precise && (lastUpdate.FromBeat != currentUpdate.FromBeat))
        {
            // We're in a precise position packet situation, so beats send pitch differently.
            return delta > 0.001;
        }
        // Pitches are comparable, we can use a tight tolerance to detect changes.
        return delta > 0.000001;
    }

    /// <summary>
    /// Check if the current position tracking information for a player represents a significant
    /// change compared to what a listener was last informed to expect, and if so, send another
    /// update. If this is a definitive update (i.e. a new beat), and the listener wants all beats,
    /// always send it.
    /// </summary>
    /// <param name="player">the device number for which an update has occurred</param>
    /// <param name="update">the latest track position tracking information for the specified
    /// player, or <c>null</c> if we no longer have any</param>
    /// <param name="beat">if this update was triggered by a beat packet, contains the packet to
    /// pass on to interested listeners</param>
    private void UpdateListenersIfNeeded(int player, TrackPositionUpdate? update, Beat? beat)
    {
        // Iterate over a copy to avoid issues with concurrent modification.
        foreach (var entry in _trackPositionListeners.ToArray())
        {
            if (_listenerPlayerNumbers.TryGetValue(entry.Key, out var listenerPlayer) && player == listenerPlayer)
            {
                // This listener is interested in this player.
                if (update == null)
                {
                    // We are reporting a loss of information.
                    if (!ReferenceEquals(entry.Value, NoInformation))
                    {
                        if (_trackPositionListeners.TryUpdate(entry.Key, NoInformation, entry.Value))
                        {
                            try
                            {
                                entry.Key.MovementChanged(null);
                            }
                            catch (Exception t)
                            {
                                Logger.LogWarning(t, "Problem delivering null MovementChanged update");
                            }
                        }
                    }
                }
                else
                {
                    // We have some information, see if it is a significant change from what was
                    // last reported.
                    var lastUpdate = entry.Value;
                    if (ReferenceEquals(lastUpdate, NoInformation) ||
                        lastUpdate.Playing != update.Playing ||
                        PitchesDiffer(lastUpdate, update) ||
                        InterpolationsDisagree(lastUpdate, update))
                    {
                        if (_trackPositionListeners.TryUpdate(entry.Key, update, entry.Value))
                        {
                            try
                            {
                                entry.Key.MovementChanged(update);
                            }
                            catch (Exception t)
                            {
                                Logger.LogWarning(t, "Problem delivering MovementChanged update");
                            }
                        }
                    }

                    // And regardless of whether this was a significant change, if this was a new
                    // beat and the listener implements the interface that requests all beats,
                    // send that information.
                    if (update.FromBeat && beat != null && entry.Key is ITrackPositionBeatListener beatListener)
                    {
                        try
                        {
                            beatListener.NewBeat(beat, update);
                        }
                        catch (Exception t)
                        {
                            Logger.LogWarning(t, "Problem delivering NewBeat update");
                        }
                    }
                }
            }
        }
    }

    /// <summary>
    /// Relays device updates from the <see cref="VirtualCdj"/> to the processing logic.
    /// </summary>
    private void OnDeviceUpdateReceived(object? sender, DeviceUpdate update) => ProcessDeviceUpdate(update);

    /// <summary>
    /// Reacts to player status updates to update the predicted playback position and state, and
    /// potentially inform registered track position listeners of significant changes. If we are
    /// receiving precise position packets from the same player, we ignore these less-useful
    /// updates. (Internal, rather than private as in the Java implementation, so tests can inject
    /// synthetic status updates deterministically.)
    /// </summary>
    /// <param name="update">the device update that has been received</param>
    internal void ProcessDeviceUpdate(DeviceUpdate update)
    {
        var device = update.DeviceNumber;
        var lastPosition = _positions.GetValueOrDefault(device);
        if (update is CdjStatus status && (lastPosition == null || !lastPosition.Precise))
        {
            _updates[device] = update;
            var beatGrid = BeatGridFinder.Instance.GetLatestBeatGridFor(update);
            var beatNumber = status.BeatNumber;
            if (beatGrid != null && beatNumber >= 0)
            {
                var done = false;
                while (!done && (lastPosition == null || lastPosition.Timestamp < update.Timestamp))
                {
                    TrackPositionUpdate newPosition;
                    if (lastPosition == null || !ReferenceEquals(lastPosition.BeatGrid, beatGrid))
                    {
                        // This is a new track, and we have not yet received a beat packet for it.
                        var timeGuess = TimeOfBeat(beatGrid, beatNumber, update);
                        var likelyCue = FindAdjacentCue(status, beatGrid);
                        if (likelyCue != null)
                        {
                            timeGuess = likelyCue.CueTime;
                        }
                        newPosition = new TrackPositionUpdate(update.Timestamp, timeGuess, beatNumber,
                            false, status.IsPlaying, Util.PitchToMultiplier(update.Pitch),
                            status.IsPlayingBackwards, beatGrid, false, false);
                    }
                    else
                    {
                        var newTime = InterpolateTimeFromUpdate(lastPosition, status, beatGrid);
                        var newReverse = status.IsPlayingBackwards;
                        // Although the players report themselves stopped when they hit the end of
                        // the track playing forward, they don't do that when they hit the
                        // beginning playing backward! That makes for weird timecode and track
                        // waveform positioning, so we make up for it by synthesizing a stopped
                        // state here.
                        var newPlaying = status.IsPlaying && (!newReverse || newTime > 0);
                        newPosition = new TrackPositionUpdate(update.Timestamp, newTime, beatNumber,
                            false, newPlaying, Util.PitchToMultiplier(update.Pitch),
                            newReverse, beatGrid, false, false);
                    }
                    if (lastPosition == null)
                    {
                        done = _positions.TryAdd(device, newPosition);
                    }
                    else
                    {
                        done = _positions.TryUpdate(device, newPosition, lastPosition);
                    }
                    if (done)
                    {
                        UpdateListenersIfNeeded(device, newPosition, null);
                    }
                    else
                    {
                        // Some other thread updated the position while we were working, re-evaluate.
                        lastPosition = _positions.GetValueOrDefault(device);
                    }
                }
            }
            else
            {
                _positions.TryRemove(device, out _);  // We can't say where that player is.
                UpdateListenersIfNeeded(device, null, null);
            }
        }
    }

    /// <summary>
    /// Translates a beat number to a track time, given a beat grid. In almost all cases, simply
    /// delegates that task to the beat grid. However, since players sometimes report beats that
    /// are outside the beat grid (especially when looping tracks that extend for more than a
    /// beat's worth of time past the last beat in the beat grid), to avoid weird exceptions and
    /// infinitely-growing track time reports in such situations, we extrapolate an extension to
    /// the beat grid by repeating the interval between the last two beats in the track.
    /// <para>In the completely degenerate case of a track with a single beat (which probably will
    /// never occur), we simply return that beat's time even if you ask for a later one.</para>
    /// </summary>
    /// <param name="beatGrid">the times at which known beats fall in the track whose playback time
    /// we are reporting</param>
    /// <param name="beatNumber">the number of the beat that a player has just reported reaching,
    /// which may be slightly greater than the actual number of beats in the track</param>
    /// <param name="update">the device update which caused this calculation to take place, in case
    /// we need to log a warning that the reported beat number falls outside the beat grid</param>
    /// <returns>the number of milliseconds into the track at which that beat falls, perhaps
    /// extrapolated past the final recorded beat</returns>
    private static long TimeOfBeat(BeatGrid beatGrid, int beatNumber, DeviceUpdate update)
    {
        if (beatNumber <= beatGrid.BeatCount)
        {
            return beatGrid.GetTimeWithinTrack(beatNumber);
        }
        Logger.LogWarning("Received beat number {BeatNumber} from {DeviceName} {DeviceNumber}, but beat grid only goes up to beat {BeatCount}. Packet: {Update}",
            beatNumber, update.DeviceName, update.DeviceNumber, beatGrid.BeatCount, update);
        if (beatGrid.BeatCount < 2)
        {
            return beatGrid.GetTimeWithinTrack(1);
        }
        var lastTime = beatGrid.GetTimeWithinTrack(beatGrid.BeatCount);
        var lastInterval = lastTime - beatGrid.GetTimeWithinTrack(beatGrid.BeatCount - 1);
        return lastTime + (lastInterval * (beatNumber - beatGrid.BeatCount));
    }

    /// <summary>
    /// Reacts to beat messages to update the definitive playback position for that player. Because
    /// we may sometimes get a beat packet before a corresponding device update containing the
    /// actual beat number, don't increment past the end of the beat grid, because we must be
    /// looping if we get an extra beat there. (Internal, rather than private as in the Java
    /// implementation, so tests can inject synthetic beats deterministically.)
    /// </summary>
    /// <param name="beat">the beat packet that was received</param>
    internal void ProcessBeat(Beat beat)
    {
        var device = beat.DeviceNumber;
        if (device < 16)
        {
            // We only care about CDJs.
            _updates[device] = beat;
            var beatGrid = BeatGridFinder.Instance.GetLatestBeatGridFor(beat);
            var lastPosition = _positions.GetValueOrDefault(device);
            if (beatGrid != null)
            {
                int beatNumber;
                if (lastPosition == null || !ReferenceEquals(lastPosition.BeatGrid, beatGrid))
                {
                    // We don't handle beat packets received before any status packets for the
                    // player. This will probably never happen except in cases where we can't use
                    // the status packets because the player is a pre-nexus model that does not
                    // send beat numbers, so we don't want to be tricked into guessing a position
                    // for that player based on no valid information.
                    return;
                }

                // See if we have moved past 1/5 of the way into the current beat.
                var distanceIntoBeat = lastPosition.Milliseconds - beatGrid.GetTimeWithinTrack(lastPosition.BeatNumber);
                var farEnough = 6000000 / beat.Bpm / 5;
                if (distanceIntoBeat >= farEnough)
                {
                    // We can consider this the start of a new beat.
                    beatNumber = Math.Min(lastPosition.BeatNumber + 1, beatGrid.BeatCount);  // Handle loop at end
                }
                else
                {
                    // We must have received the beat packet out of order with respect to the
                    // first status in the beat.
                    beatNumber = lastPosition.BeatNumber;
                }

                // We know the player is playing forward because otherwise we don't get beats.
                var newPosition = new TrackPositionUpdate(beat.Timestamp,
                    TimeOfBeat(beatGrid, beatNumber, beat), beatNumber, true, true,
                    Util.PitchToMultiplier(beat.Pitch), false, beatGrid,
                    lastPosition.Precise, true);
                _positions[device] = newPosition;
                UpdateListenersIfNeeded(device, newPosition, beat);
            }
            else
            {
                // We can't determine where the player is. That's ok if it is sending us precise
                // position packets (e.g. a CDJ-3000 playing an unanalyzed track), we will just
                // wait for the next one in that case. Otherwise, clear our notion of the playback
                // position.
                if (lastPosition != null && !lastPosition.Precise)
                {
                    _positions.TryRemove(device, out _);
                    UpdateListenersIfNeeded(device, null, beat);
                }
            }
        }
    }

    /// <summary>
    /// Relays beats from the <see cref="BeatFinder"/> to <see cref="ProcessBeat"/>. Registered
    /// through the internal priority beat listener seam, after the <see cref="VirtualCdj"/>, so
    /// the TimeFinder has an up-to-date beat number before any public subscribers see the beat
    /// (replacing the special-case check the Java <c>BeatFinder</c> made for the TimeFinder's
    /// listener).
    /// </summary>
    /// <param name="owner">the TimeFinder which should process the beats</param>
    private sealed class PriorityBeatListener(TimeFinder owner) : IPriorityBeatListener
    {
        /// <inheritdoc/>
        public void OnBeat(Beat beat) => owner.ProcessBeat(beat);
    }

    /// <summary>
    /// The priority beat listener registered while we are running.
    /// </summary>
    private readonly PriorityBeatListener _priorityBeatListener;

    /// <summary>
    /// Tracks whether we have been told to disregard precise position packets from devices that
    /// send them, to help with jitter while a better smoothing approach is worked on. The default
    /// is to use them to tightly track playback, but that is currently resulting in a problematic
    /// amount of jitter when synchronizing with other audio sources via Ableton Link. Ignoring
    /// these packets will treat CDJ-3000s like other player hardware, where only beat packets are
    /// trusted as position anchors. (Experimental, like the Java <c>setUsePrecisePositionPackets</c>.)
    /// </summary>
    private volatile bool _usePrecisePositionPackets = true;

    /// <inheritdoc cref="_usePrecisePositionPackets"/>
    public bool UsePrecisePositionPackets
    {
        get => _usePrecisePositionPackets;
        set => _usePrecisePositionPackets = value;
    }

    /// <summary>
    /// Relays precise positions received by the <see cref="BeatFinder"/> to the processing logic.
    /// </summary>
    private void OnPrecisePositionReceived(object? sender, PrecisePosition position) =>
        ProcessPrecisePosition(position);

    /// <summary>
    /// Reacts to precise position updates to update the definitive, precise position of the
    /// reporting player. (Internal, rather than private as in the Java implementation, so tests
    /// can inject synthetic positions deterministically.)
    /// </summary>
    /// <param name="position">the precise position packet that was received</param>
    internal void ProcessPrecisePosition(PrecisePosition position)
    {
        if (!UsePrecisePositionPackets)
        {
            return;
        }
        var device = position.DeviceNumber;
        if (device < 16)
        {
            // We only care about CDJs, if other devices ever send these.
            _updates[device] = position;
            var lastStatus = VirtualCdj.Instance.GetLatestStatusFor(device);
            var playing = lastStatus is CdjStatus { IsPlaying: true };
            var reverse = lastStatus is CdjStatus { IsPlayingBackwards: true };
            var beatGrid = BeatGridFinder.Instance.GetLatestBeatGridFor(position);
            var beatNumber = beatGrid?.FindBeatAtTime(position.PlaybackPosition) ?? 0;
            var newPosition = new TrackPositionUpdate(position.Timestamp,
                position.PlaybackPosition, beatNumber, true, playing,
                Util.PitchToMultiplier(position.Pitch), reverse, beatGrid, true, false);
            _positions[device] = newPosition;
            UpdateListenersIfNeeded(device, newPosition, null);
        }
    }

    /// <summary>
    /// Set once we have hooked up the handlers which allow us to automatically shut down when the
    /// services we depend on do, so we never subscribe more than once.
    /// </summary>
    private bool _dependencyListenersRegistered;

    /// <summary>
    /// Set up to automatically stop if anything we depend on stops.
    /// </summary>
    private void OnDependencyStopped(object? sender, EventArgs e)
    {
        if (IsRunning)
        {
            Logger.LogInformation("TimeFinder stopping because {Sender} has.", sender);
            Stop();
        }
    }

    /// <summary>
    /// Serializes lifecycle transitions, standing in for the Java <c>synchronized</c> start and
    /// stop methods.
    /// </summary>
    private readonly object _lifecycleLock = new();

    /// <summary>
    /// Start interpolating playback position for all active players. Starts the
    /// <see cref="BeatGridFinder"/>, <see cref="BeatFinder"/>, and <see cref="VirtualCdj"/> if
    /// they are not already running, because we need them to perform our calculations. This in
    /// turn starts the <see cref="DeviceFinder"/>, so we can keep track of the comings and goings
    /// of players themselves.
    /// </summary>
    /// <exception cref="Exception">if there is a problem starting the required components</exception>
    public void Start()
    {
        lock (_lifecycleLock)
        {
            if (IsRunning)
            {
                return;
            }
            if (!_dependencyListenersRegistered)
            {
                DeviceFinder.Instance.DeviceLost += OnDeviceLost;
                BeatGridFinder.Instance.Stopped += OnDependencyStopped;
                VirtualCdj.Instance.Stopped += OnDependencyStopped;
                BeatFinder.Instance.Stopped += OnDependencyStopped;
                _dependencyListenersRegistered = true;
            }
            BeatGridFinder.Instance.Start();
            VirtualCdj.Instance.DeviceUpdateReceived += OnDeviceUpdateReceived;
            VirtualCdj.Instance.Start();
            // Register for beats through the priority seam; the VirtualCdj registered itself
            // first (in its constructor, which has necessarily run by this point), preserving the
            // Java library's guarantee that it processes each beat before we do, and that we
            // process it before any public subscriber.
            BeatFinder.Instance.AddPriorityBeatListener(_priorityBeatListener);
            BeatFinder.Instance.PrecisePositionReceived += OnPrecisePositionReceived;
            BeatFinder.Instance.Start();
            _running = true;
            DeliverLifecycleAnnouncement(Logger, true);
        }
    }

    /// <summary>
    /// Stop interpolating playback position for all active players.
    /// </summary>
    public void Stop()
    {
        lock (_lifecycleLock)
        {
            if (!IsRunning)
            {
                return;
            }
            BeatFinder.Instance.PrecisePositionReceived -= OnPrecisePositionReceived;
            BeatFinder.Instance.RemovePriorityBeatListener(_priorityBeatListener);
            VirtualCdj.Instance.DeviceUpdateReceived -= OnDeviceUpdateReceived;
            _running = false;
            _positions.Clear();
            _updates.Clear();
            DeliverLifecycleAnnouncement(Logger, false);
        }
    }

    /// <summary>
    /// The single instance of this class which exists in the program.
    /// </summary>
    public static TimeFinder Instance { get; } = new();

    /// <summary>
    /// Prevent direct instantiation.
    /// </summary>
    private TimeFinder()
    {
        _priorityBeatListener = new PriorityBeatListener(this);
    }

    /// <inheritdoc/>
    public override string ToString()
    {
        var sb = new System.Text.StringBuilder("TimeFinder[running:").Append(IsRunning);
        sb.Append(", listener count:").Append(_trackPositionListeners.Count);
        if (IsRunning)
        {
            sb.Append(", latestPositions:[").Append(string.Join(", ", LatestPositions.Select(e => $"{e.Key}={e.Value}")));
            sb.Append("], latestUpdates:[").Append(string.Join(", ", LatestUpdates.Select(e => $"{e.Key}={e.Value}"))).Append(']');
        }
        return sb.Append(']').ToString();
    }
}
