using EduStream.Core.Common;

namespace EduStream.Server.ViewModels;

/// <summary>
/// 판서 엔진과 독립적인 도구 설정입니다. 선택만 보관하며 그리기/송출 성공 상태를 만들지 않습니다.
/// 실제 실행 명령과 화면 위 도구 모음 부착은 3번 엔진 계약 인계 후 연결합니다.
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

    // 엔진이 없는 상태를 사용 가능한 것처럼 바꾸지 않습니다. 실행 명령은 아직 제공하지 않습니다.
    public bool CanExecuteDrawing => false;
    public string EngineStatus => "도구 설정만 준비할 수 있습니다. 실제 판서·화면 위 배치·학생 송출은 엔진 연결 대기 중입니다.";
}
