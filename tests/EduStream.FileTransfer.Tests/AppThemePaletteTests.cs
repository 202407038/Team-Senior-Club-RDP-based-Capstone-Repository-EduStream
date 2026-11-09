using EduStream.Core.Common;

namespace EduStream.FileTransfer.Tests;

public sealed class AppThemePaletteTests
{
    [Theory]
    [InlineData("BrushBgPrimary", "#FAFAFA")]
    [InlineData("BrushBgSurface", "#FFFFFF")]
    [InlineData("BrushBgElevated", "#F3F3F3")]
    [InlineData("BrushBgInput", "#F8F8F8")]
    [InlineData("BrushBorder", "#D8D8D8")]
    [InlineData("BrushHover", "#ECECEC")]
    [InlineData("BrushChatOther", "#F5F5F5")]
    [InlineData("BrushListGround", "#FBFBFB")]
    [InlineData("BrushStudentCard", "#FBFBFB")]
    public void LightSurfacesUseBrighterNeutralWhites(string key, string expected)
    {
        var palette = AppThemePalette.Colors(true);
        Assert.Equal(expected, palette[key]);
        Assert.Equal(palette[key].Substring(1, 2), palette[key].Substring(3, 2));
        Assert.Equal(palette[key].Substring(3, 2), palette[key].Substring(5, 2));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void TextAndActionColorsRemainReadable(bool light)
    {
        var palette = AppThemePalette.Colors(light);
        foreach (var text in new[] { "BrushTextPrimary", "BrushTextSecondary", "BrushTextMuted", "BrushAccentPurple", "BrushAccentTeal" })
        foreach (var background in new[] { "BrushBgPrimary", "BrushBgSurface", "BrushBgInput", "BrushBgElevated", "BrushHover", "BrushListGround", "BrushStudentCard", "BrushChatSelf", "BrushChatOther", "BrushChatSystem" })
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
