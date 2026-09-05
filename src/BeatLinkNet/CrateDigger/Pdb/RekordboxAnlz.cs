// Patched by tools/patch_kaitai.py after generation; see that script for details.
#nullable disable
#pragma warning disable 1591  // generated code carries no XML docs
// This is a generated file! Please edit source .ksy file and use kaitai-struct-compiler to rebuild

using Kaitai;
using System.Collections.Generic;

namespace BeatLink.CrateDigger.Pdb
{

    /// <summary>
    /// These files are created by rekordbox when analyzing audio tracks
    /// to facilitate DJ performance. They include waveforms, beat grids
    /// (information about the precise time at which each beat occurs),
    /// time indices to allow efficient seeking to specific positions
    /// inside variable bit-rate audio streams, and lists of memory cues
    /// and loop points. They are used by Pioneer professional DJ
    /// equipment.
    /// 
    /// The format has been reverse-engineered to facilitate sophisticated
    /// integrations with light and laser shows, videos, and other musical
    /// instruments, by supporting deep knowledge of what is playing and
    /// what is coming next through monitoring the network communications
    /// of the players.
    /// </summary>
    /// <remarks>
    /// Reference: <a href="https://reverseengineering.stackexchange.com/questions/4311/help-reversing-a-edb-database-file-for-pioneers-rekordbox-software">Source</a>
    /// </remarks>
    public partial class RekordboxAnlz : KaitaiStruct
    {
        public static RekordboxAnlz FromFile(string fileName)
        {
            return new RekordboxAnlz(new KaitaiStream(fileName));
        }


        public enum CueEntryStatus
        {
            Disabled = 0,
            Enabled = 1,
            ActiveLoop = 4,
        }

        public enum CueListType
        {
            MemoryCues = 0,
            HotCues = 1,
        }

        public enum MoodHighPhrase
        {
            Intro = 1,
            Up = 2,
            Down = 3,
            Chorus = 5,
            Outro = 6,
        }

        public enum TrackBank
        {
            Default = 0,
            Cool = 1,
            Natural = 2,
            Hot = 3,
            Subtle = 4,
            Warm = 5,
            Vivid = 6,
            Club1 = 7,
            Club2 = 8,
        }

        public enum CueEntryType
        {
            MemoryCue = 1,
            Loop = 2,
        }

        public enum SectionTags
        {
            Cues2 = 1346588466,
            Cues = 1346588482,
            Path = 1347441736,
            BeatGrid = 1347507290,
            SongStructure = 1347638089,
            Vbr = 1347830354,
            WavePreview = 1347895638,
            WaveTiny = 1347900978,
            WaveScroll = 1347900979,
            WaveColorPreview = 1347900980,
            WaveColorScroll = 1347900981,
            Wave3bandPreview = 1347900982,
            Wave3bandScroll = 1347900983,
        }

        public enum TrackMood
        {
            High = 1,
            Mid = 2,
            Low = 3,
        }

        public enum MoodMidPhrase
        {
            Intro = 1,
            Verse1 = 2,
            Verse2 = 3,
            Verse3 = 4,
            Verse4 = 5,
            Verse5 = 6,
            Verse6 = 7,
            Bridge = 8,
            Chorus = 9,
            Outro = 10,
        }

        public enum MoodLowPhrase
        {
            Intro = 1,
            Verse1 = 2,
            Verse1b = 3,
            Verse1c = 4,
            Verse2 = 5,
            Verse2b = 6,
            Verse2c = 7,
            Bridge = 8,
            Chorus = 9,
            Outro = 10,
        }
        public RekordboxAnlz(KaitaiStream p__io, KaitaiStruct p__parent = null, RekordboxAnlz p__root = null) : base(p__io)
        {
            m_parent = p__parent;
            m_root = p__root ?? this;
            _read();
        }
        private void _read()
        {
            _magic = m_io.ReadBytes(4);
            if (!((KaitaiStream.ByteArrayCompare(Magic, new byte[] { 80, 77, 65, 73 }) == 0)))
            {
                throw new ValidationNotEqualError(new byte[] { 80, 77, 65, 73 }, Magic, M_Io, "/seq/0");
            }
            _lenHeader = m_io.ReadU4be();
            _lenFile = m_io.ReadU4be();
            __unnamed3 = m_io.ReadBytes((LenHeader - M_Io.Pos));
            _sections = new List<TaggedSection>();
            {
                var i = 0;
                while (!m_io.IsEof) {
                    _sections.Add(new TaggedSection(m_io, this, m_root));
                    i++;
                }
            }
        }

        /// <summary>
        /// The minimalist CDJ-3000 waveform image suitable for scrolling along
        /// as a track plays on newer high-resolution hardware.
        /// </summary>
        public partial class Wave3bandScrollTag : KaitaiStruct
        {
            public static Wave3bandScrollTag FromFile(string fileName)
            {
                return new Wave3bandScrollTag(new KaitaiStream(fileName));
            }

            public Wave3bandScrollTag(KaitaiStream p__io, RekordboxAnlz.TaggedSection p__parent = null, RekordboxAnlz p__root = null) : base(p__io)
            {
                m_parent = p__parent;
                m_root = p__root;
                _read();
            }
            private void _read()
            {
                _lenEntryBytes = m_io.ReadU4be();
                _lenEntries = m_io.ReadU4be();
                __unnamed2 = m_io.ReadU4be();
                _entries = m_io.ReadBytes((LenEntries * LenEntryBytes));
            }
            private uint _lenEntryBytes;
            private uint _lenEntries;
            private uint __unnamed2;
            private byte[] _entries;
            private RekordboxAnlz m_root;
            private RekordboxAnlz.TaggedSection m_parent;

            /// <summary>
            /// The size of each entry, in bytes. Seems to always be 3.
            /// </summary>
            public uint LenEntryBytes { get { return _lenEntryBytes; } }

            /// <summary>
            /// The number of columns of waveform data (this matches the
            /// non-color waveform length.
            /// </summary>
            public uint LenEntries { get { return _lenEntries; } }
            public uint Unnamed_2 { get { return __unnamed2; } }
            public byte[] Entries { get { return _entries; } }
            public RekordboxAnlz M_Root { get { return m_root; } }
            public RekordboxAnlz.TaggedSection M_Parent { get { return m_parent; } }
        }
        public partial class PhraseMid : KaitaiStruct
        {
            public static PhraseMid FromFile(string fileName)
            {
                return new PhraseMid(new KaitaiStream(fileName));
            }

            public PhraseMid(KaitaiStream p__io, RekordboxAnlz.SongStructureEntry p__parent = null, RekordboxAnlz p__root = null) : base(p__io)
            {
                m_parent = p__parent;
                m_root = p__root;
                _read();
            }
            private void _read()
            {
                _id = ((RekordboxAnlz.MoodMidPhrase) m_io.ReadU2be());
            }
            private MoodMidPhrase _id;
            private RekordboxAnlz m_root;
            private RekordboxAnlz.SongStructureEntry m_parent;
            public MoodMidPhrase Id { get { return _id; } }
            public RekordboxAnlz M_Root { get { return m_root; } }
            public RekordboxAnlz.SongStructureEntry M_Parent { get { return m_parent; } }
        }

