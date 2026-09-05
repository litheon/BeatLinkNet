namespace BeatLink.Tests;

public class PlayerSettingsTests
{
    [Fact]
    public void DefaultsMatchJavaImplementation()
    {
        var settings = new PlayerSettings();
        Assert.Same(PlayerSettings.Toggle.On, settings.OnAirDisplay);
        Assert.Same(PlayerSettings.LcdBrightness.Medium, settings.MainLcdBrightness);
        Assert.Same(PlayerSettings.Toggle.On, settings.Quantize);
        Assert.Same(PlayerSettings.AutoCueLevel.Memory, settings.InitialAutoCueLevel);
        Assert.Same(PlayerSettings.Language.English, settings.InterfaceLanguage);
        Assert.Same(PlayerSettings.Illumination.Bright, settings.JogRingIllumination);
        Assert.Same(PlayerSettings.Toggle.On, settings.JogRingIndicator);
        Assert.Same(PlayerSettings.Toggle.On, settings.SlipFlashing);
        Assert.Same(PlayerSettings.Illumination.Dark, settings.DiscSlotIllumination);
        Assert.Same(PlayerSettings.Toggle.On, settings.EjectLoadLock);
        Assert.Same(PlayerSettings.Toggle.On, settings.Sync);
        Assert.Same(PlayerSettings.PlayMode.Single, settings.AutoPlayMode);
        Assert.Same(PlayerSettings.QuantizeMode.Beat, settings.QuantizeBeatValue);
        Assert.Same(PlayerSettings.AutoLoadMode.Rekordbox, settings.HotCueAutoLoadMode);
        Assert.Same(PlayerSettings.Toggle.On, settings.HotCueColor);
        Assert.Same(PlayerSettings.Toggle.On, settings.NeedleLock);
        Assert.Same(PlayerSettings.TimeDisplayMode.Remaining, settings.TimeDisplay);
        Assert.Same(PlayerSettings.JogMode.Vinyl, settings.JogWheelMode);
        Assert.Same(PlayerSettings.Toggle.On, settings.AutoCue);
        Assert.Same(PlayerSettings.Toggle.On, settings.MasterTempo);
        Assert.Same(PlayerSettings.TempoRange.PlusMinus6, settings.TempoFaderRange);
        Assert.Same(PlayerSettings.PhaseMeterType.Type1, settings.PhaseMeter);
        Assert.Same(PlayerSettings.VinylSpeedAdjust.TouchRelease, settings.VinylSpeedAdjustMode);
        Assert.Same(PlayerSettings.JogWheelDisplay.Auto, settings.JogWheelDisplayMode);
        Assert.Same(PlayerSettings.PadButtonBrightness.Bright, settings.PadAndButtonBrightness);
        Assert.Same(PlayerSettings.LcdBrightness.Medium, settings.JogWheelLcdBrightness);
    }

    [Fact]
    public void ToggleValues()
    {
        Assert.Equal(0x80, PlayerSettings.Toggle.Off.ProtocolValue);
        Assert.Equal(0x81, PlayerSettings.Toggle.On.ProtocolValue);
        Assert.Equal("Off", PlayerSettings.Toggle.Off.DisplayValue);
        Assert.Equal("On", PlayerSettings.Toggle.On.DisplayValue);
        Assert.Equal(2, PlayerSettings.Toggle.Values.Count);
    }

    [Fact]
    public void LcdBrightnessValues()
    {
        Assert.Equal(0x81, PlayerSettings.LcdBrightness.Dimmest.ProtocolValue);
        Assert.Equal(0x82, PlayerSettings.LcdBrightness.Dim.ProtocolValue);
        Assert.Equal(0x83, PlayerSettings.LcdBrightness.Medium.ProtocolValue);
        Assert.Equal(0x84, PlayerSettings.LcdBrightness.Bright.ProtocolValue);
        Assert.Equal(0x85, PlayerSettings.LcdBrightness.Brightest.ProtocolValue);
        Assert.Equal(1, PlayerSettings.LcdBrightness.Dimmest.DisplayValue);
        Assert.Equal(5, PlayerSettings.LcdBrightness.Brightest.DisplayValue);
        Assert.Equal(5, PlayerSettings.LcdBrightness.Values.Count);
    }

