using BeatLink.CrateDigger.Pdb;

namespace BeatLink.Data;

/// <summary>
/// A class that is able to provide metadata for a loaded track without the need to query the
/// source player can implement this interface and register itself with the metadata finder.
/// Examples include cue lists that store their own copies of the metadata.
/// </summary>
/// <remarks>
/// Ported from Deep Symmetry's beat-link <c>MetadataProvider</c>, by James Elliott.
/// </remarks>
public interface IMetadataProvider
{
    /// <summary>
    /// Get the list of media devices for which metadata can be offered by this provider. If the
    /// provider can offer metadata for all media, return an empty list, and it will always be
    /// consulted. Otherwise, it will only be consulted for media that were listed in response to
    /// this call when it was registered with the metadata finder.
    /// </summary>
    /// <returns>the media device descriptors for which we have at least one available metadata item</returns>
    IReadOnlyList<MediaDetails> SupportedMedia();

    /// <summary>
    /// Get metadata for a particular track, if it is available.
    /// </summary>
    /// <param name="sourceMedia">the media to which the track belongs, for use by providers which
    /// store metadata from multiple sources</param>
    /// <param name="track">identifies the track whose metadata is desired; since track metadata
    /// always has a player and slot associated with it, those are needed as well as the rekordbox
    /// ID in order to create it</param>
    /// <returns>the metadata corresponding to that track, or <c>null</c> if it is not a track for
    /// which we have metadata</returns>
    TrackMetadata? GetTrackMetadata(MediaDetails sourceMedia, DataReference track);

    /// <summary>
    /// Get a particular album art image, if it is available.
    /// </summary>
    /// <param name="sourceMedia">the media to which the art belongs, for use by providers which
    /// store metadata from multiple sources</param>
    /// <param name="art">identifies the album art whose metadata is desired; since art always has
    /// a player and slot associated with it, those are needed as well as the rekordbox ID in order
    /// to create it</param>
    /// <returns>the art with the specified id, or <c>null</c> if we don't have it to offer</returns>
    AlbumArt? GetAlbumArt(MediaDetails sourceMedia, DataReference art);

    /// <summary>
    /// Get the beat grid for a particular track, if it is available.
    /// </summary>
    /// <param name="sourceMedia">the media to which the track belongs, for use by providers which
    /// store metadata from multiple sources</param>
    /// <param name="track">identifies the track whose beat grid is desired; since beat grids
    /// always have a player and slot associated with them, those are needed as well as the
    /// rekordbox ID in order to create one</param>
    /// <returns>the beat grid corresponding to that track, or <c>null</c> if we don't have one to offer</returns>
    BeatGrid? GetBeatGrid(MediaDetails sourceMedia, DataReference track);

    /// <summary>
    /// Get the cue list for a particular track, if it is available.
    /// </summary>
    /// <param name="sourceMedia">the media to which the track belongs, for use by providers which
    /// store metadata from multiple sources</param>
    /// <param name="track">identifies the track whose cue list is desired</param>
    /// <returns>the cue list corresponding to that track, or <c>null</c> if we don't have one to offer</returns>
    CueList? GetCueList(MediaDetails sourceMedia, DataReference track);

    /// <summary>
    /// Get the waveform preview for a particular track, if it is available.
    /// </summary>
    /// <param name="sourceMedia">the media to which the track belongs, for use by providers which
    /// store metadata from multiple sources</param>
    /// <param name="track">identifies the track whose waveform preview is desired; since waveform
    /// previews always have a player and slot associated with them, those are needed as well as
    /// the rekordbox ID in order to create one</param>
    /// <returns>the waveform preview corresponding to that track, or <c>null</c> if we don't have
    /// one to offer</returns>
    WaveformPreview? GetWaveformPreview(MediaDetails sourceMedia, DataReference track);

    /// <summary>
    /// Get the waveform detail for a particular track, if it is available.
    /// </summary>
    /// <param name="sourceMedia">the media to which the track belongs, for use by providers which
    /// store metadata from multiple sources</param>
    /// <param name="track">identifies the track whose waveform detail is desired; since waveform
    /// details always have a player and slot associated with them, those are needed as well as
    /// the rekordbox ID in order to create one</param>
    /// <returns>the waveform detail corresponding to that track, or <c>null</c> if we don't have
    /// one to offer</returns>
    WaveformDetail? GetWaveformDetail(MediaDetails sourceMedia, DataReference track);

    /// <summary>
    /// Get a specific analysis tag from the analysis file with a specific extension for a
    /// particular track, if it is available.
    /// </summary>
    /// <param name="sourceMedia">the media to which the track belongs, for use by providers which
    /// store metadata from multiple sources</param>
    /// <param name="track">identifies the track whose analysis section is desired</param>
    /// <param name="fileExtension">identifies the specific analysis file from which the tag is
    /// desired (such as ".DAT" or ".EXT")</param>
    /// <param name="typeTag">the four-character code identifying the specific section of the
    /// analysis file desired (such as "PSSI")</param>
    /// <returns>the parsed analysis section, or <c>null</c> if we don't have it to offer</returns>
    RekordboxAnlz.TaggedSection? GetAnalysisSection(MediaDetails sourceMedia, DataReference track,
        string fileExtension, string typeTag);
}