        /// <summary>
        /// Stores the file path of the audio file to which this analysis
        /// applies.
        /// </summary>
        public partial class PathTag : KaitaiStruct
        {
            public static PathTag FromFile(string fileName)
            {
                return new PathTag(new KaitaiStream(fileName));
            }

            public PathTag(KaitaiStream p__io, RekordboxAnlz.TaggedSection p__parent = null, RekordboxAnlz p__root = null) : base(p__io)
            {
                m_parent = p__parent;
                m_root = p__root;
                _read();
            }
            private void _read()
            {
                _lenPath = m_io.ReadU4be();
                if (LenPath > 1) {
                    _path = System.Text.Encoding.GetEncoding("utf-16be").GetString(m_io.ReadBytes((LenPath - 2)));
                }
            }
            private uint _lenPath;
            private string _path;
            private RekordboxAnlz m_root;
            private RekordboxAnlz.TaggedSection m_parent;
            public uint LenPath { get { return _lenPath; } }
            public string Path { get { return _path; } }
            public RekordboxAnlz M_Root { get { return m_root; } }
            public RekordboxAnlz.TaggedSection M_Parent { get { return m_parent; } }
        }

        /// <summary>
        /// Stores a waveform preview image suitable for display above
        /// the touch strip for jumping to a track position.
        /// </summary>
        public partial class WavePreviewTag : KaitaiStruct
        {
            public static WavePreviewTag FromFile(string fileName)
            {
                return new WavePreviewTag(new KaitaiStream(fileName));
            }

            public WavePreviewTag(KaitaiStream p__io, RekordboxAnlz.TaggedSection p__parent = null, RekordboxAnlz p__root = null) : base(p__io)
            {
                m_parent = p__parent;
                m_root = p__root;
                _read();
            }
            private void _read()
            {
                _lenData = m_io.ReadU4be();
                __unnamed1 = m_io.ReadU4be();
                if (M_Parent.LenTag > M_Parent.LenHeader) {
                    _data = m_io.ReadBytes(LenData);
                }
            }
            private uint _lenData;
            private uint __unnamed1;
            private byte[] _data;
            private RekordboxAnlz m_root;
            private RekordboxAnlz.TaggedSection m_parent;

            /// <summary>
            /// The length, in bytes, of the preview data itself. This is
            /// slightly redundant because it can be computed from the
            /// length of the tag.
            /// </summary>
            public uint LenData { get { return _lenData; } }
            public uint Unnamed_1 { get { return __unnamed1; } }

            /// <summary>
            /// The actual bytes of the waveform preview.
            /// </summary>
            public byte[] Data { get { return _data; } }
            public RekordboxAnlz M_Root { get { return m_root; } }
            public RekordboxAnlz.TaggedSection M_Parent { get { return m_parent; } }
        }

        /// <summary>
        /// Holds a list of all the beats found within the track, recording
        /// their bar position, the time at which they occur, and the tempo
        /// at that point.
        /// </summary>
        public partial class BeatGridTag : KaitaiStruct
        {
            public static BeatGridTag FromFile(string fileName)
            {
                return new BeatGridTag(new KaitaiStream(fileName));
            }

            public BeatGridTag(KaitaiStream p__io, RekordboxAnlz.TaggedSection p__parent = null, RekordboxAnlz p__root = null) : base(p__io)
            {
                m_parent = p__parent;
                m_root = p__root;
                _read();
            }
            private void _read()
            {
                __unnamed0 = m_io.ReadU4be();
                __unnamed1 = m_io.ReadU4be();
                _numBeats = m_io.ReadU4be();
                _beats = new List<BeatGridBeat>();
                for (var i = 0; i < NumBeats; i++)
                {
                    _beats.Add(new BeatGridBeat(m_io, this, m_root));
                }
            }
            private uint __unnamed0;
            private uint __unnamed1;
            private uint _numBeats;
            private List<BeatGridBeat> _beats;
            private RekordboxAnlz m_root;
            private RekordboxAnlz.TaggedSection m_parent;
            public uint Unnamed_0 { get { return __unnamed0; } }
            public uint Unnamed_1 { get { return __unnamed1; } }

            /// <summary>
            /// The number of beat entries which follow.
            /// </summary>
            public uint NumBeats { get { return _numBeats; } }

            /// <summary>
            /// The entries of the beat grid.
            /// </summary>
            public List<BeatGridBeat> Beats { get { return _beats; } }
            public RekordboxAnlz M_Root { get { return m_root; } }
            public RekordboxAnlz.TaggedSection M_Parent { get { return m_parent; } }
        }

        /// <summary>
        /// Stores the rest of the song structure tag, which can only be
        /// parsed after unmasking.
        /// </summary>
        public partial class SongStructureBody : KaitaiStruct
        {
            public static SongStructureBody FromFile(string fileName)
            {
                return new SongStructureBody(new KaitaiStream(fileName));
            }

            public SongStructureBody(KaitaiStream p__io, RekordboxAnlz.SongStructureTag p__parent = null, RekordboxAnlz p__root = null) : base(p__io)
            {
                m_parent = p__parent;
                m_root = p__root;
                f_bank = false;
                _read();
            }
            private void _read()
            {
                _mood = ((RekordboxAnlz.TrackMood) m_io.ReadU2be());
                __unnamed1 = m_io.ReadBytes(6);
                _endBeat = m_io.ReadU2be();
                __unnamed3 = m_io.ReadBytes(2);
                _rawBank = m_io.ReadU1();
                __unnamed5 = m_io.ReadBytes(1);
                _entries = new List<SongStructureEntry>();
                for (var i = 0; i < M_Parent.LenEntries; i++)
                {
                    _entries.Add(new SongStructureEntry(m_io, this, m_root));
                }
            }
            private bool f_bank;
            private TrackBank _bank;

            /// <summary>
            /// The stylistic bank which can be assigned to the track in rekordbox Lighting mode, if raw_bank has a legal value.
            /// </summary>
            public TrackBank Bank
            {
                get
                {
                    if (f_bank)
                        return _bank;
                    if (RawBank < 9) {
                        _bank = (TrackBank) (((RekordboxAnlz.TrackBank) RawBank));
                    }
                    f_bank = true;
                    return _bank;
                }
            }
            private TrackMood _mood;
            private byte[] __unnamed1;
            private ushort _endBeat;
            private byte[] __unnamed3;
            private byte _rawBank;
            private byte[] __unnamed5;
            private List<SongStructureEntry> _entries;
            private RekordboxAnlz m_root;
            private RekordboxAnlz.SongStructureTag m_parent;

            /// <summary>
            /// The mood which rekordbox assigns the track as a whole during phrase analysis.
            /// </summary>
            public TrackMood Mood { get { return _mood; } }
            public byte[] Unnamed_1 { get { return __unnamed1; } }

            /// <summary>
            /// The beat number at which the last phrase ends. The track may
            /// continue after the last phrase ends. If this is the case, it will
            /// mostly be silence.
            /// </summary>
            public ushort EndBeat { get { return _endBeat; } }
            public byte[] Unnamed_3 { get { return __unnamed3; } }

