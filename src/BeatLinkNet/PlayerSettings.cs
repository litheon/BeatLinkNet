namespace BeatLink;

/// <summary>
/// Encapsulates the "My Settings" configuration parameters that can be applied to a player over
/// the network. Not all players support all features. A simple mutable class (and therefore not
/// thread-safe). The values will be sent using the Load Settings command in a later phase (the
/// Java <c>VirtualCdj.sendLoadSettingsCommand()</c>).
/// </summary>
public class PlayerSettings
{
    /// <summary>
    /// Create an instance with default settings values.
    /// </summary>
    public PlayerSettings()
    {
        // Nothing to do.
    }

    /// <summary>
    /// A standard on/off choice which is used in many settings.
    /// </summary>
    public sealed class Toggle
    {
        /// <summary>The feature is turned off.</summary>
        public static readonly Toggle Off = new("Off", 0x80);

        /// <summary>The feature is turned on.</summary>
        public static readonly Toggle On = new("On", 0x81);

        /// <summary>The value displayed in the rekordbox interface for each state.</summary>
        public string DisplayValue { get; }

        /// <summary>The value in the Load Settings packet which corresponds to the chosen state.</summary>
        public byte ProtocolValue { get; }

        private Toggle(string displayValue, byte protocolValue)
        {
            DisplayValue = displayValue;
            ProtocolValue = protocolValue;
        }

        /// <summary>All the toggle values.</summary>
        public static IReadOnlyList<Toggle> Values { get; } = [Off, On];

        /// <inheritdoc/>
        public override string ToString() => DisplayValue;
    }

    /// <summary>
    /// Controls whether the player displays whether it is currently on the air (when paired with a
    /// mixer that supports this feature).
    /// </summary>
    public Toggle OnAirDisplay { get; set; } = Toggle.On;

    /// <summary>
    /// The brightness options for the player's LCD screen.
    /// </summary>
    public sealed class LcdBrightness
    {
        /// <summary>The dimmest LCD brightness setting (1).</summary>
        public static readonly LcdBrightness Dimmest = new(1, 0x81);

        /// <summary>A dim LCD brightness setting (2).</summary>
        public static readonly LcdBrightness Dim = new(2, 0x82);

        /// <summary>A medium LCD brightness setting (3).</summary>
        public static readonly LcdBrightness Medium = new(3, 0x83);

        /// <summary>A bright LCD brightness setting (4).</summary>
        public static readonly LcdBrightness Bright = new(4, 0x84);

        /// <summary>The brightest LCD brightness setting (5).</summary>
        public static readonly LcdBrightness Brightest = new(5, 0x85);

        /// <summary>The value displayed in the rekordbox interface for each brightness setting.</summary>
        public int DisplayValue { get; }

        /// <summary>The value sent in the Load Settings packet which establishes each brightness setting.</summary>
        public byte ProtocolValue { get; }

        private LcdBrightness(int displayValue, byte protocolValue)
        {
            DisplayValue = displayValue;
            ProtocolValue = protocolValue;
        }

        /// <summary>All the LCD brightness values.</summary>
        public static IReadOnlyList<LcdBrightness> Values { get; } = [Dimmest, Dim, Medium, Bright, Brightest];

        /// <inheritdoc/>
        public override string ToString() => DisplayValue.ToString();
    }

    /// <summary>
    /// Controls the brightness of the main display.
    /// </summary>
    public LcdBrightness MainLcdBrightness { get; set; } = LcdBrightness.Medium;

    /// <summary>
    /// Controls whether quantization is enabled.
    /// </summary>
    public Toggle Quantize { get; set; } = Toggle.On;

    /// <summary>
    /// The options for establishing an automatic cue point when loading a track.
    /// </summary>
    public sealed class AutoCueLevel
    {
        /// <summary>The auto cue point is set where the track first reaches -36 dB.</summary>
        public static readonly AutoCueLevel Minus36 = new("-36 dB", 0x80);

        /// <summary>The auto cue point is set where the track first reaches -42 dB.</summary>
        public static readonly AutoCueLevel Minus42 = new("-42 dB", 0x81);

        /// <summary>The auto cue point is set where the track first reaches -48 dB.</summary>
        public static readonly AutoCueLevel Minus48 = new("-48 dB", 0x82);

        /// <summary>The auto cue point is set where the track first reaches -54 dB.</summary>
        public static readonly AutoCueLevel Minus54 = new("-54 dB", 0x83);

