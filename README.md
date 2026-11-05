# Beat Link Net

A .NET 9 library for synchronizing with beats from Pioneer DJ Link equipment, and finding out
details about the tracks that are playing.

This is an idiomatic C# port of [beat-link](https://github.com/Deep-Symmetry/beat-link) by
[Deep Symmetry](https://deepsymmetry.org/) (ported from version 8.1.0-SNAPSHOT, commit `ef0aaa1`),
including the functionality of its companion libraries
[crate-digger](https://github.com/Deep-Symmetry/crate-digger) (commit `d95c444`: NFS download and
parsing of rekordbox databases and analysis files) and
[electro](https://github.com/Deep-Symmetry/electro) (v0.1.4, musical metronome/timeline).

All protocol analysis and the original implementation are the work of Deep Symmetry and
contributors. See their [dysentery](https://github.com/Deep-Symmetry/dysentery) project and the
[Packet Analysis](https://djl-analysis.deepsymmetry.org/) documentation.

## Features

- Device discovery, beat synchronization, and detailed player status (`DeviceFinder`,
  `BeatFinder`, CDJ-3000 precise positions).
- Full virtual-CDJ participation: device number claiming, tempo master tracking and takeover,
  sending status and beats, remote control (sync, fader start, on-air, track load, settings).
- Track metadata: title/artist/album/key/etc., album art, beat grids, cue lists, waveform
  previews and details (blue, RGB, and CDJ-3000 three-band), song-structure phrases, and menu
  browsing — via both the dbserver protocol and direct NFS download of rekordbox databases
  (which works with any number of players).
- Interpolated playback position tracking (`TimeFinder`) and media-independent track signatures.
- Experimental Opus Quad support (attach crate-digger metadata archives; marked
  `[Experimental("BLNET001")]`).

Unlike the Java original, no UI components are included; the waveform models expose
color/height accessors (`SegmentColor`, `SegmentHeight`) so any .NET UI framework can render them.

## Getting started

```csharp
using BeatLink;
using BeatLink.Data;

// Optional: see log output and route UI-friendly events to your UI thread.
BeatLinkLogging.LoggerFactory = myLoggerFactory;
EventDispatcher.Context = SynchronizationContext.Current;

// Watch for devices.
DeviceFinder.Instance.DeviceFound += (_, device) =>
    Console.WriteLine($"Found {device.DeviceName} #{device.DeviceNumber} at {device.Address}");
DeviceFinder.Instance.Start();

// React to beats (delivered synchronously on the receive thread — return quickly!).
BeatFinder.Instance.BeatReceived += (_, beat) =>
    Console.WriteLine($"Beat from player {beat.DeviceNumber} at {beat.EffectiveTempo} BPM");
BeatFinder.Instance.Start();

// Join the network as a virtual CDJ and track rich status and metadata.
if (VirtualCdj.Instance.Start())
{
    MetadataFinder.Instance.TrackMetadataChanged += (_, update) =>
        Console.WriteLine($"Player {update.Player} loaded: {update.Metadata?.Title}");
    MetadataFinder.Instance.Start();
    Data.CrateDigger.Instance.Start();   // Robust metadata via NFS database download.
    TimeFinder.Instance.Start();         // Interpolated playback positions.
}
```

Notes:

- The library binds UDP ports 50000–50002 exclusively; it cannot run on the same machine as
  rekordbox or other Pro DJ Link software.
- For the encrypted "Device Library Plus" SQLite exports (CDJ-3000/XDJ-AZ/Opus Quad), your
  application must reference a SQLCipher-capable `SQLitePCLRaw` provider (for example
  `SQLitePCLRaw.bundle_e_sqlcipher`) and call `SQLitePCL.Batteries_V2.Init()`, then set
  `OpusProvider.DatabaseKey`.
- Time-sensitive events (beats, status, positions) are delivered synchronously on receive
  threads; everything else is posted to `EventDispatcher.Context` (thread pool by default).

## Manual hardware verification checklist

Automated tests cover the wire formats against golden byte layouts and fake servers, but there
is no substitute for real hardware. With one or more CDJs/mixers on the network (and rekordbox
NOT running on this machine):

1. `DeviceFinder` discovers each device; unplugging one raises `DeviceLost` within ~10 s.
2. `BeatFinder` reports beats matching the player's displayed BPM; a CDJ-3000 also produces
   precise position reports.
3. `VirtualCdj.Start()` claims a device number without disturbing the players, and
   `TempoMaster` follows master handoffs performed on the hardware.
4. `MetadataFinder` (with `CrateDigger` started) delivers title/artist, art, beat grid, cue
   list, and waveforms for a track loaded from USB; works with four players in use.
5. `VirtualCdj.BecomeTempoMaster()` and status/beat sending sync a hardware player to us.
6. Opus Quad: attach a metadata archive built by crate-digger's Archivist for the USB in a
   slot, and verify metadata and PSSI-matched track identification.

## Regenerating the rekordbox parsers

`src/BeatLinkNet/CrateDigger/Pdb/` contains committed C# parsers generated from crate-digger's
Kaitai Struct definitions. To track a new upstream release, update the pinned commit in
`tools/generate-kaitai.sh` and run it (requires `kaitai-struct-compiler`); it applies the
type-level patches in `tools/patch_kaitai.py` automatically.

## License

Licensed under the [Eclipse Public License 2.0](LICENSE.md), the same license as the original
beat-link library from which this is derived.