            /// <summary>
            /// Number identifying a stylistic bank which can be assigned to the track in rekordbox Lighting mode.
            /// </summary>
            public byte RawBank { get { return _rawBank; } }
            public byte[] Unnamed_5 { get { return __unnamed5; } }
            public List<SongStructureEntry> Entries { get { return _entries; } }
            public RekordboxAnlz M_Root { get { return m_root; } }
            public RekordboxAnlz.SongStructureTag M_Parent { get { return m_parent; } }
        }

        /// <summary>
        /// A larger, colorful waveform preview image suitable for display
        /// above the touch strip for jumping to a track position on newer
        /// high-resolution players.
        /// </summary>
        public partial class WaveColorPreviewTag : KaitaiStruct
        {
            public static WaveColorPreviewTag FromFile(string fileName)
            {
                return new WaveColorPreviewTag(new KaitaiStream(fileName));
            }

            public WaveColorPreviewTag(KaitaiStream p__io, RekordboxAnlz.TaggedSection p__parent = null, RekordboxAnlz p__root = null) : base(p__io)
            {
                m_parent = p__parent;
                m_root = p__root;
                _read();
            }
            private void _read()
            {
                _lenEntryBytes = m_io.ReadU4be();
                _lenEntries = m_io.ReadU4be();
                __unnamed2 = m_io.ReadU4be();
                _entries = m_io.ReadBytes((LenEntries * LenEntryBytes));
            }
            private uint _lenEntryBytes;
            private uint _lenEntries;
            private uint __unnamed2;
            private byte[] _entries;
            private RekordboxAnlz m_root;
            private RekordboxAnlz.TaggedSection m_parent;

            /// <summary>
            /// The size of each entry, in bytes. Seems to always be 6.
            /// </summary>
            public uint LenEntryBytes { get { return _lenEntryBytes; } }

            /// <summary>
            /// The number of waveform data points, each of which takes one
            /// byte for each of six channels of information.
            /// </summary>
            public uint LenEntries { get { return _lenEntries; } }
            public uint Unnamed_2 { get { return __unnamed2; } }
            public byte[] Entries { get { return _entries; } }
            public RekordboxAnlz M_Root { get { return m_root; } }
            public RekordboxAnlz.TaggedSection M_Parent { get { return m_parent; } }
        }
        public partial class PhraseHigh : KaitaiStruct
        {
            public static PhraseHigh FromFile(string fileName)
            {
                return new PhraseHigh(new KaitaiStream(fileName));
            }

            public PhraseHigh(KaitaiStream p__io, RekordboxAnlz.SongStructureEntry p__parent = null, RekordboxAnlz p__root = null) : base(p__io)
            {
                m_parent = p__parent;
                m_root = p__root;
                _read();
            }
            private void _read()
            {
                _id = ((RekordboxAnlz.MoodHighPhrase) m_io.ReadU2be());
            }
            private MoodHighPhrase _id;
            private RekordboxAnlz m_root;
            private RekordboxAnlz.SongStructureEntry m_parent;
            public MoodHighPhrase Id { get { return _id; } }
            public RekordboxAnlz M_Root { get { return m_root; } }
            public RekordboxAnlz.SongStructureEntry M_Parent { get { return m_parent; } }
        }

        /// <summary>
        /// A larger waveform image suitable for scrolling along as a track
        /// plays.
        /// </summary>
        public partial class WaveScrollTag : KaitaiStruct
        {
            public static WaveScrollTag FromFile(string fileName)
            {
                return new WaveScrollTag(new KaitaiStream(fileName));
            }

            public WaveScrollTag(KaitaiStream p__io, RekordboxAnlz.TaggedSection p__parent = null, RekordboxAnlz p__root = null) : base(p__io)
            {
                m_parent = p__parent;
                m_root = p__root;
                _read();
            }
            private void _read()
            {
                _lenEntryBytes = m_io.ReadU4be();
                _lenEntries = m_io.ReadU4be();
                __unnamed2 = m_io.ReadU4be();
                _entries = m_io.ReadBytes((LenEntries * LenEntryBytes));
            }
            private uint _lenEntryBytes;
            private uint _lenEntries;
            private uint __unnamed2;
            private byte[] _entries;
            private RekordboxAnlz m_root;
            private RekordboxAnlz.TaggedSection m_parent;

            /// <summary>
            /// The size of each entry, in bytes. Seems to always be 1.
            /// </summary>
            public uint LenEntryBytes { get { return _lenEntryBytes; } }

            /// <summary>
            /// The number of waveform data points, each of which takes one
            /// byte.
            /// </summary>
            public uint LenEntries { get { return _lenEntries; } }
            public uint Unnamed_2 { get { return __unnamed2; } }
            public byte[] Entries { get { return _entries; } }
            public RekordboxAnlz M_Root { get { return m_root; } }
            public RekordboxAnlz.TaggedSection M_Parent { get { return m_parent; } }
        }

        /// <summary>
        /// Stores the song structure, also known as phrases (intro, verse,
        /// bridge, chorus, up, down, outro).
        /// </summary>
        public partial class SongStructureTag : KaitaiStruct
        {
            public static SongStructureTag FromFile(string fileName)
            {
                return new SongStructureTag(new KaitaiStream(fileName));
            }

            public SongStructureTag(KaitaiStream p__io, RekordboxAnlz.TaggedSection p__parent = null, RekordboxAnlz p__root = null) : base(p__io)
            {
                m_parent = p__parent;
                m_root = p__root;
                f_c = false;
                f_mask = false;
                f_rawMood = false;
                f_isMasked = false;
                _read();
            }
            private void _read()
            {
                _lenEntryBytes = m_io.ReadU4be();
                _lenEntries = m_io.ReadU2be();
                __raw__raw_body = m_io.ReadBytesFull();
                __raw_body = m_io.ProcessXor(__raw__raw_body, (IsMasked ? Mask : new byte[] { 0 }));
                var io___raw_body = new KaitaiStream(__raw_body);
                _body = new SongStructureBody(io___raw_body, this, m_root);
            }
            private bool f_c;
            private ushort _c;
            public ushort C
            {
                get
                {
                    if (f_c)
                        return _c;
                    _c = (ushort) (LenEntries);
                    f_c = true;
                    return _c;
                }
            }
            private bool f_mask;
            private byte[] _mask;
            public byte[] Mask
            {
                get
                {
                    if (f_mask)
                        return _mask;
                    _mask = (byte[]) (new byte[] { ((byte) ((203 + C))), ((byte) ((225 + C))), ((byte) ((238 + C))), ((byte) ((250 + C))), ((byte) ((229 + C))), ((byte) ((238 + C))), ((byte) ((173 + C))), ((byte) ((238 + C))), ((byte) ((233 + C))), ((byte) ((210 + C))), ((byte) ((233 + C))), ((byte) ((235 + C))), ((byte) ((225 + C))), ((byte) ((233 + C))), ((byte) ((243 + C))), ((byte) ((232 + C))), ((byte) ((233 + C))), ((byte) ((244 + C))), ((byte) ((225 + C))) });
                    f_mask = true;
                    return _mask;
                }
            }
            private bool f_rawMood;
            private ushort _rawMood;

