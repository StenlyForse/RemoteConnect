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

public sealed record HostHandshakeResult(Hello Hello);

public static class Handshake
{
    /// <summary>2 — добавлен буфер обмена (v0.2); 3 — файлы через буфер обмена (v0.3).</summary>
    public const int ProtocolVersion = 3;
    public static readonly TimeSpan StepTimeout = TimeSpan.FromSeconds(15);

    /// <summary>
    /// Сторона хоста: Hello → проверки → AuthChallenge → AuthResponse → AuthResult.
    /// При успехе слот сессии занят и возвращается Hello клиента; иначе соединение закрыто и возвращается null.
    /// </summary>
    public static async Task<HostHandshakeResult?> RunHostAsync(
        Connection connection,
        IPAddress remoteAddress,
        PasswordKey passwordKey,
        byte[] certificateHash,
        AuthRateLimiter rateLimiter,
        ISessionSlot slot,
        string hostName,
        CancellationToken ct)
    {
        var hello = await connection.ReceiveAsync<Hello>(StepTimeout, ct).ConfigureAwait(false);

        if (hello.ProtocolVersion != ProtocolVersion)
        {
            return await RejectAsync(connection, new AuthResult(AuthStatus.VersionMismatch, 0, 0, hostName)).ConfigureAwait(false);
        }

        if (rateLimiter.IsBlocked(remoteAddress, out var remaining))
        {
            return await RejectAsync(connection, Blocked(remaining, hostName)).ConfigureAwait(false);
        }

        if (slot.IsBusy)
        {
            return await RejectAsync(connection, new AuthResult(AuthStatus.Busy, 0, 0, hostName)).ConfigureAwait(false);
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
            return await RejectAsync(connection, result).ConfigureAwait(false);
        }

        rateLimiter.RegisterSuccess(remoteAddress);

        if (!slot.TryClaim())
        {
            return await RejectAsync(connection, new AuthResult(AuthStatus.Busy, 0, 0, hostName)).ConfigureAwait(false);
        }

        connection.Send(new AuthResult(AuthStatus.Ok, 0, 0, hostName));
        return new HostHandshakeResult(hello);
    }

    /// <summary>Сторона клиента. Возвращает итоговый AuthResult (при отказе соединение закрывает хост).</summary>
    public static async Task<AuthResult> RunClientAsync(
        Connection connection,
        string clientName,
        SessionRole clientRole,
        string password,
        byte[] certificateHash,
        CancellationToken ct)
    {
        connection.Send(new Hello(ProtocolVersion, clientName, clientRole));

        var first = await ReceiveAnyAsync(connection, ct).ConfigureAwait(false);
        if (first is AuthResult early)
        {
            return early;
        }

        if (first is not AuthChallenge challenge)
        {
            throw new ProtocolException($"Ожидалось AuthChallenge, получено {first.Type}");
        }

        var key = await Task.Run(
            () => PasswordVerifier.DeriveKey(password, challenge.Salt, challenge.Iterations), ct).ConfigureAwait(false);
        connection.Send(new AuthResponse(PasswordVerifier.ComputeProof(key, challenge.Nonce, certificateHash)));

        return await connection.ReceiveAsync<AuthResult>(StepTimeout, ct).ConfigureAwait(false);
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

    private static async Task<HostHandshakeResult?> RejectAsync(Connection connection, AuthResult result)
    {
        connection.Send(result);
        await connection.CloseAsync(DisconnectReason.UserClosed).ConfigureAwait(false);
        return null;
    }
}
