using EduStream.Core.Common;

namespace EduStream.Server.ViewModels;

/// <summary>
/// 판서 엔진과 독립적인 도구 설정입니다. 선택만 보관하며 그리기/송출 성공 상태를 만들지 않습니다.
/// 실제 실행 명령은 AnnotationDesktopWindow가 엔진에 연결합니다.
/// </summary>
public sealed class AnnotationToolsViewModel : ObservableObject
{
    public IReadOnlyList<string> Tools { get; } = Array.AsReadOnly(new[] { "자유선", "직선", "사각형", "타원", "지우개" });
    public IReadOnlyList<string> Colors { get; } = Array.AsReadOnly(new[] { "노랑", "빨강", "파랑", "초록", "검정", "흰색" });
    public IReadOnlyList<string> Placements { get; } = Array.AsReadOnly(new[] { "상단", "하단", "왼쪽", "오른쪽" });
    private string _tool = "자유선";
    private string _color = "노랑";
    private string _placement = "하단";
    private double _strokeWidth = 3;

    public string Tool
    {
        get => _tool;
        set { if (Tools.Contains(value)) SetProperty(ref _tool, value); }
    }
    public string Color
    {
        get => _color;
        set { if (Colors.Contains(value)) SetProperty(ref _color, value); }
    }
    public string Placement
    {
        get => _placement;
        set { if (Placements.Contains(value)) SetProperty(ref _placement, value); }
    }
    public double StrokeWidth
    {
        get => _strokeWidth;
        set { if (double.IsFinite(value)) SetProperty(ref _strokeWidth, Math.Clamp(Math.Round(value), 1, 20)); }
    }

    // 설정 패널 자체는 실행 명령을 제공하지 않습니다. 실제 실행은 공유 중 화면 위 도구 모음에서 합니다.
    public bool CanExecuteDrawing => false;
    public string EngineStatus => "공유 시작 후 위 판서 버튼을 누르세요. 선택 모니터의 화면 위 도구 모음에서 그리기·숨김·실행 취소·지우기를 사용할 수 있습니다.";
}