        /// <summary>The auto cue point is set where the track first reaches -60 dB.</summary>
        public static readonly AutoCueLevel Minus60 = new("-60 dB", 0x84);

        /// <summary>The auto cue point is set where the track first reaches -66 dB.</summary>
        public static readonly AutoCueLevel Minus66 = new("-66 dB", 0x85);

        /// <summary>The auto cue point is set where the track first reaches -72 dB.</summary>
        public static readonly AutoCueLevel Minus72 = new("-72 dB", 0x86);

        /// <summary>The auto cue point is set where the track first reaches -78 dB.</summary>
        public static readonly AutoCueLevel Minus78 = new("-78 dB", 0x87);

        /// <summary>The auto cue point comes from the first memory point in the track.</summary>
        public static readonly AutoCueLevel Memory = new("Memory", 0x88);

        /// <summary>The value displayed in the rekordbox interface for each auto cue level setting.</summary>
        public string DisplayValue { get; }

        /// <summary>The value sent in the Load Settings packet which establishes each auto cue level setting.</summary>
        public byte ProtocolValue { get; }

        private AutoCueLevel(string displayValue, byte protocolValue)
        {
            DisplayValue = displayValue;
            ProtocolValue = protocolValue;
        }

        /// <summary>All the auto cue level values.</summary>
        public static IReadOnlyList<AutoCueLevel> Values { get; } =
            [Minus36, Minus42, Minus48, Minus54, Minus60, Minus66, Minus72, Minus78, Memory];

        /// <inheritdoc/>
        public override string ToString() => DisplayValue;
    }

    /// <summary>
    /// Controls where the initial cue point is established when loading a track.
    /// </summary>
    public AutoCueLevel InitialAutoCueLevel { get; set; } = AutoCueLevel.Memory;

    /// <summary>
    /// The options for the player user interface language.
    /// </summary>
    public sealed class Language
    {
        /// <summary>The user interface is displayed in English.</summary>
        public static readonly Language English = new("English", 0x81);

        /// <summary>The user interface is displayed in French.</summary>
        public static readonly Language French = new("French", 0x82);

        /// <summary>The user interface is displayed in German.</summary>
        public static readonly Language German = new("German", 0x83);

        /// <summary>The user interface is displayed in Italian.</summary>
        public static readonly Language Italian = new("Italian", 0x84);

        /// <summary>The user interface is displayed in Dutch.</summary>
        public static readonly Language Dutch = new("Dutch", 0x85);

        /// <summary>The user interface is displayed in Spanish.</summary>
        public static readonly Language Spanish = new("Spanish", 0x86);

        /// <summary>The user interface is displayed in Russian.</summary>
        public static readonly Language Russian = new("Russian", 0x87);

        /// <summary>The user interface is displayed in Korean.</summary>
        public static readonly Language Korean = new("Korean", 0x88);

        /// <summary>The user interface is displayed in simplified Chinese.</summary>
        public static readonly Language ChineseSimplified = new("Chinese (simplified)", 0x89);

        /// <summary>The user interface is displayed in traditional Chinese.</summary>
        public static readonly Language ChineseTraditional = new("Chinese (traditional)", 0x8a);

        /// <summary>The user interface is displayed in Japanese.</summary>
        public static readonly Language Japanese = new("Japanese", 0x8b);

        /// <summary>The user interface is displayed in Portuguese.</summary>
        public static readonly Language Portuguese = new("Portuguese", 0x8c);

        /// <summary>The user interface is displayed in Swedish.</summary>
        public static readonly Language Swedish = new("Swedish", 0x8d);

        /// <summary>The user interface is displayed in Czech.</summary>
        public static readonly Language Czech = new("Czech", 0x8e);

        /// <summary>The user interface is displayed in Magyar.</summary>
        public static readonly Language Magyar = new("Magyar", 0x8f);

        /// <summary>The user interface is displayed in Danish.</summary>
        public static readonly Language Danish = new("Danish", 0x90);

        /// <summary>The user interface is displayed in Greek.</summary>
        public static readonly Language Greek = new("Greek", 0x91);

        /// <summary>The user interface is displayed in Turkish.</summary>
        public static readonly Language Turkish = new("Turkish", 0x92);

        /// <summary>The value displayed in the rekordbox interface for each language setting.</summary>
        public string DisplayValue { get; }

        /// <summary>The value sent in the Load Settings packet which establishes each language setting.</summary>
        public byte ProtocolValue { get; }