    [Fact]
    public void AutoCueLevelValues()
    {
        Assert.Equal(0x80, PlayerSettings.AutoCueLevel.Minus36.ProtocolValue);
        Assert.Equal(0x81, PlayerSettings.AutoCueLevel.Minus42.ProtocolValue);
        Assert.Equal(0x82, PlayerSettings.AutoCueLevel.Minus48.ProtocolValue);
        Assert.Equal(0x83, PlayerSettings.AutoCueLevel.Minus54.ProtocolValue);
        Assert.Equal(0x84, PlayerSettings.AutoCueLevel.Minus60.ProtocolValue);
        Assert.Equal(0x85, PlayerSettings.AutoCueLevel.Minus66.ProtocolValue);
        Assert.Equal(0x86, PlayerSettings.AutoCueLevel.Minus72.ProtocolValue);
        Assert.Equal(0x87, PlayerSettings.AutoCueLevel.Minus78.ProtocolValue);
        Assert.Equal(0x88, PlayerSettings.AutoCueLevel.Memory.ProtocolValue);
        Assert.Equal("-36 dB", PlayerSettings.AutoCueLevel.Minus36.DisplayValue);
        Assert.Equal("Memory", PlayerSettings.AutoCueLevel.Memory.DisplayValue);
        Assert.Equal(9, PlayerSettings.AutoCueLevel.Values.Count);
    }

    [Fact]
    public void LanguageValues()
    {
        Assert.Equal(0x81, PlayerSettings.Language.English.ProtocolValue);
        Assert.Equal(0x82, PlayerSettings.Language.French.ProtocolValue);
        Assert.Equal(0x89, PlayerSettings.Language.ChineseSimplified.ProtocolValue);
        Assert.Equal(0x8a, PlayerSettings.Language.ChineseTraditional.ProtocolValue);
        Assert.Equal(0x8b, PlayerSettings.Language.Japanese.ProtocolValue);
        Assert.Equal(0x92, PlayerSettings.Language.Turkish.ProtocolValue);
        Assert.Equal("Chinese (simplified)", PlayerSettings.Language.ChineseSimplified.DisplayValue);
        Assert.Equal(18, PlayerSettings.Language.Values.Count);
    }

    [Fact]
    public void IlluminationValues()
    {
        Assert.Equal(0x80, PlayerSettings.Illumination.Off.ProtocolValue);
        Assert.Equal(0x81, PlayerSettings.Illumination.Dark.ProtocolValue);
        Assert.Equal(0x82, PlayerSettings.Illumination.Bright.ProtocolValue);
        Assert.Equal("0 (Off)", PlayerSettings.Illumination.Off.DisplayValue);
        Assert.Equal("2 (Bright)", PlayerSettings.Illumination.Bright.DisplayValue);
        Assert.Equal(3, PlayerSettings.Illumination.Values.Count);
    }

    [Fact]
    public void PlayModeValues()
    {
        Assert.Equal(0x80, PlayerSettings.PlayMode.Continue.ProtocolValue);
        Assert.Equal(0x81, PlayerSettings.PlayMode.Single.ProtocolValue);
        Assert.Equal("Continue", PlayerSettings.PlayMode.Continue.DisplayValue);
        Assert.Equal(2, PlayerSettings.PlayMode.Values.Count);
    }

    [Fact]
    public void QuantizeModeValues()
    {
        Assert.Equal(0x80, PlayerSettings.QuantizeMode.Beat.ProtocolValue);
        Assert.Equal(0x81, PlayerSettings.QuantizeMode.Half.ProtocolValue);
        Assert.Equal(0x82, PlayerSettings.QuantizeMode.Quarter.ProtocolValue);
        Assert.Equal(0x83, PlayerSettings.QuantizeMode.Eighth.ProtocolValue);
        Assert.Equal("Half Beat", PlayerSettings.QuantizeMode.Half.DisplayValue);
        Assert.Equal(4, PlayerSettings.QuantizeMode.Values.Count);
    }

    [Fact]
    public void AutoLoadModeValues()
    {
        Assert.Equal(0x80, PlayerSettings.AutoLoadMode.Off.ProtocolValue);
        Assert.Equal(0x81, PlayerSettings.AutoLoadMode.On.ProtocolValue);
        Assert.Equal(0x82, PlayerSettings.AutoLoadMode.Rekordbox.ProtocolValue);
        Assert.Equal("rekordbox", PlayerSettings.AutoLoadMode.Rekordbox.DisplayValue);
        Assert.Equal(3, PlayerSettings.AutoLoadMode.Values.Count);
    }

    [Fact]
    public void TimeDisplayModeValues()
    {
        Assert.Equal(0x80, PlayerSettings.TimeDisplayMode.Elapsed.ProtocolValue);
        Assert.Equal(0x81, PlayerSettings.TimeDisplayMode.Remaining.ProtocolValue);
        Assert.Equal("Remaining", PlayerSettings.TimeDisplayMode.Remaining.DisplayValue);
        Assert.Equal(2, PlayerSettings.TimeDisplayMode.Values.Count);
    }

