using BeatLink.CrateDigger.Pdb;
using BeatLink.Data;

namespace BeatLink;

/// <summary>
/// The phrase-analysis members of <see cref="Util"/>: the colors and labels used to render song
/// structure (phrase) information from rekordbox track analysis. These members live in their own
/// file because they depend on the Kaitai Struct <c>RekordboxAnlz</c> parser, which arrived in a
/// later phase than the rest of <see cref="Util"/>. Ported from the phrase-color portion of Deep
/// Symmetry's beat-link <c>Util</c>, by James Elliott; where the Java library exposes
/// <c>java.awt.Color</c>, this port uses the platform-neutral <see cref="ArgbColor"/> struct.
/// </summary>
public static partial class Util
{
    /// <summary>The color of an intro phrase in a track with a low mood.</summary>
    public static readonly ArgbColor LowIntroColor = ArgbColor.FromRgb(255, 170, 180);

    /// <summary>The color of a verse 1 phrase in a track with a low mood.</summary>
    public static readonly ArgbColor LowVerse1Color = ArgbColor.FromRgb(165, 160, 255);

    /// <summary>The color of a verse 2 phrase in a track with a low mood.</summary>
    public static readonly ArgbColor LowVerse2Color = ArgbColor.FromRgb(190, 160, 255);

    /// <summary>The color of a bridge phrase in a track with a low mood.</summary>
    public static readonly ArgbColor LowBridgeColor = ArgbColor.FromRgb(255, 250, 165);

    /// <summary>The color of a chorus phrase in a track with a low mood.</summary>
    public static readonly ArgbColor LowChorusColor = ArgbColor.FromRgb(185, 225, 185);

    /// <summary>The color of an outro phrase in a track with a low mood.</summary>
    public static readonly ArgbColor LowOutroColor = ArgbColor.FromRgb(145, 160, 180);

    /// <summary>The color of an intro phrase in a track with a mid mood.</summary>
    public static readonly ArgbColor MidIntroColor = ArgbColor.FromRgb(225, 70, 70);

    /// <summary>The color of a verse 1 phrase in a track with a mid mood.</summary>
    public static readonly ArgbColor MidVerse1Color = ArgbColor.FromRgb(80, 110, 255);

    /// <summary>The color of a verse 2 phrase in a track with a mid mood.</summary>
    public static readonly ArgbColor MidVerse2Color = ArgbColor.FromRgb(80, 85, 255);

    /// <summary>The color of a verse 3 phrase in a track with a mid mood.</summary>
    public static readonly ArgbColor MidVerse3Color = ArgbColor.FromRgb(100, 80, 255);

    /// <summary>The color of a verse 4 phrase in a track with a mid mood.</summary>
    public static readonly ArgbColor MidVerse4Color = ArgbColor.FromRgb(120, 80, 255);

    /// <summary>The color of a verse 5 phrase in a track with a mid mood.</summary>
    public static readonly ArgbColor MidVerse5Color = ArgbColor.FromRgb(140, 80, 255);

    /// <summary>The color of a verse 6 phrase in a track with a mid mood.</summary>
    public static readonly ArgbColor MidVerse6Color = ArgbColor.FromRgb(160, 80, 255);

    /// <summary>The color of a bridge phrase in a track with a mid mood.</summary>
    public static readonly ArgbColor MidBridgeColor = ArgbColor.FromRgb(225, 215, 65);

    /// <summary>The color of a chorus phrase in a track with a mid mood.</summary>
    public static readonly ArgbColor MidChorusColor = ArgbColor.FromRgb(120, 195, 125);

    /// <summary>The color of an outro phrase in a track with a mid mood.</summary>
    public static readonly ArgbColor MidOutroColor = ArgbColor.FromRgb(115, 130, 150);

    /// <summary>The color of an intro 1 phrase in a track with a high mood.</summary>
    public static readonly ArgbColor HighIntro1Color = ArgbColor.FromRgb(200, 0, 0);

    /// <summary>The color of an intro 2 phrase in a track with a high mood.</summary>
    public static readonly ArgbColor HighIntro2Color = ArgbColor.FromRgb(200, 50, 0);

    /// <summary>The color of an up 1 phrase in a track with a high mood.</summary>
    public static readonly ArgbColor HighUp1Color = ArgbColor.FromRgb(140, 50, 255);

    /// <summary>The color of an up 2 phrase in a track with a high mood.</summary>
    public static readonly ArgbColor HighUp2Color = ArgbColor.FromRgb(105, 50, 255);

    /// <summary>The color of an up 3 phrase in a track with a high mood.</summary>
    public static readonly ArgbColor HighUp3Color = ArgbColor.FromRgb(90, 50, 255);

    /// <summary>The color of a down phrase in a track with a high mood.</summary>
    public static readonly ArgbColor HighDownColor = ArgbColor.FromRgb(155, 115, 45);