            /// <summary>
            /// This is a way to tell whether the rest of the tag has been masked. The value is supposed
            /// to range from 1 to 3, but in masked files it will be much larger.
            /// </summary>
            public ushort RawMood
            {
                get
                {
                    if (f_rawMood)
                        return _rawMood;
                    long _pos = m_io.Pos;
                    m_io.Seek(6);
                    _rawMood = m_io.ReadU2be();
                    m_io.Seek(_pos);
                    f_rawMood = true;
                    return _rawMood;
                }
            }
            private bool f_isMasked;
            private bool _isMasked;
            public bool IsMasked
            {
                get
                {
                    if (f_isMasked)
                        return _isMasked;
                    _isMasked = (bool) (RawMood > 20);
                    f_isMasked = true;
                    return _isMasked;
                }
            }
            private uint _lenEntryBytes;
            private ushort _lenEntries;
            private SongStructureBody _body;
            private RekordboxAnlz m_root;
            private RekordboxAnlz.TaggedSection m_parent;
            private byte[] __raw_body;
            private byte[] __raw__raw_body;

            /// <summary>
            /// The size of each entry, in bytes. Seems to always be 24.
            /// </summary>
            public uint LenEntryBytes { get { return _lenEntryBytes; } }

            /// <summary>
            /// The number of phrases.
            /// </summary>
            public ushort LenEntries { get { return _lenEntries; } }

            /// <summary>
            /// The rest of the tag, which needs to be unmasked before it
            /// can be parsed.
            /// </summary>
            public SongStructureBody Body { get { return _body; } }
            public RekordboxAnlz M_Root { get { return m_root; } }
            public RekordboxAnlz.TaggedSection M_Parent { get { return m_parent; } }
            public byte[] M_RawBody { get { return __raw_body; } }
            public byte[] M_RawM_RawBody { get { return __raw__raw_body; } }
        }

        /// <summary>
        /// A cue extended list entry. Can either describe a memory cue or a
        /// loop.
        /// </summary>
        public partial class CueExtendedEntry : KaitaiStruct
        {
            public static CueExtendedEntry FromFile(string fileName)
            {
                return new CueExtendedEntry(new KaitaiStream(fileName));
            }

            public CueExtendedEntry(KaitaiStream p__io, RekordboxAnlz.CueExtendedTag p__parent = null, RekordboxAnlz p__root = null) : base(p__io)
            {
                m_parent = p__parent;
                m_root = p__root;
                _read();
            }
            private void _read()
            {
                _magic = m_io.ReadBytes(4);
                if (!((KaitaiStream.ByteArrayCompare(Magic, new byte[] { 80, 67, 80, 50 }) == 0)))
                {
                    throw new ValidationNotEqualError(new byte[] { 80, 67, 80, 50 }, Magic, M_Io, "/types/cue_extended_entry/seq/0");
                }
                _lenHeader = m_io.ReadU4be();
                _lenEntry = m_io.ReadU4be();
                _hotCue = m_io.ReadU4be();
                _type = ((RekordboxAnlz.CueEntryType) m_io.ReadU1());
                __unnamed5 = m_io.ReadBytes(3);
                _time = m_io.ReadU4be();
                _loopTime = m_io.ReadU4be();
                _colorId = m_io.ReadU1();
                __unnamed9 = m_io.ReadBytes(7);
                _loopNumerator = m_io.ReadU2be();
                _loopDenominator = m_io.ReadU2be();
                if (LenEntry > 43) {
                    _lenComment = m_io.ReadU4be();
                }
                if (LenEntry > 43) {
                    _comment = System.Text.Encoding.GetEncoding("utf-16be").GetString(m_io.ReadBytes((long) LenComment));
                }
                if ((LenEntry - LenComment) > 44) {
                    _colorCode = m_io.ReadU1();
                }
                if ((LenEntry - LenComment) > 45) {
                    _colorRed = m_io.ReadU1();
                }
                if ((LenEntry - LenComment) > 46) {
                    _colorGreen = m_io.ReadU1();
                }
                if ((LenEntry - LenComment) > 47) {
                    _colorBlue = m_io.ReadU1();
                }
                if ((LenEntry - LenComment) > 48) {
                    __unnamed18 = m_io.ReadBytes((long) ((LenEntry - 48) - LenComment));
                }
            }
            private byte[] _magic;
            private uint _lenHeader;
            private uint _lenEntry;
            private uint _hotCue;
            private CueEntryType _type;
            private byte[] __unnamed5;
            private uint _time;
            private uint _loopTime;
            private byte _colorId;
            private byte[] __unnamed9;
            private ushort _loopNumerator;
            private ushort _loopDenominator;
            private uint? _lenComment;
            private string _comment;
            private byte? _colorCode;
            private byte? _colorRed;
            private byte? _colorGreen;
            private byte? _colorBlue;
            private byte[] __unnamed18;
            private RekordboxAnlz m_root;
            private RekordboxAnlz.CueExtendedTag m_parent;

            /// <summary>
            /// Identifies this as an extended cue list entry (cue point).
            /// </summary>
            public byte[] Magic { get { return _magic; } }
            public uint LenHeader { get { return _lenHeader; } }
            public uint LenEntry { get { return _lenEntry; } }

            /// <summary>
            /// If zero, this is an ordinary memory cue, otherwise this a
            /// hot cue with the specified number.
            /// </summary>
            public uint HotCue { get { return _hotCue; } }

            /// <summary>
            /// Indicates whether this is a regular cue point or a loop.
            /// </summary>
            public CueEntryType Type { get { return _type; } }
            public byte[] Unnamed_5 { get { return __unnamed5; } }

            /// <summary>
            /// The position, in milliseconds, at which the cue point lies
            /// in the track.
            /// </summary>
            public uint Time { get { return _time; } }

            /// <summary>
            /// The position, in milliseconds, at which the player loops
            /// back to the cue time if this is a loop.
            /// </summary>
            public uint LoopTime { get { return _loopTime; } }

            /// <summary>
            /// References a row in the colors table if this is a memory cue or loop
            /// and has been assigned a color.
            /// </summary>
            public byte ColorId { get { return _colorId; } }
            public byte[] Unnamed_9 { get { return __unnamed9; } }

            /// <summary>
            /// The numerator of the loop length in beats. 
            /// Zero if the loop is not quantized.
            /// </summary>
            public ushort LoopNumerator { get { return _loopNumerator; } }

            /// <summary>
            /// The denominator of the loop length in beats. 
            /// Zero if the loop is not quantized.
            /// </summary>
            public ushort LoopDenominator { get { return _loopDenominator; } }
            public uint? LenComment { get { return _lenComment; } }

            /// <summary>
            /// The comment assigned to this cue by the DJ, if any, with a trailing NUL.
            /// </summary>
            public string Comment { get { return _comment; } }

