using EduStream.Core.Common;

namespace EduStream.FileTransfer.Tests;

public sealed class AppThemePaletteTests
{
    [Fact]
    public void LightSurfacesUseSubtleWarmOffWhiteWithoutStrongBeige()
    {
        var palette = AppThemePalette.Colors(true);
        foreach (var key in new[] { "BrushBgPrimary", "BrushBgSurface", "BrushBgElevated", "BrushBgInput", "BrushListGround", "BrushStudentCard" })
        {
            var hex = palette[key];
            var red = Convert.ToInt32(hex.Substring(1, 2), 16);
            var green = Convert.ToInt32(hex.Substring(3, 2), 16);
            var blue = Convert.ToInt32(hex.Substring(5, 2), 16);
            Assert.True(red >= green && green > blue, key);
            Assert.InRange(red, 239, 252);
            Assert.InRange(red - blue, 1, 3);
        }
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void TextAndActionColorsRemainReadable(bool light)
    {
        var palette = AppThemePalette.Colors(light);
        foreach (var text in new[] { "BrushTextPrimary", "BrushTextSecondary", "BrushTextMuted", "BrushAccentPurple", "BrushAccentTeal" })
        foreach (var background in new[] { "BrushBgPrimary", "BrushBgSurface", "BrushBgInput", "BrushChatSelf", "BrushChatOther", "BrushChatSystem" })
            Assert.True(Contrast(palette[text], palette[background]) >= 4.5, $"{light}: {text}/{background}");
        Assert.True(Contrast(palette["BrushBgPrimary"], palette["BrushAccentPurple"]) >= 4.5);
        Assert.True(Contrast(palette["BrushOnStrong"], palette["BrushDrawingButton"]) >= 4.5);
    }
    [Fact]
    public void ThemesUseMatchingPaletteKeys()
    {
        Assert.Equal(AppThemePalette.Colors(false).Keys, AppThemePalette.Colors(true).Keys);
        Assert.All(AppThemePalette.Colors(true).Keys, key => Assert.StartsWith("Brush", key));
    }
    private static double Contrast(string a, string b)
    {
        double Luminance(string hex)
        {
            double Channel(int start) { var c = Convert.ToInt32(hex.Substring(start, 2), 16) / 255d; return c <= .04045 ? c / 12.92 : Math.Pow((c + .055) / 1.055, 2.4); }
            return .2126 * Channel(1) + .7152 * Channel(3) + .0722 * Channel(5);
        }
        var x = Luminance(a); var y = Luminance(b);
        return (Math.Max(x, y) + .05) / (Math.Min(x, y) + .05);
    }
}
