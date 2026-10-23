using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using RemoteConnect.Core.Network;

namespace RemoteConnect.Windows.Network;

/// <param name="LocalAddress">Адрес этого ПК в сети роутера — на него перенаправлен порт.</param>
public sealed record PortMappingResult(bool Success, IPAddress? RouterExternalAddress, IPAddress? LocalAddress, string? Error);

/// <summary>
/// Открытие порта на роутере через UPnP IGD (FR-NET-2).
/// Поиск роутера идёт через каждый физический интерфейс и напрямую на его шлюз:
/// при включённом VPN multicast-запрос иначе уходит в туннель и роутер не находится.
/// Аренда на 1 час с продлением каждые 50 минут: если приложение упадёт, порт закроется сам.
/// </summary>
public sealed class UpnpPortMapper : IAsyncDisposable
{
    private const string Description = "RemoteConnect";
    private const int ErrorConflict = 718;
    private const int ErrorOnlyPermanentLeases = 725;
    private static readonly TimeSpan Lease = TimeSpan.FromHours(1);
    private static readonly TimeSpan RenewEvery = TimeSpan.FromMinutes(50);
    private static readonly HttpClient Http = new(new HttpClientHandler { UseProxy = false }) { Timeout = TimeSpan.FromSeconds(5) };

    private Gateway? _gateway;
    private int _port;
    private CancellationTokenSource? _renewCts;

    public async Task<PortMappingResult> MapAsync(int port, TimeSpan discoveryTimeout, CancellationToken ct)
    {
        await UnmapAsync().ConfigureAwait(false);

        var gateway = await DiscoverAsync(discoveryTimeout, ct).ConfigureAwait(false);
        if (gateway is null)
        {
            return new PortMappingResult(false, null, null, "Роутер с поддержкой UPnP не найден (или UPnP в нём выключен).");
        }

        var error = await AddMappingAsync(gateway, port, (int)Lease.TotalSeconds, ct).ConfigureAwait(false);
        var lease = Lease;
        if (error == ErrorOnlyPermanentLeases)
        {
            error = await AddMappingAsync(gateway, port, 0, ct).ConfigureAwait(false);
            lease = TimeSpan.Zero;
        }

        var external = await GetExternalAddressAsync(gateway, ct).ConfigureAwait(false);
        if (error is not null)
        {
            var message = error == ErrorConflict
                ? $"Порт {port} на роутере уже перенаправлен на другое устройство. Выберите другой порт."
                : error == -1
                    ? "Роутер найден, но не дал открыть порт через UPnP (ограничение или ошибка прошивки)."
                    : $"Роутер отказал в открытии порта через UPnP (код {error}).";
            return new PortMappingResult(false, external, gateway.LocalAddress, message);
        }

        _gateway = gateway;
        _port = port;
        if (lease > TimeSpan.Zero)
        {
            _renewCts = new CancellationTokenSource();
            _ = RenewLoopAsync(gateway, port, _renewCts.Token);
        }

        return new PortMappingResult(true, external, gateway.LocalAddress, null);
    }

