using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography.X509Certificates;
using RemoteConnect.App.Sessions;
using RemoteConnect.Core.Network;
using RemoteConnect.Core.Protocol;
using RemoteConnect.Core.Security;
using RemoteConnect.Core.Session;
using RemoteConnect.Windows.Network;
using Serilog;

namespace RemoteConnect.App.Services;

public enum ProbeState
{
    NotStarted,
    InProgress,
    Ok,
    Failed,
}

public sealed record HostSnapshot(
    bool Running,
    int Port,
    string? OneTimePassword,
    string? Error,
    ProbeState Upnp,
    string? UpnpError,
    IPAddress? RouterAddress,
    IPAddress? PublicAddress,
    NatVerdict Nat,
    ProbeState Reachability,
    string? VpnInterface = null,
    IPAddress? LanAddress = null)
{
    public static readonly HostSnapshot Stopped = new(false, 0, null, null, ProbeState.NotStarted, null, null, null, NatVerdict.Unknown, ProbeState.NotStarted);

    /// <summary>Адрес для друга (FR-NET-3).</summary>
    public IPAddress? ConnectionAddress => NatClassifier.ChooseConnectionAddress(RouterAddress, PublicAddress);
}

/// <summary>
/// Режим ожидания подключений: TCP-порт, UPnP, диагностика сети, авто-остановка,
/// приём подключений и авторизация (FR-NET-1..11, FR-SEC-1..4, FR-SEC-10).
/// </summary>
public sealed class HostService(
    SettingsService settings,
    X509Certificate2 certificate,
    AuthRateLimiter rateLimiter,
    SessionManager sessions) : IAsyncDisposable
{
    private readonly byte[] _certificateHash = Fingerprint.Hash(certificate.RawData);
    private readonly UpnpPortMapper _upnp = new();
    private readonly SemaphoreSlim _gate = new(1, 1);
    private TcpListener? _listener;
    private CancellationTokenSource? _cts;
    private AutoStopTimer? _autoStop;
    private PasswordKey? _passwordKey;
    private HostSnapshot _snapshot = HostSnapshot.Stopped;

    public event Action<HostSnapshot>? Changed;

    /// <summary>Хост остановлен автоматически по таймеру.</summary>
    public event Action? AutoStopped;

    public HostSnapshot Snapshot => _snapshot;

    public TimeSpan? AutoStopRemaining => _autoStop?.Remaining;

    public string CertificateFingerprint => Fingerprint.Format(_certificateHash);

    public async Task StartAsync()
    {
        await _gate.WaitAsync();
        try
        {
            if (_snapshot.Running)
            {
                return;
            }

            var config = settings.Current;
            string? oneTimePassword = null;
            if (config.PasswordMode == PasswordMode.OneTime)
            {
                oneTimePassword = OneTimePassword.Generate();
                var password = oneTimePassword;
                _passwordKey = await Task.Run(() => PasswordVerifier.CreateKey(password));
            }
            else
            {
                _passwordKey = settings.GetPermanentPasswordKey();
                if (_passwordKey is null)
                {
                    Publish(HostSnapshot.Stopped with { Error = "Задайте постоянный пароль или выберите одноразовый." });
                    return;
                }
            }

            var listener = new TcpListener(IPAddress.IPv6Any, config.Port);
            listener.Server.DualMode = true;
            try
            {
                listener.Start();
            }
            catch (SocketException ex)
            {
                Log.Warning(ex, "Не удалось открыть порт {Port}", config.Port);
                Publish(HostSnapshot.Stopped with { Error = $"Порт {config.Port} занят или недоступен: {ex.Message}" });
                return;
            }

            _listener = listener;
            _cts = new CancellationTokenSource();
            _autoStop = new AutoStopTimer(TimeProvider.System, TimeSpan.FromMinutes(Math.Max(0, config.AutoStopMinutes)), OnAutoStop);
            if (!sessions.Slot.IsBusy)
            {
                _autoStop.Restart();
            }

            Publish(new HostSnapshot(true, config.Port, oneTimePassword, null, ProbeState.InProgress, null, null, null, NatVerdict.Unknown, ProbeState.NotStarted));
            Log.Information("Хост запущен на порту {Port}, режим пароля {Mode}", config.Port, config.PasswordMode);

            var ct = _cts.Token;
            _ = Task.Run(() => AcceptLoopAsync(listener, ct));
            _ = Task.Run(() => DiagnoseNetworkAsync(config.Port, ct));
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task StopAsync()
    {
        await _gate.WaitAsync();
        try
        {
            if (!_snapshot.Running)
            {
                return;
            }

            _cts?.Cancel();
            _listener?.Stop();
            _listener = null;
            _autoStop?.Dispose();
            _autoStop = null;
            _passwordKey = null;
            await _upnp.UnmapAsync();
            Publish(HostSnapshot.Stopped);
            Log.Information("Хост остановлен");
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>Вызывается после окончания любой сессии: перезапускает таймер авто-остановки.</summary>
    public void OnSessionEnded()
    {
        if (_snapshot.Running)
        {
            _autoStop?.Restart();
        }
    }

    public void OnSessionStarted() => _autoStop?.Pause();

    public async ValueTask DisposeAsync()
    {
        await StopAsync();
        await _upnp.DisposeAsync();
    }

    private void OnAutoStop()
    {
        if (sessions.Slot.IsBusy)
        {
            // Идёт сессия, начатая с этого ПК как клиента: ждём следующий интервал.
            _autoStop?.Restart();
            return;
        }

        Log.Information("Авто-остановка хоста: никто не подключился");
        _ = StopAsync().ContinueWith(_ => AutoStopped?.Invoke(), TaskScheduler.Default);
    }

    private async Task AcceptLoopAsync(TcpListener listener, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            TcpClient client;
            try
            {
                client = await listener.AcceptTcpClientAsync(ct);
            }
            catch (Exception ex) when (ex is OperationCanceledException or ObjectDisposedException or SocketException)
            {
                return;
            }

            client.NoDelay = true;
            _ = Task.Run(() => HandleClientAsync(client, ct), ct);
        }
    }

    private async Task HandleClientAsync(TcpClient client, CancellationToken ct)
    {
        var remote = (IPEndPoint?)client.Client.RemoteEndPoint;
        var remoteAddress = remote?.Address is { } a && a.IsIPv4MappedToIPv6 ? a.MapToIPv4() : remote?.Address ?? IPAddress.None;
        Connection? connection = null;
        var handedOff = false;
        try
        {
            var ssl = await SecureTransport.AuthenticateAsServerAsync(client.GetStream(), certificate, ct);
            connection = new Connection(ssl, remote);
            connection.Start();

            var key = _passwordKey ?? throw new OperationCanceledException();
            var result = await Handshake.RunHostAsync(
                connection, remoteAddress, key, _certificateHash, rateLimiter, sessions.Slot, settings.Current.DisplayName, ct);

            if (result is null)
            {
                Log.Information("Отклонено подключение от {Address}", remoteAddress);
                return;
            }

            Log.Information("Авторизовано подключение от {Address} ({Name}), роль клиента {Role}", remoteAddress, result.Hello.Name, result.Hello.ClientRole);
            handedOff = true;
            var localRole = result.Hello.ClientRole == SessionRole.Controller ? SessionRole.Controlled : SessionRole.Controller;
            OnSessionStarted();
            try
            {
                await sessions.RunOnUiAsync(connection, localRole, result.Hello.Name, CertificateFingerprint);
            }
            finally
            {
                OnSessionEnded();
            }
        }
        catch (Exception ex) when (ex is IOException or System.Security.Authentication.AuthenticationException
                                       or TimeoutException or ProtocolException or ConnectionClosedException
                                       or OperationCanceledException or SocketException)
        {
            Log.Debug(ex, "Подключение от {Address} прервано до начала сессии", remoteAddress);
        }
        finally
        {
            if (!handedOff)
            {
                if (connection is not null)
                {
                    await connection.DisposeAsync();
                }

                client.Dispose();
            }
        }
    }

    private async Task DiagnoseNetworkAsync(int port, CancellationToken ct)
    {
        var upnpTask = _upnp.MapAsync(port, TimeSpan.FromSeconds(5), ct);
        var publicTask = NetworkInfo.GetPublicAddressAsync(ct);

        PortMappingResult upnp;
        try
        {
            upnp = await upnpTask;
        }
        catch (OperationCanceledException)
        {
            return;
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Ошибка UPnP");
            upnp = new PortMappingResult(false, null, null, $"Ошибка UPnP: {ex.Message}");
        }

        var publicAddress = await publicTask;
        if (ct.IsCancellationRequested)
        {
            return;
        }

        var nat = NatClassifier.Classify(upnp.RouterExternalAddress);
        var vpn = NetworkInfo.DetectVpnInterface();
        var address = NatClassifier.ChooseConnectionAddress(upnp.RouterExternalAddress, publicAddress);
        Log.Information("UPnP: {Ok} {Error}; локальный {Local}, внешний IP роутера {Router}, публичный IP {Public}, вердикт {Nat}, VPN {Vpn}",
            upnp.Success, upnp.Error, upnp.LocalAddress, upnp.RouterExternalAddress, publicAddress, nat, vpn);

        // С включённым VPN самопроверка бессмысленна: ответ уйдёт в туннель.
        // Проверяем и без UPnP: порт мог быть проброшен вручную.
        var canProbe = address is not null && nat != NatVerdict.Cgnat && vpn is null;
        Update(s => s with
        {
            Upnp = upnp.Success ? ProbeState.Ok : ProbeState.Failed,
            UpnpError = upnp.Error,
            RouterAddress = upnp.RouterExternalAddress,
            PublicAddress = publicAddress,
            Nat = nat,
            VpnInterface = vpn,
            LanAddress = upnp.LocalAddress ?? UpnpPortMapper.GetLanInterfaces().Select(i => i.Local).FirstOrDefault(),
            Reachability = canProbe ? ProbeState.InProgress : ProbeState.NotStarted,
        });

        if (canProbe)
        {
            var reachable = await NetworkInfo.TryConnectAsync(address!, port, TimeSpan.FromSeconds(3), ct);
            if (!ct.IsCancellationRequested)
            {
                Update(s => s with { Reachability = reachable ? ProbeState.Ok : ProbeState.Failed });
            }
        }
    }

    private void Update(Func<HostSnapshot, HostSnapshot> change)
    {
        if (_snapshot.Running)
        {
            Publish(change(_snapshot));
        }
    }

    private void Publish(HostSnapshot snapshot)
    {
        _snapshot = snapshot;
        Changed?.Invoke(snapshot);
    }
}