    [Fact]
    public void JogModeValues()
    {
        Assert.Equal(0x80, PlayerSettings.JogMode.Cdj.ProtocolValue);
        Assert.Equal(0x81, PlayerSettings.JogMode.Vinyl.ProtocolValue);
        Assert.Equal("CDJ", PlayerSettings.JogMode.Cdj.DisplayValue);
        Assert.Equal("Vinyl", PlayerSettings.JogMode.Vinyl.DisplayValue);
        Assert.Equal(2, PlayerSettings.JogMode.Values.Count);
    }

    [Fact]
    public void TempoRangeValues()
    {
        Assert.Equal(0x80, PlayerSettings.TempoRange.PlusMinus6.ProtocolValue);
        Assert.Equal(0x81, PlayerSettings.TempoRange.PlusMinus10.ProtocolValue);
        Assert.Equal(0x82, PlayerSettings.TempoRange.PlusMinus16.ProtocolValue);
        Assert.Equal(0x83, PlayerSettings.TempoRange.Wide.ProtocolValue);
        Assert.Equal("±6", PlayerSettings.TempoRange.PlusMinus6.DisplayValue);
        Assert.Equal("Wide", PlayerSettings.TempoRange.Wide.DisplayValue);
        Assert.Equal(4, PlayerSettings.TempoRange.Values.Count);
    }

    [Fact]
    public void PhaseMeterTypeValues()
    {
        Assert.Equal(0x80, PlayerSettings.PhaseMeterType.Type1.ProtocolValue);
        Assert.Equal(0x81, PlayerSettings.PhaseMeterType.Type2.ProtocolValue);
        Assert.Equal("Type 1", PlayerSettings.PhaseMeterType.Type1.DisplayValue);
        Assert.Equal(2, PlayerSettings.PhaseMeterType.Values.Count);
    }

    [Fact]
    public void VinylSpeedAdjustValues()
    {
        Assert.Equal(0x80, PlayerSettings.VinylSpeedAdjust.TouchRelease.ProtocolValue);
        Assert.Equal(0x81, PlayerSettings.VinylSpeedAdjust.Touch.ProtocolValue);
        Assert.Equal(0x82, PlayerSettings.VinylSpeedAdjust.Release.ProtocolValue);
        Assert.Equal("Touch and Release", PlayerSettings.VinylSpeedAdjust.TouchRelease.DisplayValue);
        Assert.Equal(3, PlayerSettings.VinylSpeedAdjust.Values.Count);
    }

    [Fact]
    public void JogWheelDisplayValues()
    {
        Assert.Equal(0x80, PlayerSettings.JogWheelDisplay.Auto.ProtocolValue);
        Assert.Equal(0x81, PlayerSettings.JogWheelDisplay.Info.ProtocolValue);
        Assert.Equal(0x82, PlayerSettings.JogWheelDisplay.Simple.ProtocolValue);
        Assert.Equal(0x83, PlayerSettings.JogWheelDisplay.Artwork.ProtocolValue);
        Assert.Equal("Artwork", PlayerSettings.JogWheelDisplay.Artwork.DisplayValue);
        Assert.Equal(4, PlayerSettings.JogWheelDisplay.Values.Count);
    }

    [Fact]
    public void PadButtonBrightnessValues()
    {
        Assert.Equal(0x81, PlayerSettings.PadButtonBrightness.Dimmest.ProtocolValue);
        Assert.Equal(0x82, PlayerSettings.PadButtonBrightness.Dim.ProtocolValue);
        Assert.Equal(0x83, PlayerSettings.PadButtonBrightness.Bright.ProtocolValue);
        Assert.Equal(0x84, PlayerSettings.PadButtonBrightness.Brightest.ProtocolValue);
        Assert.Equal(3, PlayerSettings.PadButtonBrightness.Bright.DisplayValue);
        Assert.Equal(4, PlayerSettings.PadButtonBrightness.Values.Count);
    }

    [Fact]
    public void SettingsAreMutable()
    {
        var settings = new PlayerSettings
        {
            TempoFaderRange = PlayerSettings.TempoRange.Wide,
            JogWheelMode = PlayerSettings.JogMode.Cdj,
            Sync = PlayerSettings.Toggle.Off
        };
        Assert.Same(PlayerSettings.TempoRange.Wide, settings.TempoFaderRange);
        Assert.Same(PlayerSettings.JogMode.Cdj, settings.JogWheelMode);
        Assert.Same(PlayerSettings.Toggle.Off, settings.Sync);
    }
}