            /// <summary>
            /// A lookup value for a color table? We use this to index to the hot cue colors shown in rekordbox.
            /// </summary>
            public byte? ColorCode { get { return _colorCode; } }

            /// <summary>
            /// The red component of the hot cue color to be displayed.
            /// </summary>
            public byte? ColorRed { get { return _colorRed; } }

            /// <summary>
            /// The green component of the hot cue color to be displayed.
            /// </summary>
            public byte? ColorGreen { get { return _colorGreen; } }

            /// <summary>
            /// The blue component of the hot cue color to be displayed.
            /// </summary>
            public byte? ColorBlue { get { return _colorBlue; } }
            public byte[] Unnamed_18 { get { return __unnamed18; } }
            public RekordboxAnlz M_Root { get { return m_root; } }
            public RekordboxAnlz.CueExtendedTag M_Parent { get { return m_parent; } }
        }

        /// <summary>
        /// Stores an index allowing rapid seeking to particular times
        /// within a variable-bitrate audio file.
        /// </summary>
        public partial class VbrTag : KaitaiStruct
        {
            public static VbrTag FromFile(string fileName)
            {
                return new VbrTag(new KaitaiStream(fileName));
            }

            public VbrTag(KaitaiStream p__io, RekordboxAnlz.TaggedSection p__parent = null, RekordboxAnlz p__root = null) : base(p__io)
            {
                m_parent = p__parent;
                m_root = p__root;
                _read();
            }
            private void _read()
            {
                __unnamed0 = m_io.ReadU4be();
                _index = new List<uint>();
                for (var i = 0; i < 400; i++)
                {
                    _index.Add(m_io.ReadU4be());
                }
            }
            private uint __unnamed0;
            private List<uint> _index;
            private RekordboxAnlz m_root;
            private RekordboxAnlz.TaggedSection m_parent;
            public uint Unnamed_0 { get { return __unnamed0; } }
            public List<uint> Index { get { return _index; } }
            public RekordboxAnlz M_Root { get { return m_root; } }
            public RekordboxAnlz.TaggedSection M_Parent { get { return m_parent; } }
        }

        /// <summary>
        /// A song structure entry, represents a single phrase.
        /// </summary>
        public partial class SongStructureEntry : KaitaiStruct
        {
            public static SongStructureEntry FromFile(string fileName)
            {
                return new SongStructureEntry(new KaitaiStream(fileName));
            }

            public SongStructureEntry(KaitaiStream p__io, RekordboxAnlz.SongStructureBody p__parent = null, RekordboxAnlz p__root = null) : base(p__io)
            {
                m_parent = p__parent;
                m_root = p__root;
                _read();
            }
            private void _read()
            {
                _index = m_io.ReadU2be();
                _beat = m_io.ReadU2be();
                switch (M_Parent.Mood) {
                case RekordboxAnlz.TrackMood.High: {
                    _kind = new PhraseHigh(m_io, this, m_root);
                    break;
                }
                case RekordboxAnlz.TrackMood.Mid: {
                    _kind = new PhraseMid(m_io, this, m_root);
                    break;
                }
                case RekordboxAnlz.TrackMood.Low: {
                    _kind = new PhraseLow(m_io, this, m_root);
                    break;
                }
                default: {
                    _kind = new PhraseMid(m_io, this, m_root);
                    break;
                }
                }
                __unnamed3 = m_io.ReadBytes(1);
                _k1 = m_io.ReadU1();
                __unnamed5 = m_io.ReadBytes(1);
                _k2 = m_io.ReadU1();
                __unnamed7 = m_io.ReadBytes(1);
                _b = m_io.ReadU1();
                _beat2 = m_io.ReadU2be();
                _beat3 = m_io.ReadU2be();
                _beat4 = m_io.ReadU2be();
                __unnamed12 = m_io.ReadBytes(1);
                _k3 = m_io.ReadU1();
                __unnamed14 = m_io.ReadBytes(1);
                _fill = m_io.ReadU1();
                _beatFill = m_io.ReadU2be();
            }
            private ushort _index;
            private ushort _beat;
            private KaitaiStruct _kind;
            private byte[] __unnamed3;
            private byte _k1;
            private byte[] __unnamed5;
            private byte _k2;
            private byte[] __unnamed7;
            private byte _b;
            private ushort _beat2;
            private ushort _beat3;
            private ushort _beat4;
            private byte[] __unnamed12;
            private byte _k3;
            private byte[] __unnamed14;
            private byte _fill;
            private ushort _beatFill;
            private RekordboxAnlz m_root;
            private RekordboxAnlz.SongStructureBody m_parent;

            /// <summary>
            /// The absolute number of the phrase, starting at one.
            /// </summary>
            public ushort Index { get { return _index; } }

            /// <summary>
            /// The beat number at which the phrase starts.
            /// </summary>
            public ushort Beat { get { return _beat; } }

            /// <summary>
            /// The kind of phrase as displayed in rekordbox.
            /// </summary>
            public KaitaiStruct Kind { get { return _kind; } }
            public byte[] Unnamed_3 { get { return __unnamed3; } }

            /// <summary>
            /// One of three flags that identify phrase kind variants in high-mood tracks.
            /// </summary>
            public byte K1 { get { return _k1; } }
            public byte[] Unnamed_5 { get { return __unnamed5; } }

            /// <summary>
            /// One of three flags that identify phrase kind variants in high-mood tracks.
            /// </summary>
            public byte K2 { get { return _k2; } }
            public byte[] Unnamed_7 { get { return __unnamed7; } }

            /// <summary>
            /// Flags how many more beat numbers are in a high-mood &quot;Up 3&quot; phrase.
            /// </summary>
            public byte B { get { return _b; } }

            /// <summary>
            /// Extra beat number (falling within phrase) always present in high-mood &quot;Up 3&quot; phrases.
            /// </summary>
            public ushort Beat2 { get { return _beat2; } }

            /// <summary>
            /// Extra beat number (falling within phrase, larger than beat2)
            /// present in high-mood &quot;Up 3&quot; phrases when b has value 1.
            /// </summary>
            public ushort Beat3 { get { return _beat3; } }

            /// <summary>
            /// Extra beat number (falling within phrase, larger than beat3)
            /// present in high-mood &quot;Up 3&quot; phrases when b has value 1.
            /// </summary>
            public ushort Beat4 { get { return _beat4; } }
            public byte[] Unnamed_12 { get { return __unnamed12; } }

            /// <summary>
            /// One of three flags that identify phrase kind variants in high-mood tracks.
            /// </summary>
            public byte K3 { get { return _k3; } }
            public byte[] Unnamed_14 { get { return __unnamed14; } }

            /// <summary>
            /// If nonzero, fill-in is present at end of phrase.
            /// </summary>
            public byte Fill { get { return _fill; } }

            /// <summary>
            /// The beat number at which fill-in starts.
            /// </summary>
            public ushort BeatFill { get { return _beatFill; } }
            public RekordboxAnlz M_Root { get { return m_root; } }
            public RekordboxAnlz.SongStructureBody M_Parent { get { return m_parent; } }
        }