    /// <summary>The color of a chorus 1 phrase in a track with a high mood.</summary>
    public static readonly ArgbColor HighChorus1Color = ArgbColor.FromRgb(15, 170, 0);

    /// <summary>The color of a chorus 2 phrase in a track with a high mood.</summary>
    public static readonly ArgbColor HighChorus2Color = ArgbColor.FromRgb(15, 170, 0);

    /// <summary>The color of an outro 1 phrase in a track with a high mood.</summary>
    public static readonly ArgbColor HighOutro1Color = ArgbColor.FromRgb(80, 135, 195);

    /// <summary>The color of an outro 2 phrase in a track with a high mood.</summary>
    public static readonly ArgbColor HighOutro2Color = ArgbColor.FromRgb(95, 135, 175);

    /// <summary>
    /// Get the color that should be used for the bar representing a phrase being painted on a
    /// waveform.
    /// </summary>
    /// <param name="phrase">the song structure entry representing a phrase being painted</param>
    /// <returns>the color with which to paint the box that identifies the phrase</returns>
    /// <exception cref="ArgumentNullException">if <paramref name="phrase"/> is <c>null</c></exception>
    public static ArgbColor PhraseColor(RekordboxAnlz.SongStructureEntry phrase)
    {
        ArgumentNullException.ThrowIfNull(phrase);

        switch (phrase.M_Parent.Mood)
        {
            case RekordboxAnlz.TrackMood.Low:
                if (phrase.Kind is not RekordboxAnlz.PhraseLow phraseLow)
                {
                    return ArgbColor.White;  // We don't recognize this phrase.
                }
                return phraseLow.Id switch
                {
                    RekordboxAnlz.MoodLowPhrase.Intro => LowIntroColor,
                    RekordboxAnlz.MoodLowPhrase.Verse1 or RekordboxAnlz.MoodLowPhrase.Verse1b or
                        RekordboxAnlz.MoodLowPhrase.Verse1c => LowVerse1Color,
                    RekordboxAnlz.MoodLowPhrase.Verse2 or RekordboxAnlz.MoodLowPhrase.Verse2b or
                        RekordboxAnlz.MoodLowPhrase.Verse2c => LowVerse2Color,
                    RekordboxAnlz.MoodLowPhrase.Bridge => LowBridgeColor,
                    RekordboxAnlz.MoodLowPhrase.Chorus => LowChorusColor,
                    RekordboxAnlz.MoodLowPhrase.Outro => LowOutroColor,
                    _ => ArgbColor.White  // We don't recognize this phrase.
                };

            case RekordboxAnlz.TrackMood.Mid:
                if (phrase.Kind is not RekordboxAnlz.PhraseMid phraseMid)
                {
                    return ArgbColor.White;  // We don't recognize this phrase.
                }
                return phraseMid.Id switch
                {
                    RekordboxAnlz.MoodMidPhrase.Intro => MidIntroColor,
                    RekordboxAnlz.MoodMidPhrase.Verse1 => MidVerse1Color,
                    RekordboxAnlz.MoodMidPhrase.Verse2 => MidVerse2Color,
                    RekordboxAnlz.MoodMidPhrase.Verse3 => MidVerse3Color,
                    RekordboxAnlz.MoodMidPhrase.Verse4 => MidVerse4Color,
                    RekordboxAnlz.MoodMidPhrase.Verse5 => MidVerse5Color,
                    RekordboxAnlz.MoodMidPhrase.Verse6 => MidVerse6Color,
                    RekordboxAnlz.MoodMidPhrase.Bridge => MidBridgeColor,
                    RekordboxAnlz.MoodMidPhrase.Chorus => MidChorusColor,
                    RekordboxAnlz.MoodMidPhrase.Outro => MidOutroColor,
                    _ => ArgbColor.White  // We don't recognize this phrase.
                };

            case RekordboxAnlz.TrackMood.High:
                if (phrase.Kind is not RekordboxAnlz.PhraseHigh phraseHigh)
                {
                    return ArgbColor.White;  // We don't recognize this phrase.
                }
                switch (phraseHigh.Id)
                {
                    case RekordboxAnlz.MoodHighPhrase.Intro:
                        return phrase.K1 == 1 ? HighIntro1Color : HighIntro2Color;

                    case RekordboxAnlz.MoodHighPhrase.Up:
                        if (phrase.K2 == 0)
                        {
                            return phrase.K3 == 0 ? HighUp1Color : HighUp2Color;
                        }
                        return HighUp3Color;

                    case RekordboxAnlz.MoodHighPhrase.Down:
                        return HighDownColor;

                    case RekordboxAnlz.MoodHighPhrase.Chorus:
                        return phrase.K1 == 1 ? HighChorus1Color : HighChorus2Color;

                    case RekordboxAnlz.MoodHighPhrase.Outro:
                        return phrase.K1 == 1 ? HighOutro1Color : HighOutro2Color;

                    default:
                        return ArgbColor.White;  // We don't recognize this phrase.
                }

            default:
                return ArgbColor.White;  // We don't recognize this mood.
        }
    }

