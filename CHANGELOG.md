# Change Log

All notable changes to this project will be documented in this file.
This project adheres to [Semantic Versioning](https://semver.org).

## [Unreleased]

### Added

- Initial C#/.NET 9 port of [beat-link](https://github.com/Deep-Symmetry/beat-link)
  8.1.0-SNAPSHOT (commit `ef0aaa1`), including the functionality of its companion libraries
  [electro](https://github.com/Deep-Symmetry/electro) v0.1.4 (Metronome/Snapshot) and
  [crate-digger](https://github.com/Deep-Symmetry/crate-digger) (commit `d95c444`:
  rekordbox PDB/ANLZ parsing, NFSv2 file retrieval).
- Core protocol: `DeviceFinder`, `BeatFinder`, `VirtualCdj`, `BeatSender`, packet models
  (`DeviceAnnouncement`, `CdjStatus`, `Beat`, `PrecisePosition`, `MixerStatus`,
  `MediaDetails`, `PlayerSettings`).
- dbserver metadata protocol: `Field`/`Message` wire format, `Client`, `ConnectionManager`.
- Data models: `TrackMetadata`, `CueList`, `BeatGrid`, `WaveformPreview`/`WaveformDetail`
  (blue/RGB/three-band), `AlbumArt`, with a platform-neutral `ArgbColor` in place of AWT colors.
- Kaitai-generated `RekordboxPdb`/`RekordboxAnlz` parsers, `Database` indexes, and a
  hand-written NFSv2/ONC-RPC client (`FileFetcher`) replacing Remote Tea.
- The full finder suite (`MetadataFinder`, `TimeFinder`, `WaveformFinder`, `ArtFinder`,
  `BeatGridFinder`, `SignatureFinder`, `AnalysisTagFinder`) plus `MenuLoader`, the
  `CrateDigger` NFS metadata provider (PDB and encrypted Device Library Plus SQLite paths),
  and experimental Opus Quad support (`OpusProvider` metadata archives, `VirtualRekordbox`
  rekordbox-lighting emulation, PSSI track matching), marked `[Experimental("BLNET001")]`.

### Changed (deviations from the Java originals, each documented in source)

- Idiomatic .NET API: events in place of listener interfaces, properties in place of getters,
  `Microsoft.Extensions.Logging`, a pluggable `EventDispatcher.Context` in place of the Swing
  event dispatch thread.
- Swing UI components (`WaveformPreviewComponent`, `WaveformDetailComponent`) are not ported;
  the waveform data models expose color/height accessors for any UI framework instead.
- A single monotonic clock is used throughout, fixing upstream's mixed
  `nanoTime`/`currentTimeMillis` comparison in `VirtualCdj.getLatestStatus`.
- Fixed (rather than replicated) small upstream bugs: the `TrackMetadata` remixer/album
  switch fall-through, hot-cue crash on dbserver cue lists containing memory points,
  `FileFetcher.setReadSize` having no effect, and `Metronome.jumpToBar`/`jumpToPhrase`
  placing the start time in the future.