        /// <summary>
        /// A cue list entry. Can either represent a memory cue or a loop.
        /// </summary>
        public partial class CueEntry : KaitaiStruct
        {
            public static CueEntry FromFile(string fileName)
            {
                return new CueEntry(new KaitaiStream(fileName));
            }

            public CueEntry(KaitaiStream p__io, RekordboxAnlz.CueTag p__parent = null, RekordboxAnlz p__root = null) : base(p__io)
            {
                m_parent = p__parent;
                m_root = p__root;
                _read();
            }
            private void _read()
            {
                _magic = m_io.ReadBytes(4);
                if (!((KaitaiStream.ByteArrayCompare(Magic, new byte[] { 80, 67, 80, 84 }) == 0)))
                {
                    throw new ValidationNotEqualError(new byte[] { 80, 67, 80, 84 }, Magic, M_Io, "/types/cue_entry/seq/0");
                }
                _lenHeader = m_io.ReadU4be();
                _lenEntry = m_io.ReadU4be();
                _hotCue = m_io.ReadU4be();
                _status = ((RekordboxAnlz.CueEntryStatus) m_io.ReadU4be());
                __unnamed5 = m_io.ReadU4be();
                _orderFirst = m_io.ReadU2be();
                _orderLast = m_io.ReadU2be();
                _type = ((RekordboxAnlz.CueEntryType) m_io.ReadU1());
                __unnamed9 = m_io.ReadBytes(3);
                _time = m_io.ReadU4be();
                _loopTime = m_io.ReadU4be();
                __unnamed12 = m_io.ReadBytes(16);
            }
            private byte[] _magic;
            private uint _lenHeader;
            private uint _lenEntry;
            private uint _hotCue;
            private CueEntryStatus _status;
            private uint __unnamed5;
            private ushort _orderFirst;
            private ushort _orderLast;
            private CueEntryType _type;
            private byte[] __unnamed9;
            private uint _time;
            private uint _loopTime;
            private byte[] __unnamed12;
            private RekordboxAnlz m_root;
            private RekordboxAnlz.CueTag m_parent;

            /// <summary>
            /// Identifies this as a cue list entry (cue point).
            /// </summary>
            public byte[] Magic { get { return _magic; } }
            public uint LenHeader { get { return _lenHeader; } }
            public uint LenEntry { get { return _lenEntry; } }

            /// <summary>
            /// If zero, this is an ordinary memory cue, otherwise this a
            /// hot cue with the specified number.
            /// </summary>
            public uint HotCue { get { return _hotCue; } }

            /// <summary>
            /// Indicates if this is an active loop.
            /// </summary>
            public CueEntryStatus Status { get { return _status; } }
            public uint Unnamed_5 { get { return __unnamed5; } }

            /// <summary>
            /// @flesniak says: &quot;0xffff for first cue, 0,1,3 for next&quot;
            /// </summary>
            public ushort OrderFirst { get { return _orderFirst; } }

            /// <summary>
            /// @flesniak says: &quot;1,2,3 for first, second, third cue, 0xffff for last&quot;
            /// </summary>
            public ushort OrderLast { get { return _orderLast; } }

            /// <summary>
            /// Indicates whether this is a memory cue or a loop.
            /// </summary>
            public CueEntryType Type { get { return _type; } }
            public byte[] Unnamed_9 { get { return __unnamed9; } }

            /// <summary>
            /// The position, in milliseconds, at which the cue point lies
            /// in the track.
            /// </summary>
            public uint Time { get { return _time; } }

            /// <summary>
            /// The position, in milliseconds, at which the player loops
            /// back to the cue time if this is a loop.
            /// </summary>
            public uint LoopTime { get { return _loopTime; } }
            public byte[] Unnamed_12 { get { return __unnamed12; } }
            public RekordboxAnlz M_Root { get { return m_root; } }
            public RekordboxAnlz.CueTag M_Parent { get { return m_parent; } }
        }

        /// <summary>
        /// Describes an individual beat in a beat grid.
        /// </summary>
        public partial class BeatGridBeat : KaitaiStruct
        {
            public static BeatGridBeat FromFile(string fileName)
            {
                return new BeatGridBeat(new KaitaiStream(fileName));
            }

            public BeatGridBeat(KaitaiStream p__io, RekordboxAnlz.BeatGridTag p__parent = null, RekordboxAnlz p__root = null) : base(p__io)
            {
                m_parent = p__parent;
                m_root = p__root;
                _read();
            }
            private void _read()
            {
                _beatNumber = m_io.ReadU2be();
                _tempo = m_io.ReadU2be();
                _time = m_io.ReadU4be();
            }
            private ushort _beatNumber;
            private ushort _tempo;
            private uint _time;
            private RekordboxAnlz m_root;
            private RekordboxAnlz.BeatGridTag m_parent;

            /// <summary>
            /// The position of the beat within its musical bar, where beat 1
            /// is the down beat.
            /// </summary>
            public ushort BeatNumber { get { return _beatNumber; } }

            /// <summary>
            /// The tempo at the time of this beat, in beats per minute,
            /// multiplied by 100.
            /// </summary>
            public ushort Tempo { get { return _tempo; } }

            /// <summary>
            /// The time, in milliseconds, at which this beat occurs when
            /// the track is played at normal (100%) pitch.
            /// </summary>
            public uint Time { get { return _time; } }
            public RekordboxAnlz M_Root { get { return m_root; } }
            public RekordboxAnlz.BeatGridTag M_Parent { get { return m_parent; } }
        }

        /// <summary>
        /// A variation of cue_tag which was introduced with the nxs2 line,
        /// and adds descriptive names. (Still comes in two forms, either
        /// holding memory cues and loop points, or holding hot cues and
        /// loop points.) Also includes hot cues D through H and color assignment.
        /// </summary>
        public partial class CueExtendedTag : KaitaiStruct
        {
            public static CueExtendedTag FromFile(string fileName)
            {
                return new CueExtendedTag(new KaitaiStream(fileName));
            }

            public CueExtendedTag(KaitaiStream p__io, RekordboxAnlz.TaggedSection p__parent = null, RekordboxAnlz p__root = null) : base(p__io)
            {
                m_parent = p__parent;
                m_root = p__root;
                _read();
            }
            private void _read()
            {
                _type = ((RekordboxAnlz.CueListType) m_io.ReadU4be());
                _numCues = m_io.ReadU2be();
                __unnamed2 = m_io.ReadBytes(2);
                _cues = new List<CueExtendedEntry>();
                for (var i = 0; i < NumCues; i++)
                {
                    _cues.Add(new CueExtendedEntry(m_io, this, m_root));
                }
            }
            private CueListType _type;
            private ushort _numCues;
            private byte[] __unnamed2;
            private List<CueExtendedEntry> _cues;
            private RekordboxAnlz m_root;
            private RekordboxAnlz.TaggedSection m_parent;

            /// <summary>
            /// Identifies whether this tag stores ordinary or hot cues.
            /// </summary>
            public CueListType Type { get { return _type; } }

