using System.IO;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using EduStream.Core.Collaboration;
using EduStream.Core.Logging;
using EduStream.Core.Network;

namespace EduStream.Client.Services;

public enum SecureJoinFailure
{
    /// <summary>교수자 IP 또는 포트 형식이 잘못됐습니다.</summary>
    InvalidAddress,
    /// <summary>TLS 서버 인증서가 없거나 만료됐거나 서버 용도가 아닙니다.</summary>
    InvalidCertificate,
    /// <summary>방 비밀번호가 틀렸습니다. 같은 연결로 다시 시도할 수 있습니다.</summary>
    PasswordRejected,
    /// <summary>비밀번호를 여러 번 틀려 잠시 시도가 막혔습니다.</summary>
    LockedOut,
    /// <summary>교수자 앱 버전이 달라 보호 채널을 쓸 수 없습니다.</summary>
    VersionMismatch,
    /// <summary>교수자 PC에 연결하지 못했거나 응답이 없습니다.</summary>
    Unreachable,
    /// <summary>재연결 토큰이 만료·사용됐거나 이미 다른 이름으로 쓰였습니다. 새로 참가해야 합니다.</summary>
    ReconnectRejected
}

public sealed class SecureJoinException(SecureJoinFailure failure) : Exception(failure.ToString())
{
    public SecureJoinFailure Failure { get; } = failure;
}

/// <summary>
/// 참가 인증을 마친 보호 채널. 기존 TCP 참가가 끝날 때까지 열어 두며, 참가자 연결과 수명을 같이합니다.
/// </summary>
public sealed class SecureSessionChannel : IAsyncDisposable
{
    internal SecureSessionChannel(SecureCollaborationConnection connection, string joinTicket, Guid sessionId)
    {
        Connection = connection;
        JoinTicket = joinTicket;
        SessionId = sessionId;
    }

    public SecureCollaborationConnection Connection { get; }

    /// <summary>기존 TCP 참가 요청에 한 번만 넣는 티켓입니다.</summary>
    public string JoinTicket { get; }

    public Guid SessionId { get; }

    /// <summary>참가 인증 이후 교수자가 보낸 프레임(파일·목록 등)입니다.</summary>
    public event Func<byte[], Task>? FrameReceived;

    internal Task DispatchAsync(byte[] frame) => FrameReceived?.Invoke(frame) ?? Task.CompletedTask;

    public ValueTask DisposeAsync() => Connection.DisposeAsync();
}

/// <summary>
/// 학생 앱 참가 인증. 입력한 교수자 IP에 TLS로 연결한 뒤 방 비밀번호를 보내고
/// 기존 TCP 참가에 쓸 티켓을 받습니다.
/// </summary>
public static class SecureRoomJoinClient
{
    /// <param name="reconnectToken">비정상 끊김 뒤 자동 재연결할 때만 넣습니다. 넣으면 비밀번호는 보내지 않습니다.</param>
    public static async Task<SecureSessionChannel> AuthenticateAsync(string host, int sessionPort,
        string displayName, ReadOnlyMemory<char> password, ILogSink logSink, TimeSpan? timeout = null,
        CancellationToken cancellationToken = default, string? reconnectToken = null)
    {
        ArgumentNullException.ThrowIfNull(logSink);
        if (sessionPort is < 1 or > 65534)
            throw new SecureJoinException(SecureJoinFailure.InvalidAddress);
        // 이전 수동 코드 방식(주석 보존):
        // if (!ConnectionCode.TryNormalize(connectionCode, out _))
        //     throw new SecureJoinException(SecureJoinFailure.InvalidCode);

        SecureCollaborationConnection connection;
        try
        {
            connection = await SecureCollaborationConnector.ConnectAsync(host, CollaborationPorts.ForSession(sessionPort),
                logSink, timeout, cancellationToken);
        }
        catch (CollaborationException ex)
        {
            throw new SecureJoinException(ex.Code switch
            {
                CollaborationError.NotAuthorized => SecureJoinFailure.InvalidCertificate,
                CollaborationError.InvalidRequest => SecureJoinFailure.InvalidAddress,
                CollaborationError.UnsupportedCapability => SecureJoinFailure.VersionMismatch,
                _ => SecureJoinFailure.Unreachable
            });
        }
        catch (Exception ex) when (ex is SocketException or IOException or OperationCanceledException)
        {
            if (cancellationToken.IsCancellationRequested) throw;
            throw new SecureJoinException(SecureJoinFailure.Unreachable);
        }

        var result = new TaskCompletionSource<RoomAuthResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        SecureSessionChannel? channel = null;
        connection.Start(frame =>
        {
            if (channel is not null) return channel.DispatchAsync(frame);
            try
            {
                if (CollaborationFrameInspector.PeekKind(frame) == CollaborationMessageKind.RoomAuthResult)
                    result.TrySetResult(CollaborationMessageCodec.Decode<RoomAuthResult>(frame, out _));
            }
            catch (CollaborationException)
            {
                logSink.Write("[SecureJoin] 잘못된 인증 응답 무시");
            }
            return Task.CompletedTask;
        });
        _ = connection.Completion.ContinueWith(
            _ => result.TrySetException(new SecureJoinException(SecureJoinFailure.Unreachable)), TaskScheduler.Default);

        var attemptId = Guid.NewGuid();
        var passwordBytes = new byte[Encoding.UTF8.GetByteCount(password.Span)];
        byte[]? frame = null;
        try
        {
            Encoding.UTF8.GetBytes(password.Span, passwordBytes);
            frame = CollaborationMessageCodec.Encode(Guid.NewGuid(),
                new RoomAuthRequest
                {
                    AttemptId = attemptId, DisplayName = displayName,
                    Password = reconnectToken is null ? passwordBytes : [], ReconnectToken = reconnectToken
                });
            await connection.SendAsync(frame, cancellationToken);

            var response = await result.Task.WaitAsync(timeout ?? CollaborationHandshake.DefaultTimeout, cancellationToken);
            if (response.AttemptId != attemptId) throw new SecureJoinException(SecureJoinFailure.Unreachable);
            if (!response.Accepted)
            {
                throw new SecureJoinException(response.Error switch
                {
                    CollaborationError.ResourceLimit => SecureJoinFailure.LockedOut,
                    CollaborationError.StaleConnection => SecureJoinFailure.ReconnectRejected,
                    _ => SecureJoinFailure.PasswordRejected
                });
            }

            channel = new SecureSessionChannel(connection, response.JoinTicket!, response.SessionId!.Value);
            logSink.Write("[SecureJoin] 참가 인증 완료");
            return channel;
        }
        catch (Exception ex)
        {
            await connection.DisposeAsync();
            if (ex is SecureJoinException) throw;
            if (ex is OperationCanceledException && cancellationToken.IsCancellationRequested) throw;
            throw new SecureJoinException(SecureJoinFailure.Unreachable);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(passwordBytes);
            if (frame is not null) CryptographicOperations.ZeroMemory(frame);
        }
    }
}