    public async Task UnmapAsync()
    {
        _renewCts?.Cancel();
        _renewCts?.Dispose();
        _renewCts = null;

        if (_gateway is { } gateway)
        {
            _gateway = null;
            try
            {
                await SoapAsync(gateway, "DeletePortMapping", CancellationToken.None,
                    ("NewRemoteHost", string.Empty),
                    ("NewExternalPort", _port.ToString()),
                    ("NewProtocol", "TCP")).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
            {
            }
        }
    }

    public async ValueTask DisposeAsync() => await UnmapAsync().ConfigureAwait(false);

    /// <summary>Пары (адрес этого ПК, шлюз) для активных физических интерфейсов IPv4.</summary>
    public static IReadOnlyList<(IPAddress Local, IPAddress Gateway)> GetLanInterfaces()
    {
        var result = new List<(IPAddress, IPAddress)>();
        foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
        {
            if (nic.OperationalStatus != OperationalStatus.Up
                || nic.NetworkInterfaceType is NetworkInterfaceType.Loopback or NetworkInterfaceType.Tunnel or NetworkInterfaceType.Ppp)
            {
                continue;
            }

            var props = nic.GetIPProperties();
            var gateway = props.GatewayAddresses.Select(g => g.Address).FirstOrDefault(a => a.AddressFamily == AddressFamily.InterNetwork && !a.Equals(IPAddress.Any));
            var local = props.UnicastAddresses.Select(u => u.Address).FirstOrDefault(a => a.AddressFamily == AddressFamily.InterNetwork);
            if (gateway is not null && local is not null)
            {
                result.Add((local, gateway));
            }
        }

        return result;
    }

    private static async Task<Gateway?> DiscoverAsync(TimeSpan timeout, CancellationToken ct)
    {
        var interfaces = GetLanInterfaces();
        if (interfaces.Count == 0)
        {
            return null;
        }

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(timeout);
        var tasks = interfaces.Select(i => DiscoverOnInterfaceAsync(i.Local, i.Gateway, cts.Token)).ToList();

        while (tasks.Count > 0)
        {
            var done = await Task.WhenAny(tasks).ConfigureAwait(false);
            tasks.Remove(done);
            if (await done.ConfigureAwait(false) is { } gateway)
            {
                await cts.CancelAsync().ConfigureAwait(false);
                return gateway;
            }
        }

        return null;
    }

    private static async Task<Gateway?> DiscoverOnInterfaceAsync(IPAddress local, IPAddress gatewayAddress, CancellationToken ct)
    {
        try
        {
            using var udp = new UdpClient(new IPEndPoint(local, 0));
            udp.Client.SetSocketOption(SocketOptionLevel.IP, SocketOptionName.MulticastInterface, local.GetAddressBytes());
            var multicast = new IPEndPoint(IPAddress.Parse(UpnpProtocol.MulticastAddress), UpnpProtocol.SsdpPort);
            var direct = new IPEndPoint(gatewayAddress, UpnpProtocol.SsdpPort);

            for (var attempt = 0; attempt < 2 && !ct.IsCancellationRequested; attempt++)
            {
                foreach (var target in UpnpProtocol.SearchTargets)
                {
                    var bytes = System.Text.Encoding.ASCII.GetBytes(UpnpProtocol.BuildSearch(target));
                    await udp.SendAsync(bytes, direct, ct).ConfigureAwait(false);
                    await udp.SendAsync(bytes, multicast, ct).ConfigureAwait(false);
                }

                using var roundCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                roundCts.CancelAfter(TimeSpan.FromSeconds(2));
                var tried = new HashSet<Uri>();
                while (true)
                {
                    UdpReceiveResult response;
                    try
                    {
                        response = await udp.ReceiveAsync(roundCts.Token).ConfigureAwait(false);
                    }
                    catch (OperationCanceledException)
                    {
                        break;
                    }

                    var location = UpnpProtocol.ParseLocation(System.Text.Encoding.ASCII.GetString(response.Buffer));
                    if (location is null || !tried.Add(location))
                    {
                        continue;
                    }

                    if (await ResolveServiceAsync(location, ct).ConfigureAwait(false) is { } service)
                    {
                        return new Gateway(service.ControlUrl, service.ServiceType, local);
                    }
                }
            }
        }
        catch (Exception ex) when (ex is SocketException or OperationCanceledException)
        {
        }

        return null;
    }

    private static async Task<UpnpWanService?> ResolveServiceAsync(Uri location, CancellationToken ct)
    {
        try
        {
            var xml = await Http.GetStringAsync(location, ct).ConfigureAwait(false);
            return UpnpProtocol.FindWanService(xml, location);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or System.Xml.XmlException)
        {
            return null;
        }
    }

    /// <returns>null — успех; иначе код ошибки UPnP (или -1, если роутер не ответил).</returns>
    private static async Task<int?> AddMappingAsync(Gateway gateway, int port, int leaseSeconds, CancellationToken ct)
    {
        try
        {
            var (ok, body) = await SoapAsync(gateway, "AddPortMapping", ct,
                ("NewRemoteHost", string.Empty),
                ("NewExternalPort", port.ToString()),
                ("NewProtocol", "TCP"),
                ("NewInternalPort", port.ToString()),
                ("NewInternalClient", gateway.LocalAddress.ToString()),
                ("NewEnabled", "1"),
                ("NewPortMappingDescription", Description),
                ("NewLeaseDuration", leaseSeconds.ToString())).ConfigureAwait(false);
            return ok ? null : UpnpProtocol.GetErrorCode(body) ?? -1;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            return -1;
        }
    }

    private static async Task<IPAddress?> GetExternalAddressAsync(Gateway gateway, CancellationToken ct)
    {
        try
        {
            var (ok, body) = await SoapAsync(gateway, "GetExternalIPAddress", ct).ConfigureAwait(false);
            return ok && IPAddress.TryParse(UpnpProtocol.GetValue(body, "NewExternalIPAddress"), out var address) ? address : null;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            return null;
        }
    }

    private static async Task<(bool Ok, string Body)> SoapAsync(Gateway gateway, string action, CancellationToken ct, params (string, string)[] arguments)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, gateway.ControlUrl)
        {
            Content = new StringContent(UpnpProtocol.BuildSoapBody(action, gateway.ServiceType, arguments), System.Text.Encoding.UTF8, "text/xml"),
        };
        request.Headers.TryAddWithoutValidation("SOAPAction", $"\"{gateway.ServiceType}#{action}\"");
        using var response = await Http.SendAsync(request, ct).ConfigureAwait(false);
        var body = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        return (response.IsSuccessStatusCode, body);
    }

    private async Task RenewLoopAsync(Gateway gateway, int port, CancellationToken ct)
    {
        try
        {
            while (true)
            {
                await Task.Delay(RenewEvery, ct).ConfigureAwait(false);
                await AddMappingAsync(gateway, port, (int)Lease.TotalSeconds, ct).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
        }
    }

    private sealed record Gateway(Uri ControlUrl, string ServiceType, IPAddress LocalAddress);
}
