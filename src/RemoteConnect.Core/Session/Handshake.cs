using System.Net;
using RemoteConnect.Core.Protocol;
using RemoteConnect.Core.Security;

namespace RemoteConnect.Core.Session;

/// <summary>Единственный слот сессии на приложение (FR-NET-10).</summary>
public interface ISessionSlot
{
    bool IsBusy { get; }

    bool TryClaim();

    void Release();
}

public sealed class SessionSlot : ISessionSlot
{
    private int _busy;

    public bool IsBusy => Volatile.Read(ref _busy) == 1;

    public bool TryClaim() => Interlocked.CompareExchange(ref _busy, 1, 0) == 0;

    public void Release() => Volatile.Write(ref _busy, 0);
}

/// <summary>
/// Итог рукопожатия на хосте. <paramref name="PeerAppVersion"/> — версия программы клиента или null,
/// если клиент старше v0.4 и версию не сообщает.
/// </summary>
public sealed record HostHandshakeResult(AuthStatus Status, Hello Hello, string? PeerAppVersion)
{
    public bool IsAccepted => Status == AuthStatus.Ok;
}

/// <summary>Итог рукопожатия у клиента. <paramref name="PeerAppVersion"/> — версия хоста или null для хоста старше v0.4.</summary>
public sealed record ClientHandshakeResult(AuthResult Result, string? PeerAppVersion);

public static class Handshake
{
    /// <summary>2 — добавлен буфер обмена (v0.2); 3 — файлы через буфер обмена (v0.3); 4 — чат и обмен версиями (v0.4);
    /// 5 — качество под канал (v0.5).</summary>
    public const int ProtocolVersion = 5;

    /// <summary>С этой версии протокола стороны обмениваются <see cref="AppVersion"/> сразу после Hello.</summary>
    public const int FirstVersionExchangeProtocol = 4;
    public static readonly TimeSpan StepTimeout = TimeSpan.FromSeconds(15);

    /// <summary>
    /// Сторона хоста: Hello → [AppVersion ⇄ AppVersion] → проверки → AuthChallenge → AuthResponse → AuthResult.
    /// При успехе слот сессии занят; при отказе соединение закрыто. Клиенту старше v0.4 AppVersion не отправляется:
    /// незнакомое сообщение он счёл бы ошибкой протокола вместо «несовместимой версии».
    /// </summary>
    public static async Task<HostHandshakeResult> RunHostAsync(
        Connection connection,
        IPAddress remoteAddress,
        PasswordKey passwordKey,
        byte[] certificateHash,
        AuthRateLimiter rateLimiter,
        ISessionSlot slot,
        string hostName,
        string appVersion,
        CancellationToken ct)
    {
        var hello = await connection.ReceiveAsync<Hello>(StepTimeout, ct).ConfigureAwait(false);

        string? peerVersion = null;
        if (hello.ProtocolVersion >= FirstVersionExchangeProtocol)
        {
            connection.Send(new AppVersion(appVersion));
            peerVersion = (await connection.ReceiveAsync<AppVersion>(StepTimeout, ct).ConfigureAwait(false)).Version;
        }

        Task<HostHandshakeResult> Reject(AuthResult result) => RejectAsync(connection, result, hello, peerVersion);

        if (hello.ProtocolVersion != ProtocolVersion)
        {
            return await Reject(new AuthResult(AuthStatus.VersionMismatch, 0, 0, hostName)).ConfigureAwait(false);
        }

        if (rateLimiter.IsBlocked(remoteAddress, out var remaining))
        {
            return await Reject(Blocked(remaining, hostName)).ConfigureAwait(false);
        }

        if (slot.IsBusy)
        {
            return await Reject(new AuthResult(AuthStatus.Busy, 0, 0, hostName)).ConfigureAwait(false);
        }

        var nonce = PasswordVerifier.CreateNonce();
        connection.Send(new AuthChallenge(passwordKey.Salt, passwordKey.Iterations, nonce));
        var response = await connection.ReceiveAsync<AuthResponse>(StepTimeout, ct).ConfigureAwait(false);

        if (!PasswordVerifier.Verify(passwordKey, nonce, certificateHash, response.Proof))
        {
            var outcome = rateLimiter.RegisterFailure(remoteAddress);
            var result = outcome.IsBlocked
                ? Blocked(outcome.BlockedFor, hostName)
                : new AuthResult(AuthStatus.WrongPassword, outcome.AttemptsLeft, 0, hostName);
            return await Reject(result).ConfigureAwait(false);
        }

        rateLimiter.RegisterSuccess(remoteAddress);

        if (!slot.TryClaim())
        {
            return await Reject(new AuthResult(AuthStatus.Busy, 0, 0, hostName)).ConfigureAwait(false);
        }

        connection.Send(new AuthResult(AuthStatus.Ok, 0, 0, hostName));
        return new HostHandshakeResult(AuthStatus.Ok, hello, peerVersion);
    }

    /// <summary>
    /// Сторона клиента. Возвращает итоговый AuthResult (при отказе соединение закрывает хост).
    /// Хост v0.4+ первым присылает AppVersion; хост старше сразу отвечает AuthChallenge или AuthResult.
    /// </summary>
    public static async Task<ClientHandshakeResult> RunClientAsync(
        Connection connection,
        string clientName,
        SessionRole clientRole,
        string password,
        byte[] certificateHash,
        string appVersion,
        CancellationToken ct)
    {
        connection.Send(new Hello(ProtocolVersion, clientName, clientRole));

        string? peerVersion = null;
        var first = await ReceiveAnyAsync(connection, ct).ConfigureAwait(false);
        if (first is AppVersion hostVersion)
        {
            peerVersion = hostVersion.Version;
            connection.Send(new AppVersion(appVersion));
            first = await ReceiveAnyAsync(connection, ct).ConfigureAwait(false);
        }

        if (first is AuthResult early)
        {
            return new ClientHandshakeResult(early, peerVersion);
        }

        if (first is not AuthChallenge challenge)
        {
            throw new ProtocolException($"Ожидалось AuthChallenge, получено {first.Type}");
        }

        var key = await Task.Run(
            () => PasswordVerifier.DeriveKey(password, challenge.Salt, challenge.Iterations), ct).ConfigureAwait(false);
        connection.Send(new AuthResponse(PasswordVerifier.ComputeProof(key, challenge.Nonce, certificateHash)));

        var result = await connection.ReceiveAsync<AuthResult>(StepTimeout, ct).ConfigureAwait(false);
        return new ClientHandshakeResult(result, peerVersion);
    }

    private static async Task<Message> ReceiveAnyAsync(Connection connection, CancellationToken ct)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(StepTimeout);
        try
        {
            return await connection.ReceiveAsync(cts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new TimeoutException("Хост не ответил");
        }
    }

    private static AuthResult Blocked(TimeSpan remaining, string hostName) =>
        new(AuthStatus.Blocked, 0, (int)Math.Ceiling(remaining.TotalSeconds), hostName);

    private static async Task<HostHandshakeResult> RejectAsync(Connection connection, AuthResult result, Hello hello, string? peerVersion)
    {
        connection.Send(result);
        await connection.CloseAsync(DisconnectReason.UserClosed).ConfigureAwait(false);
        return new HostHandshakeResult(result.Status, hello, peerVersion);
    }
}
