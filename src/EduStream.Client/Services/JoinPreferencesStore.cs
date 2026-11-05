using System.IO;
using System.Text.Json;
using EduStream.Core.Network;

namespace EduStream.Client.Services;

// 비밀번호·참가 티켓·재연결 토큰을 저장할 필드를 두지 않는다.
public sealed record JoinPreferences(bool Remember, string DisplayName, string HostAddress, int Port);

public interface IJoinPreferencesStore
{
    JoinPreferences? Load();
    void Save(JoinPreferences preferences);
}

public sealed class JoinPreferencesStore : IJoinPreferencesStore
{
    private readonly string _path;
    public JoinPreferencesStore(string? path = null) => _path = path ?? Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "EduStream", "client-join.json");

    public JoinPreferences? Load()
    {
        if (!File.Exists(_path)) return null;
        if (new FileInfo(_path).Length > 4096) throw new InvalidDataException("참가 설정 파일이 너무 큽니다.");
        var value = JsonSerializer.Deserialize<JoinPreferences>(File.ReadAllText(_path))
            ?? throw new InvalidDataException("참가 설정을 읽을 수 없습니다.");
        Validate(value);
        return value;
    }

    public void Save(JoinPreferences preferences)
    {
        Validate(preferences);
        // 기억 해제는 파일 자체에 남은 이름/IP도 비운다.
        var value = preferences.Remember ? preferences : new JoinPreferences(false, "", "", 5000);
        var directory = Path.GetDirectoryName(Path.GetFullPath(_path))!;
        Directory.CreateDirectory(directory);
        var temporary = Path.Combine(directory, Guid.NewGuid().ToString("N") + ".tmp");
        try
        {
            File.WriteAllText(temporary, JsonSerializer.Serialize(value));
            File.Move(temporary, _path, overwrite: true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }

    private static void Validate(JoinPreferences value)
    {
        if (!value.Remember) return;
        if (string.IsNullOrWhiteSpace(value.DisplayName) || value.DisplayName.Length > 128)
            throw new InvalidDataException("학생 이름이 올바르지 않습니다.");
        _ = LanSessionEndpoint.Create(value.HostAddress, value.Port);
    }
}
