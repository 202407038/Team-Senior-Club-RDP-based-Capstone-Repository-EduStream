namespace EduStream.Core.Collaboration;

public enum ControlPhase { Idle, Requested, Active, Revoked, Failed }

/// <summary>제어는 화면 펼침/크게 보기와 독립. Active는 native 승인 이벤트 이후에만 적용.</summary>
public sealed record RemoteControlState
{
    public ParticipantConnection Professor { get; }
    public ParticipantConnection Student { get; }
    public Guid RequestId { get; }
    public long PermissionRevision { get; }
    public ControlPhase Phase { get; }

    private RemoteControlState(ParticipantConnection professor, ParticipantConnection student,
        Guid requestId, long revision, ControlPhase phase)
        => (Professor, Student, RequestId, PermissionRevision, Phase) =
            (professor, student, requestId, revision, phase);

    public static RemoteControlState Request(ParticipantConnection professor,
        ParticipantSnapshot student, Guid requestId)
    {
        professor.Validate();
        student.Connection.Validate();
        CollaborationContract.RequireId(requestId, nameof(requestId));
        if (professor.Role != ParticipantRole.Professor ||
            student.Connection.Role != ParticipantRole.Student ||
            professor.SessionId != student.Connection.SessionId ||
            professor.ParticipantId == student.Connection.ParticipantId)
            throw new CollaborationException(CollaborationError.NotAuthorized);
        if (!student.Connected || !student.AllowViewing || !student.AllowControl)
            throw new CollaborationException(CollaborationError.PermissionDenied);
        if (student.PermissionRevision < 0)
            throw new CollaborationException(CollaborationError.InvalidRequest);
        return new(professor, student.Connection, requestId, student.PermissionRevision, ControlPhase.Requested);
    }

    public RemoteControlState Activate(Guid requestId, ParticipantSnapshot current)
    {
        if (Phase != ControlPhase.Requested || requestId != RequestId ||
            current.Connection != Student || current.PermissionRevision != PermissionRevision)
            throw new CollaborationException(CollaborationError.StaleConnection);
        if (!current.Connected || !current.AllowViewing || !current.AllowControl)
            throw new CollaborationException(CollaborationError.PermissionDenied);
        return new(Professor, Student, RequestId, PermissionRevision, ControlPhase.Active);
    }

    public RemoteControlState Revoke() =>
        new(Professor, Student, RequestId, PermissionRevision, ControlPhase.Revoked);

    public RemoteControlState Fail() =>
        Phase == ControlPhase.Revoked ? this :
        new(Professor, Student, RequestId, PermissionRevision, ControlPhase.Failed);
}

/// <summary>2번은 단일 대상·승인/회수를 직렬화하고 3번은 native 입력을 실제 차단합니다.</summary>
public interface IRemoteControlCoordinator
{
    event Action<RemoteControlState>? StateChanged;
    Task RequestAsync(ParticipantConnection target, CancellationToken cancellationToken = default);
    Task StopAsync(CancellationToken cancellationToken = default);
}

public enum ScreenDirection { ProfessorToStudents, StudentToProfessor }
public sealed record ScreenStreamKey(Guid SessionId, Guid SharingId, Guid ConnectionId,
    Guid SourceParticipantId, ScreenDirection Direction);
public sealed record MonitorDescriptor(string Id, string DisplayName, int Width, int Height,
    double DpiScaleX, double DpiScaleY);
public enum AnnotationTool { Pen, Line, Rectangle, Ellipse, Eraser }
public enum ToolbarDock { Top, Bottom, Left, Right }

/// <summary>불변 판서 상태. OFF/숨김을 구분하며 다시 표시해도 그리기 모드로 바뀌지 않습니다.</summary>
public sealed record AnnotationState(bool Drawing, bool Visible, long ContentRevision)
{
    public static AnnotationState Empty { get; } = new(false, true, 0);
    public AnnotationState SetDrawing(bool drawing) => this with { Drawing = drawing };
    public AnnotationState ToggleVisibility() => this with { Visible = !Visible };
    public AnnotationState ContentChanged() => this with { ContentRevision = checked(ContentRevision + 1) };
    public AnnotationState ClearAndStop() => ContentChanged() with { Drawing = false };
}

/// <summary>3번 공급, 5번 소비. 구현체가 UI/STA 호출과 native 수명을 책임집니다.</summary>
public interface ISharedScreenPresentation : IAsyncDisposable
{
    Task FitAsync(CancellationToken cancellationToken = default);
    Task ZoomAsync(double factor, double normalizedX, double normalizedY,
        CancellationToken cancellationToken = default);
    Task PanAsync(double normalizedDeltaX, double normalizedDeltaY,
        CancellationToken cancellationToken = default);
}

/// <summary>3번 판서 엔진 계약. 실행 취소에는 지우기/전체 지우기도 포함합니다.</summary>
public interface IAnnotationController
{
    AnnotationState State { get; }
    event Action<AnnotationState>? StateChanged;
    void SetDrawing(bool enabled);
    void ToggleVisibility();
    void SelectTool(AnnotationTool tool, uint argbColor, double thickness);
    void Undo();
    void Clear(bool stopDrawing);
}
