using System.Collections.ObjectModel;
using EduStream.Client.Services;
using EduStream.Core.Common;
using EduStream.Core.Network;

namespace EduStream.Client.ViewModels;

public sealed partial class ClientViewModel
{
    private readonly ITeacherDiscoveryClient _teacherDiscovery;
    private CancellationTokenSource? _discoveryCancellation;
    public ObservableCollection<DiscoveredTeacher> DiscoveredTeachers { get; } = [];
    public RelayCommand FindTeachersCommand { get; }
    private bool _isFindingTeachers;
    public bool IsFindingTeachers
    {
        get => _isFindingTeachers;
        private set { SetProperty(ref _isFindingTeachers, value); FindTeachersCommand.RaiseCanExecuteChanged(); }
    }
    private string _discoveryStatus = "같은 LAN의 교수자를 찾거나 아래 IP를 직접 입력하세요.";
    public string DiscoveryStatus { get => _discoveryStatus; private set => SetProperty(ref _discoveryStatus, value); }
    private DiscoveredTeacher? _selectedTeacher;
    public DiscoveredTeacher? SelectedTeacher
    {
        get => _selectedTeacher;
        set
        {
            if (IsConnected || IsConnecting || !SetProperty(ref _selectedTeacher, value) || value is null) return;
            HostAddress = value.Address; Port = value.Port; RoomPasswordRequired = value.PasswordRequired;
            DiscoveryStatus = $"선택: {value.Label}. 아래 정보를 확인한 뒤 세션에 참가하세요.";
        }
    }
    public async Task FindTeachersAsync()
    {
        if (!FindTeachersCommand.CanExecute(null) || _disposing) return;
        using var cancellation = new CancellationTokenSource();
        _discoveryCancellation = cancellation;
        IsFindingTeachers = true;
        DiscoveryStatus = "교수자를 찾는 중입니다… (약 3초)";
        try
        {
            var results = await _teacherDiscovery.FindAsync(cancellation.Token);
            if (_disposing || cancellation.IsCancellationRequested || IsConnected || IsConnecting) return;
            SelectedTeacher = null;
            DiscoveredTeachers.Clear();
            foreach (var teacher in results) DiscoveredTeachers.Add(teacher);
            DiscoveryStatus = results.Count == 0
                ? "교수자를 찾지 못했습니다. 같은 LAN인지 확인하거나 IP를 직접 입력하세요."
                : $"교수자 {results.Count}개를 찾았습니다. 참가할 PC를 선택하세요.";
        }
        catch (OperationCanceledException) { DiscoveryStatus = "검색을 취소했습니다. 입력한 접속 정보는 유지됩니다."; }
        catch (Exception ex)
        {
            DiscoveryStatus = "자동 찾기를 사용할 수 없습니다. 교수자 IP를 직접 입력해 주세요.";
            _logSink.Write($"[Discovery] 검색 실패: {ex.GetType().Name}");
        }
        finally { _discoveryCancellation = null; IsFindingTeachers = false; }
    }
}