        private Language(string displayValue, byte protocolValue)
        {
            DisplayValue = displayValue;
            ProtocolValue = protocolValue;
        }

        /// <summary>All the language values.</summary>
        public static IReadOnlyList<Language> Values { get; } =
            [English, French, German, Italian, Dutch, Spanish, Russian, Korean, ChineseSimplified,
             ChineseTraditional, Japanese, Portuguese, Swedish, Czech, Magyar, Danish, Greek, Turkish];

        /// <inheritdoc/>
        public override string ToString() => DisplayValue;
    }

    /// <summary>
    /// Controls the language displayed in the player user interface.
    /// </summary>
    public Language InterfaceLanguage { get; set; } = Language.English;

    /// <summary>
    /// The brightness choices for optional illumination (for example of the jog ring or disc slot).
    /// </summary>
    public sealed class Illumination
    {
        /// <summary>The illumination is turned off.</summary>
        public static readonly Illumination Off = new("0 (Off)", 0x80);

        /// <summary>The illumination is dark.</summary>
        public static readonly Illumination Dark = new("1 (Dark)", 0x81);

        /// <summary>The illumination is bright.</summary>
        public static readonly Illumination Bright = new("2 (Bright)", 0x82);

        /// <summary>The value displayed in the rekordbox interface for each illumination setting.</summary>
        public string DisplayValue { get; }

        /// <summary>The value sent in the Load Settings packet which establishes each illumination setting.</summary>
        public byte ProtocolValue { get; }

        private Illumination(string displayValue, byte protocolValue)
        {
            DisplayValue = displayValue;
            ProtocolValue = protocolValue;
        }

        /// <summary>All the illumination values.</summary>
        public static IReadOnlyList<Illumination> Values { get; } = [Off, Dark, Bright];

        /// <inheritdoc/>
        public override string ToString() => DisplayValue;
    }

    /// <summary>
    /// Controls the illumination of the player's jog ring.
    /// </summary>
    public Illumination JogRingIllumination { get; set; } = Illumination.Bright;

    /// <summary>
    /// Controls the jog ring indicator feature.
    /// </summary>
    public Toggle JogRingIndicator { get; set; } = Toggle.On;

    /// <summary>
    /// Controls the Slip Flashing feature.
    /// </summary>
    public Toggle SlipFlashing { get; set; } = Toggle.On;

    /// <summary>
    /// Controls the illumination of the player's disc slot.
    /// </summary>
    public Illumination DiscSlotIllumination { get; set; } = Illumination.Dark;

    /// <summary>
    /// Controls the eject/load lock feature.
    /// </summary>
    public Toggle EjectLoadLock { get; set; } = Toggle.On;

    /// <summary>
    /// Controls the Sync feature.
    /// </summary>
    public Toggle Sync { get; set; } = Toggle.On;

    /// <summary>
    /// The options for whether to keep playing when a track ends.
    /// </summary>
    public sealed class PlayMode
    {
        /// <summary>When a track ends, the player moves on to the next track.</summary>
        public static readonly PlayMode Continue = new("Continue", 0x80);

        /// <summary>When a track ends, playback stops.</summary>
        public static readonly PlayMode Single = new("Single", 0x81);

        /// <summary>The value displayed in the rekordbox interface for each play mode setting.</summary>
        public string DisplayValue { get; }

        /// <summary>The value sent in the Load Settings packet which establishes each play mode setting.</summary>
        public byte ProtocolValue { get; }

        private PlayMode(string displayValue, byte protocolValue)
        {
            DisplayValue = displayValue;
            ProtocolValue = protocolValue;
        }

        /// <summary>All the play mode values.</summary>
        public static IReadOnlyList<PlayMode> Values { get; } = [Continue, Single];

        /// <inheritdoc/>
        public override string ToString() => DisplayValue;
    }

    /// <summary>
    /// Controls what to do when a track ends.
    /// </summary>
    public PlayMode AutoPlayMode { get; set; } = PlayMode.Single;

    /// <summary>
    /// The options for the quantization size.
    /// </summary>
    public sealed class QuantizeMode
    {
        /// <summary>Quantization aligns to whole beats.</summary>
        public static readonly QuantizeMode Beat = new("Beat", 0x80);

        /// <summary>Quantization aligns to half beats.</summary>
        public static readonly QuantizeMode Half = new("Half Beat", 0x81);

        /// <summary>Quantization aligns to quarter beats.</summary>
        public static readonly QuantizeMode Quarter = new("Quarter Beat", 0x82);