            /// <summary>
            /// The length of the cue comment list.
            /// </summary>
            public ushort NumCues { get { return _numCues; } }
            public byte[] Unnamed_2 { get { return __unnamed2; } }
            public List<CueExtendedEntry> Cues { get { return _cues; } }
            public RekordboxAnlz M_Root { get { return m_root; } }
            public RekordboxAnlz.TaggedSection M_Parent { get { return m_parent; } }
        }
        public partial class PhraseLow : KaitaiStruct
        {
            public static PhraseLow FromFile(string fileName)
            {
                return new PhraseLow(new KaitaiStream(fileName));
            }

            public PhraseLow(KaitaiStream p__io, RekordboxAnlz.SongStructureEntry p__parent = null, RekordboxAnlz p__root = null) : base(p__io)
            {
                m_parent = p__parent;
                m_root = p__root;
                _read();
            }
            private void _read()
            {
                _id = ((RekordboxAnlz.MoodLowPhrase) m_io.ReadU2be());
            }
            private MoodLowPhrase _id;
            private RekordboxAnlz m_root;
            private RekordboxAnlz.SongStructureEntry m_parent;
            public MoodLowPhrase Id { get { return _id; } }
            public RekordboxAnlz M_Root { get { return m_root; } }
            public RekordboxAnlz.SongStructureEntry M_Parent { get { return m_parent; } }
        }
        public partial class UnknownTag : KaitaiStruct
        {
            public static UnknownTag FromFile(string fileName)
            {
                return new UnknownTag(new KaitaiStream(fileName));
            }

            public UnknownTag(KaitaiStream p__io, RekordboxAnlz.TaggedSection p__parent = null, RekordboxAnlz p__root = null) : base(p__io)
            {
                m_parent = p__parent;
                m_root = p__root;
                _read();
            }
            private void _read()
            {
            }
            private RekordboxAnlz m_root;
            private RekordboxAnlz.TaggedSection m_parent;
            public RekordboxAnlz M_Root { get { return m_root; } }
            public RekordboxAnlz.TaggedSection M_Parent { get { return m_parent; } }
        }

        /// <summary>
        /// A type-tagged file section, identified by a four-byte magic
        /// sequence, with a header specifying its length, and whose payload
        /// is determined by the type tag.
        /// </summary>
        public partial class TaggedSection : KaitaiStruct
        {
            public static TaggedSection FromFile(string fileName)
            {
                return new TaggedSection(new KaitaiStream(fileName));
            }

            public TaggedSection(KaitaiStream p__io, RekordboxAnlz p__parent = null, RekordboxAnlz p__root = null) : base(p__io)
            {
                m_parent = p__parent;
                m_root = p__root;
                _read();
            }
            private void _read()
            {
                _fourcc = ((RekordboxAnlz.SectionTags) m_io.ReadS4be());
                _lenHeader = m_io.ReadU4be();
                _lenTag = m_io.ReadU4be();
                switch (Fourcc) {
                case RekordboxAnlz.SectionTags.WaveColorScroll: {
                    __raw_body = m_io.ReadBytes((LenTag - 12));
                    var io___raw_body = new KaitaiStream(__raw_body);
                    _body = new WaveColorScrollTag(io___raw_body, this, m_root);
                    break;
                }
                case RekordboxAnlz.SectionTags.WaveScroll: {
                    __raw_body = m_io.ReadBytes((LenTag - 12));
                    var io___raw_body = new KaitaiStream(__raw_body);
                    _body = new WaveScrollTag(io___raw_body, this, m_root);
                    break;
                }
                case RekordboxAnlz.SectionTags.Vbr: {
                    __raw_body = m_io.ReadBytes((LenTag - 12));
                    var io___raw_body = new KaitaiStream(__raw_body);
                    _body = new VbrTag(io___raw_body, this, m_root);
                    break;
                }
                case RekordboxAnlz.SectionTags.Wave3bandScroll: {
                    __raw_body = m_io.ReadBytes((LenTag - 12));
                    var io___raw_body = new KaitaiStream(__raw_body);
                    _body = new Wave3bandScrollTag(io___raw_body, this, m_root);
                    break;
                }
                case RekordboxAnlz.SectionTags.Cues2: {
                    __raw_body = m_io.ReadBytes((LenTag - 12));
                    var io___raw_body = new KaitaiStream(__raw_body);
                    _body = new CueExtendedTag(io___raw_body, this, m_root);
                    break;
                }
                case RekordboxAnlz.SectionTags.Cues: {
                    __raw_body = m_io.ReadBytes((LenTag - 12));
                    var io___raw_body = new KaitaiStream(__raw_body);
                    _body = new CueTag(io___raw_body, this, m_root);
                    break;
                }
                case RekordboxAnlz.SectionTags.SongStructure: {
                    __raw_body = m_io.ReadBytes((LenTag - 12));
                    var io___raw_body = new KaitaiStream(__raw_body);
                    _body = new SongStructureTag(io___raw_body, this, m_root);
                    break;
                }
                case RekordboxAnlz.SectionTags.BeatGrid: {
                    __raw_body = m_io.ReadBytes((LenTag - 12));
                    var io___raw_body = new KaitaiStream(__raw_body);
                    _body = new BeatGridTag(io___raw_body, this, m_root);
                    break;
                }
                case RekordboxAnlz.SectionTags.WavePreview: {
                    __raw_body = m_io.ReadBytes((LenTag - 12));
                    var io___raw_body = new KaitaiStream(__raw_body);
                    _body = new WavePreviewTag(io___raw_body, this, m_root);
                    break;
                }
                case RekordboxAnlz.SectionTags.Wave3bandPreview: {
                    __raw_body = m_io.ReadBytes((LenTag - 12));
                    var io___raw_body = new KaitaiStream(__raw_body);
                    _body = new Wave3bandPreviewTag(io___raw_body, this, m_root);
                    break;
                }
                case RekordboxAnlz.SectionTags.WaveColorPreview: {
                    __raw_body = m_io.ReadBytes((LenTag - 12));
                    var io___raw_body = new KaitaiStream(__raw_body);
                    _body = new WaveColorPreviewTag(io___raw_body, this, m_root);
                    break;
                }
                case RekordboxAnlz.SectionTags.Path: {
                    __raw_body = m_io.ReadBytes((LenTag - 12));
                    var io___raw_body = new KaitaiStream(__raw_body);
                    _body = new PathTag(io___raw_body, this, m_root);
                    break;
                }
                case RekordboxAnlz.SectionTags.WaveTiny: {
                    __raw_body = m_io.ReadBytes((LenTag - 12));
                    var io___raw_body = new KaitaiStream(__raw_body);
                    _body = new WavePreviewTag(io___raw_body, this, m_root);
                    break;
                }
                default: {
                    __raw_body = m_io.ReadBytes((LenTag - 12));
                    var io___raw_body = new KaitaiStream(__raw_body);
                    _body = new UnknownTag(io___raw_body, this, m_root);
                    break;
                }
                }
            }
            private SectionTags _fourcc;
            private uint _lenHeader;
            private uint _lenTag;
            private KaitaiStruct _body;
            private RekordboxAnlz m_root;
            private RekordboxAnlz m_parent;
            private byte[] __raw_body;

