namespace EduStream.Core.Common;

public static class AppThemePalette
{
    public static IReadOnlyDictionary<string, string> Colors(bool light) => new Dictionary<string, string>
    {
        ["BrushBgPrimary"] = light ? "#FAFAFA" : "#121212",
        ["BrushBgSurface"] = light ? "#FFFFFF" : "#1A1A1A",
        ["BrushBgElevated"] = light ? "#F3F3F3" : "#252525",
        ["BrushBgInput"] = light ? "#F8F8F8" : "#2D2D2D",
        ["BrushBorder"] = light ? "#D8D8D8" : "#3D3D3D",
        ["BrushAccentPurple"] = light ? "#00695F" : "#03DAC6",
        ["BrushAccentTeal"] = light ? "#00695F" : "#03DAC6",
        ["BrushAccentError"] = light ? "#AB2945" : "#F08B9E",
        ["BrushTextPrimary"] = light ? "#172723" : "#FFFFFF",
        ["BrushTextSecondary"] = light ? "#334D47" : "#B8B8B8",
        ["BrushTextMuted"] = light ? "#485F59" : "#A0A0A0",
        ["BrushHover"] = light ? "#ECECEC" : "#333333",
        ["BrushPrimaryHover"] = light ? "#00544B" : "#65E6D8",
        ["BrushChatSelf"] = light ? "#E0EBE5" : "#19332E",
        ["BrushChatOther"] = light ? "#F5F5F5" : "#2A2A2A",
        ["BrushChatSystem"] = light ? "#E6EBE5" : "#1E2A28",
        ["BrushListGround"] = light ? "#FBFBFB" : "#151515",
        ["BrushErrorGround"] = light ? "#FCE8ED" : "#2A1E22",
        ["BrushSuccessGround"] = light ? "#E4F2E9" : "#162B22",
        ["BrushSuccess"] = light ? "#176638" : "#6BDC95",
        ["BrushStudentCard"] = light ? "#FBFBFB" : "#181818",
        ["BrushSelection"] = light ? "#4400695F" : "#5503DAC6",
        ["BrushOnStrong"] = "#FFFFFF",
        ["BrushDrawingButton"] = "#00695F",
        ["BrushDrawingHover"] = "#00544B",
        ["BrushDangerButton"] = "#C62828",
        ["BrushDangerHover"] = "#D32F2F"
    };
}