    /// <summary>
    /// Returns the color that should be used for the text of a phrase label, for legibility on
    /// the phrase background.
    /// </summary>
    /// <param name="phrase">the song structure entry representing a phrase being painted</param>
    /// <returns>the color that should be used for painting the label</returns>
    /// <exception cref="ArgumentNullException">if <paramref name="phrase"/> is <c>null</c></exception>
    public static ArgbColor PhraseTextColor(RekordboxAnlz.SongStructureEntry phrase)
    {
        ArgumentNullException.ThrowIfNull(phrase);
        if (phrase.M_Parent.Mood == RekordboxAnlz.TrackMood.High)
        {
            return ArgbColor.White;
        }
        return ArgbColor.Black;
    }

    /// <summary>
    /// Get the text that should be used for the label representing a phrase being painted on a
    /// waveform.
    /// </summary>
    /// <param name="phrase">the song structure entry representing a phrase being painted</param>
    /// <returns>the text with which to label the box that identifies the phrase</returns>
    /// <exception cref="ArgumentNullException">if <paramref name="phrase"/> is <c>null</c></exception>
    public static string PhraseLabel(RekordboxAnlz.SongStructureEntry phrase)
    {
        ArgumentNullException.ThrowIfNull(phrase);

        switch (phrase.M_Parent.Mood)
        {
            case RekordboxAnlz.TrackMood.Low:
                if (phrase.Kind is not RekordboxAnlz.PhraseLow phraseLow)
                {
                    return "Unknown Low";  // We don't recognize this phrase.
                }
                return phraseLow.Id switch
                {
                    RekordboxAnlz.MoodLowPhrase.Intro => "Intro",
                    RekordboxAnlz.MoodLowPhrase.Verse1 or RekordboxAnlz.MoodLowPhrase.Verse1b or
                        RekordboxAnlz.MoodLowPhrase.Verse1c => "Verse 1",
                    RekordboxAnlz.MoodLowPhrase.Verse2 or RekordboxAnlz.MoodLowPhrase.Verse2b or
                        RekordboxAnlz.MoodLowPhrase.Verse2c => "Verse 2",
                    RekordboxAnlz.MoodLowPhrase.Bridge => "Bridge",
                    RekordboxAnlz.MoodLowPhrase.Chorus => "Chorus",
                    RekordboxAnlz.MoodLowPhrase.Outro => "Outro",
                    _ => "Unknown Low"  // We don't recognize this phrase.
                };

            case RekordboxAnlz.TrackMood.Mid:
                if (phrase.Kind is not RekordboxAnlz.PhraseMid phraseMid)
                {
                    return "Unknown Mid";  // We don't recognize this phrase.
                }
                return phraseMid.Id switch
                {
                    RekordboxAnlz.MoodMidPhrase.Intro => "Intro",
                    RekordboxAnlz.MoodMidPhrase.Verse1 => "Verse 1",
                    RekordboxAnlz.MoodMidPhrase.Verse2 => "Verse 2",
                    RekordboxAnlz.MoodMidPhrase.Verse3 => "Verse 3",
                    RekordboxAnlz.MoodMidPhrase.Verse4 => "Verse 4",
                    RekordboxAnlz.MoodMidPhrase.Verse5 => "Verse 5",
                    RekordboxAnlz.MoodMidPhrase.Verse6 => "Verse 6",
                    RekordboxAnlz.MoodMidPhrase.Bridge => "Bridge",
                    RekordboxAnlz.MoodMidPhrase.Chorus => "Chorus",
                    RekordboxAnlz.MoodMidPhrase.Outro => "Outro",
                    _ => "Unknown Mid"  // We don't recognize this phrase.
                };

            case RekordboxAnlz.TrackMood.High:
                if (phrase.Kind is not RekordboxAnlz.PhraseHigh phraseHigh)
                {
                    return "Unknown High";  // We don't recognize this phrase.
                }
                switch (phraseHigh.Id)
                {
                    case RekordboxAnlz.MoodHighPhrase.Intro:
                        return phrase.K1 == 1 ? "Intro 1" : "Intro 2";

                    case RekordboxAnlz.MoodHighPhrase.Up:
                        if (phrase.K2 == 0)
                        {
                            return phrase.K3 == 0 ? "Up 1" : "Up 2";
                        }
                        return "Up 3";

                    case RekordboxAnlz.MoodHighPhrase.Down:
                        return "Down";

                    case RekordboxAnlz.MoodHighPhrase.Chorus:
                        return phrase.K1 == 1 ? "Chorus 1" : "Chorus 2";

                    case RekordboxAnlz.MoodHighPhrase.Outro:
                        return phrase.K1 == 1 ? "Outro 1" : "Outro 2";

                    default:
                        return "Unknown High";  // We don't recognize this phrase.
                }

            default:
                return "Unknown Mood";  // We don't recognize this mood.
        }
    }
}