        /// <summary>Quantization aligns to eighth beats.</summary>
        public static readonly QuantizeMode Eighth = new("Eighth Beat", 0x83);

        /// <summary>The value displayed in the rekordbox interface for each quantize mode setting.</summary>
        public string DisplayValue { get; }

        /// <summary>The value sent in the Load Settings packet which establishes each quantize mode setting.</summary>
        public byte ProtocolValue { get; }

        private QuantizeMode(string displayValue, byte protocolValue)
        {
            DisplayValue = displayValue;
            ProtocolValue = protocolValue;
        }

        /// <summary>All the quantize mode values.</summary>
        public static IReadOnlyList<QuantizeMode> Values { get; } = [Beat, Half, Quarter, Eighth];

        /// <inheritdoc/>
        public override string ToString() => DisplayValue;
    }

    /// <summary>
    /// Controls the scale at which quantization is applied, when it is active.
    /// </summary>
    public QuantizeMode QuantizeBeatValue { get; set; } = QuantizeMode.Beat;

    /// <summary>
    /// The options for whether to autoload hot cues when loading a track.
    /// </summary>
    public sealed class AutoLoadMode
    {
        /// <summary>Hot cues are not autoloaded.</summary>
        public static readonly AutoLoadMode Off = new("Off", 0x80);

        /// <summary>Hot cues are autoloaded.</summary>
        public static readonly AutoLoadMode On = new("On", 0x81);

        /// <summary>The rekordbox setting for the track controls whether hot cues are autoloaded.</summary>
        public static readonly AutoLoadMode Rekordbox = new("rekordbox", 0x82);

        /// <summary>The value displayed in the rekordbox interface for each autoload setting.</summary>
        public string DisplayValue { get; }

        /// <summary>The value sent in the Load Settings packet which establishes each autoload setting.</summary>
        public byte ProtocolValue { get; }

        private AutoLoadMode(string displayValue, byte protocolValue)
        {
            DisplayValue = displayValue;
            ProtocolValue = protocolValue;
        }

        /// <summary>All the autoload mode values.</summary>
        public static IReadOnlyList<AutoLoadMode> Values { get; } = [Off, On, Rekordbox];

        /// <inheritdoc/>
        public override string ToString() => DisplayValue;
    }

    /// <summary>
    /// Controls whether hot cues are autoloaded when loading tracks.
    /// </summary>
    public AutoLoadMode HotCueAutoLoadMode { get; set; } = AutoLoadMode.Rekordbox;

    /// <summary>
    /// Controls whether hot cue colors are displayed.
    /// </summary>
    public Toggle HotCueColor { get; set; } = Toggle.On;

    /// <summary>
    /// Controls the Needle Lock feature.
    /// </summary>
    public Toggle NeedleLock { get; set; } = Toggle.On;

    /// <summary>
    /// The options for how time is displayed on the player.
    /// </summary>
    public sealed class TimeDisplayMode
    {
        /// <summary>Time is displayed as elapsed time within the track.</summary>
        public static readonly TimeDisplayMode Elapsed = new("Elapsed", 0x80);

        /// <summary>Time is displayed as remaining time within the track.</summary>
        public static readonly TimeDisplayMode Remaining = new("Remaining", 0x81);

        /// <summary>The value displayed in the rekordbox interface for each time display setting.</summary>
        public string DisplayValue { get; }

        /// <summary>The value sent in the Load Settings packet which establishes each time display setting.</summary>
        public byte ProtocolValue { get; }

        private TimeDisplayMode(string displayValue, byte protocolValue)
        {
            DisplayValue = displayValue;
            ProtocolValue = protocolValue;
        }

        /// <summary>All the time display mode values.</summary>
        public static IReadOnlyList<TimeDisplayMode> Values { get; } = [Elapsed, Remaining];

        /// <inheritdoc/>
        public override string ToString() => DisplayValue;
    }

    /// <summary>
    /// Controls how time is displayed on the player.
    /// </summary>
    public TimeDisplayMode TimeDisplay { get; set; } = TimeDisplayMode.Remaining;

    /// <summary>
    /// The options for how the jog wheel affects playback.
    /// </summary>
    public sealed class JogMode
    {
        /// <summary>The jog wheel is in CDJ mode.</summary>
        public static readonly JogMode Cdj = new("CDJ", 0x80);

        /// <summary>The jog wheel is in Vinyl mode.</summary>
        public static readonly JogMode Vinyl = new("Vinyl", 0x81);