            /// <summary>
            /// A tag value indicating what kind of section this is.
            /// </summary>
            public SectionTags Fourcc { get { return _fourcc; } }

            /// <summary>
            /// The size, in bytes, of the header portion of the tag.
            /// </summary>
            public uint LenHeader { get { return _lenHeader; } }

            /// <summary>
            /// The size, in bytes, of this entire tag, counting the header.
            /// </summary>
            public uint LenTag { get { return _lenTag; } }
            public KaitaiStruct Body { get { return _body; } }
            public RekordboxAnlz M_Root { get { return m_root; } }
            public RekordboxAnlz M_Parent { get { return m_parent; } }
            public byte[] M_RawBody { get { return __raw_body; } }
        }

        /// <summary>
        /// The minimalist CDJ-3000 waveform preview image suitable for display
        /// above the touch strip for jumping to a track position.
        /// </summary>
        public partial class Wave3bandPreviewTag : KaitaiStruct
        {
            public static Wave3bandPreviewTag FromFile(string fileName)
            {
                return new Wave3bandPreviewTag(new KaitaiStream(fileName));
            }

            public Wave3bandPreviewTag(KaitaiStream p__io, RekordboxAnlz.TaggedSection p__parent = null, RekordboxAnlz p__root = null) : base(p__io)
            {
                m_parent = p__parent;
                m_root = p__root;
                _read();
            }
            private void _read()
            {
                _lenEntryBytes = m_io.ReadU4be();
                _lenEntries = m_io.ReadU4be();
                _entries = m_io.ReadBytes((LenEntries * LenEntryBytes));
            }
            private uint _lenEntryBytes;
            private uint _lenEntries;
            private byte[] _entries;
            private RekordboxAnlz m_root;
            private RekordboxAnlz.TaggedSection m_parent;

            /// <summary>
            /// The size of each entry, in bytes. Seems to always be 3.
            /// </summary>
            public uint LenEntryBytes { get { return _lenEntryBytes; } }

            /// <summary>
            /// The number of waveform data points, each of which takes one
            /// byte for each of six channels of information.
            /// </summary>
            public uint LenEntries { get { return _lenEntries; } }
            public byte[] Entries { get { return _entries; } }
            public RekordboxAnlz M_Root { get { return m_root; } }
            public RekordboxAnlz.TaggedSection M_Parent { get { return m_parent; } }
        }

        /// <summary>
        /// A larger, colorful waveform image suitable for scrolling along
        /// as a track plays on newer high-resolution hardware. Also
        /// contains a higher-resolution blue/white waveform.
        /// </summary>
        public partial class WaveColorScrollTag : KaitaiStruct
        {
            public static WaveColorScrollTag FromFile(string fileName)
            {
                return new WaveColorScrollTag(new KaitaiStream(fileName));
            }

            public WaveColorScrollTag(KaitaiStream p__io, RekordboxAnlz.TaggedSection p__parent = null, RekordboxAnlz p__root = null) : base(p__io)
            {
                m_parent = p__parent;
                m_root = p__root;
                _read();
            }
            private void _read()
            {
                _lenEntryBytes = m_io.ReadU4be();
                _lenEntries = m_io.ReadU4be();
                __unnamed2 = m_io.ReadU4be();
                _entries = m_io.ReadBytes((LenEntries * LenEntryBytes));
            }
            private uint _lenEntryBytes;
            private uint _lenEntries;
            private uint __unnamed2;
            private byte[] _entries;
            private RekordboxAnlz m_root;
            private RekordboxAnlz.TaggedSection m_parent;

            /// <summary>
            /// The size of each entry, in bytes. Seems to always be 2.
            /// </summary>
            public uint LenEntryBytes { get { return _lenEntryBytes; } }

            /// <summary>
            /// The number of columns of waveform data (this matches the
            /// non-color waveform length.
            /// </summary>
            public uint LenEntries { get { return _lenEntries; } }
            public uint Unnamed_2 { get { return __unnamed2; } }
            public byte[] Entries { get { return _entries; } }
            public RekordboxAnlz M_Root { get { return m_root; } }
            public RekordboxAnlz.TaggedSection M_Parent { get { return m_parent; } }
        }

        /// <summary>
        /// Stores either a list of ordinary memory cues and loop points, or
        /// a list of hot cues and loop points.
        /// </summary>
        public partial class CueTag : KaitaiStruct
        {
            public static CueTag FromFile(string fileName)
            {
                return new CueTag(new KaitaiStream(fileName));
            }

            public CueTag(KaitaiStream p__io, RekordboxAnlz.TaggedSection p__parent = null, RekordboxAnlz p__root = null) : base(p__io)
            {
                m_parent = p__parent;
                m_root = p__root;
                _read();
            }
            private void _read()
            {
                _type = ((RekordboxAnlz.CueListType) m_io.ReadU4be());
                __unnamed1 = m_io.ReadBytes(2);
                _numCues = m_io.ReadU2be();
                _memoryCount = m_io.ReadU4be();
                _cues = new List<CueEntry>();
                for (var i = 0; i < NumCues; i++)
                {
                    _cues.Add(new CueEntry(m_io, this, m_root));
                }
            }
            private CueListType _type;
            private byte[] __unnamed1;
            private ushort _numCues;
            private uint _memoryCount;
            private List<CueEntry> _cues;
            private RekordboxAnlz m_root;
            private RekordboxAnlz.TaggedSection m_parent;

            /// <summary>
            /// Identifies whether this tag stores ordinary or hot cues.
            /// </summary>
            public CueListType Type { get { return _type; } }
            public byte[] Unnamed_1 { get { return __unnamed1; } }

            /// <summary>
            /// The length of the cue list.
            /// </summary>
            public ushort NumCues { get { return _numCues; } }

            /// <summary>
            /// Unsure what this means.
            /// </summary>
            public uint MemoryCount { get { return _memoryCount; } }
            public List<CueEntry> Cues { get { return _cues; } }
            public RekordboxAnlz M_Root { get { return m_root; } }
            public RekordboxAnlz.TaggedSection M_Parent { get { return m_parent; } }
        }
        private byte[] _magic;
        private uint _lenHeader;
        private uint _lenFile;
        private byte[] __unnamed3;
        private List<TaggedSection> _sections;
        private RekordboxAnlz m_root;
        private KaitaiStruct m_parent;

        /// <summary>
        /// Identifies this as an analysis file.
        /// </summary>
        public byte[] Magic { get { return _magic; } }

        /// <summary>
        /// The number of bytes of this header section.
        /// </summary>
        public uint LenHeader { get { return _lenHeader; } }

        /// <summary>
        /// The number of bytes in the entire file.
        /// </summary>
        public uint LenFile { get { return _lenFile; } }
        public byte[] Unnamed_3 { get { return __unnamed3; } }

        /// <summary>
        /// The remainder of the file is a sequence of type-tagged sections,
        /// identified by a four-byte magic sequence.
        /// </summary>
        public List<TaggedSection> Sections { get { return _sections; } }
        public RekordboxAnlz M_Root { get { return m_root; } }
        public KaitaiStruct M_Parent { get { return m_parent; } }
    }
}
