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
        Assert.True(hostResult.IsAccepted);
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
            Assert.False((await hostTask).Result.IsAccepted);
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
        Assert.Equal(AuthStatus.Busy, (await hostTask).Result.Status);
        Assert.Equal(AuthStatus.Busy, result.Status);
    }

    [Fact]
    public async Task SameVersion_ExchangesAppVersions()
    {
        var hostTask = RunHostAsync();
        var (client, result, hostVersion) = await RunClientWithVersionAsync("k7m-29x");
        var (_, hostResult) = await hostTask;

        Assert.Equal(AuthStatus.Ok, result.Status);
        Assert.Equal(HostAppVersion, hostVersion);
        Assert.Equal(ClientAppVersion, hostResult.PeerAppVersion);
        await client.CloseAsync(DisconnectReason.UserClosed);
    }

    [Fact]
    public async Task OldClient_GetsMismatchInOldFormat_WithoutAppVersion()
    {
        var hostTask = RunHostAsync();
        var client = await ConnectRawAsync();
        client.Send(new Hello(3, "old-friend", SessionRole.Controller));

        // Хост v0.4 не шлёт клиенту v0.3 незнакомое AppVersion — первым приходит отказ.
        var first = await client.ReceiveAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5));
        var (_, hostResult) = await hostTask;

        Assert.Equal(AuthStatus.VersionMismatch, Assert.IsType<AuthResult>(first).Status);
        Assert.Equal(AuthStatus.VersionMismatch, hostResult.Status);
        Assert.Null(hostResult.PeerAppVersion);
    }

    [Fact]
    public async Task OldHost_ClientReportsMismatchWithUnknownVersion()
    {
        var hostTask = AcceptRawAsync();
        var clientTask = RunClientWithVersionAsync("k7m-29x");
        var host = await hostTask;

        // Хост v0.3: читает Hello и сразу отказывает, AppVersion не присылает.
        Assert.Equal(Handshake.ProtocolVersion, (await host.ReceiveAsync<Hello>(TimeSpan.FromSeconds(5))).ProtocolVersion);
        host.Send(new AuthResult(AuthStatus.VersionMismatch, 0, 0, "old-host"));
        var (_, result, hostVersion) = await clientTask;

        Assert.Equal(AuthStatus.VersionMismatch, result.Status);
        Assert.Null(hostVersion);
        await host.CloseAsync(DisconnectReason.UserClosed);
    }

    [Fact]
    public async Task V04Client_BothSidesSeeEachOthersVersion()
    {
        var hostTask = RunHostAsync();
        var client = await ConnectRawAsync();
        client.Send(new Hello(4, "friend-0.4", SessionRole.Controller));

        // Клиент v0.4 уже умеет обмен версиями — хост v0.5 называет ему свою, отказ с номером собеседника.
        Assert.Equal(HostAppVersion, (await client.ReceiveAsync<AppVersion>(TimeSpan.FromSeconds(5))).Version);
        client.Send(new AppVersion("0.4.0"));
        var result = await client.ReceiveAsync<AuthResult>(TimeSpan.FromSeconds(5));
        var (_, hostResult) = await hostTask;

        Assert.Equal(AuthStatus.VersionMismatch, result.Status);
        Assert.Equal((AuthStatus.VersionMismatch, "0.4.0"), (hostResult.Status, hostResult.PeerAppVersion));
    }

    [Fact]
    public async Task NewerClient_BothSidesSeeEachOthersVersion()
    {
        var hostTask = RunHostAsync();
        var client = await ConnectRawAsync();
        client.Send(new Hello(Handshake.ProtocolVersion + 1, "new-friend", SessionRole.Controller));

        Assert.Equal(HostAppVersion, (await client.ReceiveAsync<AppVersion>(TimeSpan.FromSeconds(5))).Version);
        client.Send(new AppVersion("0.5.0"));
        var result = await client.ReceiveAsync<AuthResult>(TimeSpan.FromSeconds(5));
        var (_, hostResult) = await hostTask;

        Assert.Equal(AuthStatus.VersionMismatch, result.Status);
        Assert.Equal((AuthStatus.VersionMismatch, "0.5.0"), (hostResult.Status, hostResult.PeerAppVersion));
    }

    public void Dispose() => _listener.Stop();

    private const string HostAppVersion = "0.4.0-host";
    private const string ClientAppVersion = "0.4.0-client";

    private async Task<(Connection? Connection, HostHandshakeResult Result)> RunHostAsync()
    {
        var connection = await AcceptRawAsync();
        var certHash = Fingerprint.Hash(Certificate.RawData);
        var result = await Handshake.RunHostAsync(connection, IPAddress.Loopback, _key, certHash, _limiter, _slot, "host-pc", HostAppVersion, default);
        return (result.IsAccepted ? connection : null, result);
    }

    private async Task<(Connection Connection, AuthResult Result)> RunClientAsync(string password, SessionRole role)
    {
        var (connection, result, _) = await RunClientWithVersionAsync(password, role);
        return (connection, result);
    }

    private async Task<(Connection Connection, AuthResult Result, string? HostVersion)> RunClientWithVersionAsync(
        string password, SessionRole role = SessionRole.Controller)
    {
        var connection = await ConnectRawAsync();
        var certHash = Fingerprint.Hash(Certificate.RawData);
        var (result, hostVersion) = await Handshake.RunClientAsync(connection, "friend", role, password, certHash, ClientAppVersion, default);
        return (connection, result, hostVersion);
    }

    /// <summary>Принятое TLS-соединение без рукопожатия — для имитации хоста другой версии.</summary>
    private async Task<Connection> AcceptRawAsync()
    {
        var tcp = await _listener.AcceptTcpClientAsync();
        var ssl = await SecureTransport.AuthenticateAsServerAsync(tcp.GetStream(), Certificate, default);
        var connection = new Connection(ssl, tcp.Client.RemoteEndPoint);
        connection.Start();
        return connection;
    }

    /// <summary>Клиентское TLS-соединение без рукопожатия — для имитации клиента другой версии.</summary>
    private async Task<Connection> ConnectRawAsync()
    {
        var tcp = await SecureTransport.ConnectTcpAsync("127.0.0.1", Port, TimeSpan.FromSeconds(5), default);
        var (ssl, certHash) = await SecureTransport.AuthenticateAsClientAsync(tcp.GetStream(), default);
        Assert.Equal(Fingerprint.Hash(Certificate.RawData), certHash);
        var connection = new Connection(ssl, tcp.Client.RemoteEndPoint);
        connection.Start();
        return connection;
    }
}