        /// <summary>The value displayed in the rekordbox interface for each jog mode setting.</summary>
        public string DisplayValue { get; }

        /// <summary>The value sent in the Load Settings packet which establishes each jog mode setting.</summary>
        public byte ProtocolValue { get; }

        private JogMode(string displayValue, byte protocolValue)
        {
            DisplayValue = displayValue;
            ProtocolValue = protocolValue;
        }

        /// <summary>All the jog mode values.</summary>
        public static IReadOnlyList<JogMode> Values { get; } = [Cdj, Vinyl];

        /// <inheritdoc/>
        public override string ToString() => DisplayValue;
    }

    /// <summary>
    /// Controls how the jog wheel affects playback.
    /// </summary>
    public JogMode JogWheelMode { get; set; } = JogMode.Vinyl;

    /// <summary>
    /// Controls the Auto Cue feature.
    /// </summary>
    public Toggle AutoCue { get; set; } = Toggle.On;

    /// <summary>
    /// Controls the Master Tempo feature.
    /// </summary>
    public Toggle MasterTempo { get; set; } = Toggle.On;

    /// <summary>
    /// The options for tempo range limits.
    /// </summary>
    public sealed class TempoRange
    {
        /// <summary>The tempo fader range is ±6%.</summary>
        public static readonly TempoRange PlusMinus6 = new("±6", 0x80);

        /// <summary>The tempo fader range is ±10%.</summary>
        public static readonly TempoRange PlusMinus10 = new("±10", 0x81);

        /// <summary>The tempo fader range is ±16%.</summary>
        public static readonly TempoRange PlusMinus16 = new("±16", 0x82);

        /// <summary>The tempo fader range is wide.</summary>
        public static readonly TempoRange Wide = new("Wide", 0x83);

        /// <summary>The value displayed in the rekordbox interface for each tempo range setting.</summary>
        public string DisplayValue { get; }

        /// <summary>The value sent in the Load Settings packet which establishes each tempo range setting.</summary>
        public byte ProtocolValue { get; }

        private TempoRange(string displayValue, byte protocolValue)
        {
            DisplayValue = displayValue;
            ProtocolValue = protocolValue;
        }

        /// <summary>All the tempo range values.</summary>
        public static IReadOnlyList<TempoRange> Values { get; } = [PlusMinus6, PlusMinus10, PlusMinus16, Wide];

        /// <inheritdoc/>
        public override string ToString() => DisplayValue;
    }

    /// <summary>
    /// Controls the range of the tempo fader.
    /// </summary>
    public TempoRange TempoFaderRange { get; set; } = TempoRange.PlusMinus6;

    /// <summary>
    /// The options for the phase meter type.
    /// </summary>
    public sealed class PhaseMeterType
    {
        /// <summary>The first phase meter type.</summary>
        public static readonly PhaseMeterType Type1 = new("Type 1", 0x80);

        /// <summary>The second phase meter type.</summary>
        public static readonly PhaseMeterType Type2 = new("Type 2", 0x81);

        /// <summary>The value displayed in the rekordbox interface for each phase meter setting.</summary>
        public string DisplayValue { get; }

        /// <summary>The value sent in the Load Settings packet which establishes each phase meter setting.</summary>
        public byte ProtocolValue { get; }

        private PhaseMeterType(string displayValue, byte protocolValue)
        {
            DisplayValue = displayValue;
            ProtocolValue = protocolValue;
        }

        /// <summary>All the phase meter type values.</summary>
        public static IReadOnlyList<PhaseMeterType> Values { get; } = [Type1, Type2];

        /// <inheritdoc/>
        public override string ToString() => DisplayValue;
    }

    /// <summary>
    /// Controls the phase meter type displayed.
    /// </summary>
    public PhaseMeterType PhaseMeter { get; set; } = PhaseMeterType.Type1;

    /// <summary>
    /// The options for the vinyl speed adjust mode.
    /// </summary>
    public sealed class VinylSpeedAdjust
    {
        /// <summary>Vinyl speed adjustment applies both when touching and releasing the jog wheel.</summary>
        public static readonly VinylSpeedAdjust TouchRelease = new("Touch and Release", 0x80);

        /// <summary>Vinyl speed adjustment applies only when touching the jog wheel.</summary>
        public static readonly VinylSpeedAdjust Touch = new("Touch", 0x81);

