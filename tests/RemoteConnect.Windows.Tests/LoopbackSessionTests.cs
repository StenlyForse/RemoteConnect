using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography.X509Certificates;
using RemoteConnect.Core.Protocol;
using RemoteConnect.Core.Security;
using RemoteConnect.Core.Session;
using RemoteConnect.Windows.Security;

namespace RemoteConnect.Windows.Tests;

/// <summary>Полный путь TCP → TLS → handshake → сообщения по loopback.</summary>
public sealed class LoopbackSessionTests : IDisposable
{
    private static readonly X509Certificate2 Certificate = HostCertificate.Import(HostCertificate.Create());

    private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
    private readonly AuthRateLimiter _limiter = new(TimeProvider.System);
    private readonly SessionSlot _slot = new();
    private readonly PasswordKey _key = PasswordVerifier.CreateKey("k7m-29x", iterations: 1000);

    public LoopbackSessionTests() => _listener.Start();

    private int Port => ((IPEndPoint)_listener.LocalEndpoint).Port;

    [Fact]
    public async Task CorrectPassword_EstablishesSession_AndMessagesFlowBothWays()
    {
        var hostTask = RunHostAsync();
        var (client, result) = await RunClientAsync("k7m-29x", SessionRole.Controller);
        var (host, hostResult) = await hostTask;

        Assert.Equal(AuthStatus.Ok, result.Status);
        Assert.Equal("host-pc", result.HostName);
        Assert.NotNull(hostResult);
        Assert.Equal(("friend", SessionRole.Controller), (hostResult.Hello.Name, hostResult.Hello.ClientRole));
        Assert.True(_slot.IsBusy);

        host!.Send(new ConsentResult(ConsentDecision.Allowed));
        Assert.Equal(ConsentDecision.Allowed, (await client.ReceiveAsync<ConsentResult>(TimeSpan.FromSeconds(5))).Decision);

        client.Send(new KeyInput(0x41, 0x1E, true, false));
        Assert.Equal(0x41, (await host.ReceiveAsync<KeyInput>(TimeSpan.FromSeconds(5))).VirtualKey);

        await client.CloseAsync(DisconnectReason.UserClosed);
        var info = await host.Completion.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(DisconnectReason.UserClosed, info.Reason);
        Assert.True(info.ByRemote);
    }

    [Fact]
    public async Task WrongPassword_ReportsAttemptsLeft_ThenBlocks()
    {
        for (var expectedLeft = 4; expectedLeft >= 1; expectedLeft--)
        {
            var hostTask = RunHostAsync();
            var (_, result) = await RunClientAsync("wrong", SessionRole.Controller);
            Assert.Null((await hostTask).Result);
            Assert.Equal((AuthStatus.WrongPassword, expectedLeft), (result.Status, result.AttemptsLeft));
        }

        var lastHost = RunHostAsync();
        var (_, blocked) = await RunClientAsync("wrong", SessionRole.Controller);
        await lastHost;
        Assert.Equal(AuthStatus.Blocked, blocked.Status);
        Assert.InRange(blocked.BlockedSeconds, 299, 300);

        // Даже с верным паролем — блокировка, пока её не снимут.
        var blockedHost = RunHostAsync();
        var (_, stillBlocked) = await RunClientAsync("k7m-29x", SessionRole.Controller);
        await blockedHost;
        Assert.Equal(AuthStatus.Blocked, stillBlocked.Status);

        _limiter.Unblock(IPAddress.Loopback);
        var okHost = RunHostAsync();
        var (_, ok) = await RunClientAsync("k7m-29x", SessionRole.Controller);
        await okHost;
        Assert.Equal(AuthStatus.Ok, ok.Status);
    }

    [Fact]
    public async Task SecondClient_IsBusy()
    {
        Assert.True(_slot.TryClaim());
        var hostTask = RunHostAsync();
        var (_, result) = await RunClientAsync("k7m-29x", SessionRole.Controlled);
        Assert.Null((await hostTask).Result);
        Assert.Equal(AuthStatus.Busy, result.Status);
    }

    public void Dispose() => _listener.Stop();

    private async Task<(Connection? Connection, HostHandshakeResult? Result)> RunHostAsync()
    {
        var tcp = await _listener.AcceptTcpClientAsync();
        var ssl = await SecureTransport.AuthenticateAsServerAsync(tcp.GetStream(), Certificate, default);
        var connection = new Connection(ssl, tcp.Client.RemoteEndPoint);
        connection.Start();
        var certHash = Fingerprint.Hash(Certificate.RawData);
        var remote = ((IPEndPoint)tcp.Client.RemoteEndPoint!).Address;
        var result = await Handshake.RunHostAsync(connection, remote, _key, certHash, _limiter, _slot, "host-pc", default);
        return (result is null ? null : connection, result);
    }

    private async Task<(Connection Connection, AuthResult Result)> RunClientAsync(string password, SessionRole role)
    {
        var tcp = await SecureTransport.ConnectTcpAsync("127.0.0.1", Port, TimeSpan.FromSeconds(5), default);
        var (ssl, certHash) = await SecureTransport.AuthenticateAsClientAsync(tcp.GetStream(), default);
        Assert.Equal(Fingerprint.Hash(Certificate.RawData), certHash);
        var connection = new Connection(ssl, tcp.Client.RemoteEndPoint);
        connection.Start();
        var result = await Handshake.RunClientAsync(connection, "friend", role, password, certHash, default);
        return (connection, result);
    }
}