        /// <summary>Vinyl speed adjustment applies only when releasing the jog wheel.</summary>
        public static readonly VinylSpeedAdjust Release = new("Release", 0x82);

        /// <summary>The value displayed in the rekordbox interface for each vinyl speed adjust setting.</summary>
        public string DisplayValue { get; }

        /// <summary>The value sent in the Load Settings packet which establishes each vinyl speed adjust setting.</summary>
        public byte ProtocolValue { get; }

        private VinylSpeedAdjust(string displayValue, byte protocolValue)
        {
            DisplayValue = displayValue;
            ProtocolValue = protocolValue;
        }

        /// <summary>All the vinyl speed adjust values.</summary>
        public static IReadOnlyList<VinylSpeedAdjust> Values { get; } = [TouchRelease, Touch, Release];

        /// <inheritdoc/>
        public override string ToString() => DisplayValue;
    }

    /// <summary>
    /// Controls when vinyl speed adjustment is used.
    /// </summary>
    public VinylSpeedAdjust VinylSpeedAdjustMode { get; set; } = VinylSpeedAdjust.TouchRelease;

    /// <summary>
    /// The options for what to display in the jog wheel display screens.
    /// </summary>
    public sealed class JogWheelDisplay
    {
        /// <summary>The jog wheel display contents are chosen automatically.</summary>
        public static readonly JogWheelDisplay Auto = new("Auto", 0x80);

        /// <summary>The jog wheel displays information.</summary>
        public static readonly JogWheelDisplay Info = new("Info", 0x81);

        /// <summary>The jog wheel display is simple.</summary>
        public static readonly JogWheelDisplay Simple = new("Simple", 0x82);

        /// <summary>The jog wheel displays artwork.</summary>
        public static readonly JogWheelDisplay Artwork = new("Artwork", 0x83);

        /// <summary>The value displayed in the rekordbox interface for each jog wheel display setting.</summary>
        public string DisplayValue { get; }

        /// <summary>The value sent in the Load Settings packet which establishes each jog wheel display setting.</summary>
        public byte ProtocolValue { get; }

        private JogWheelDisplay(string displayValue, byte protocolValue)
        {
            DisplayValue = displayValue;
            ProtocolValue = protocolValue;
        }

        /// <summary>All the jog wheel display values.</summary>
        public static IReadOnlyList<JogWheelDisplay> Values { get; } = [Auto, Info, Simple, Artwork];

        /// <inheritdoc/>
        public override string ToString() => DisplayValue;
    }

    /// <summary>
    /// Controls what is shown in the jog wheel display screens.
    /// </summary>
    public JogWheelDisplay JogWheelDisplayMode { get; set; } = JogWheelDisplay.Auto;

    /// <summary>
    /// The brightness options for the player's performance pads and buttons.
    /// </summary>
    public sealed class PadButtonBrightness
    {
        /// <summary>The dimmest pad/button brightness setting (1).</summary>
        public static readonly PadButtonBrightness Dimmest = new(1, 0x81);

        /// <summary>A dim pad/button brightness setting (2).</summary>
        public static readonly PadButtonBrightness Dim = new(2, 0x82);

        /// <summary>A bright pad/button brightness setting (3).</summary>
        public static readonly PadButtonBrightness Bright = new(3, 0x83);

        /// <summary>The brightest pad/button brightness setting (4).</summary>
        public static readonly PadButtonBrightness Brightest = new(4, 0x84);

        /// <summary>The value displayed in the rekordbox interface for each brightness setting.</summary>
        public int DisplayValue { get; }

        /// <summary>The value sent in the Load Settings packet which establishes each brightness setting.</summary>
        public byte ProtocolValue { get; }

        private PadButtonBrightness(int displayValue, byte protocolValue)
        {
            DisplayValue = displayValue;
            ProtocolValue = protocolValue;
        }

        /// <summary>All the pad/button brightness values.</summary>
        public static IReadOnlyList<PadButtonBrightness> Values { get; } = [Dimmest, Dim, Bright, Brightest];

        /// <inheritdoc/>
        public override string ToString() => DisplayValue.ToString();
    }

    /// <summary>
    /// Controls the brightness of the performance pads and buttons.
    /// </summary>
    public PadButtonBrightness PadAndButtonBrightness { get; set; } = PadButtonBrightness.Bright;

    /// <summary>
    /// Controls the brightness of the jog wheel display.
    /// </summary>
    public LcdBrightness JogWheelLcdBrightness { get; set; } = LcdBrightness.Medium;
}
