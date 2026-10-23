using System.Net;
using Mono.Nat;

namespace RemoteConnect.Windows.Network;

public sealed record PortMappingResult(bool Success, IPAddress? RouterExternalAddress, string? Error);

/// <summary>
/// Открытие порта на роутере через UPnP IGD / NAT-PMP (FR-NET-2).
/// Аренда на 1 час с продлением каждые 50 минут: если приложение упадёт, порт закроется сам.
/// </summary>
public sealed class UpnpPortMapper : IAsyncDisposable
{
    private const string Description = "RemoteConnect";
    private static readonly TimeSpan Lease = TimeSpan.FromHours(1);
    private static readonly TimeSpan RenewEvery = TimeSpan.FromMinutes(50);
    private static readonly SemaphoreSlim DiscoveryLock = new(1, 1);

    private INatDevice? _device;
    private Mapping? _mapping;
    private CancellationTokenSource? _renewCts;

    public async Task<PortMappingResult> MapAsync(int port, TimeSpan discoveryTimeout, CancellationToken ct)
    {
        await UnmapAsync().ConfigureAwait(false);

        var device = await DiscoverAsync(discoveryTimeout, ct).ConfigureAwait(false);
        if (device is null)
        {
            return new PortMappingResult(false, null, "Роутер с поддержкой UPnP не найден (или UPnP в нём выключен).");
        }

        Mapping mapping;
        try
        {
            mapping = await CreateMappingAsync(device, port, (int)Lease.TotalSeconds).ConfigureAwait(false);
        }
        catch (MappingException)
        {
            // Часть роутеров поддерживает только постоянные правила.
            try
            {
                mapping = await CreateMappingAsync(device, port, 0).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is MappingException or IOException or TimeoutException)
            {
                return new PortMappingResult(false, null, $"Роутер отказал в открытии порта: {ex.Message}");
            }
        }
        catch (Exception ex) when (ex is IOException or TimeoutException or System.Net.Sockets.SocketException)
        {
            return new PortMappingResult(false, null, $"Ошибка UPnP: {ex.Message}");
        }

        _device = device;
        _mapping = mapping;

        IPAddress? external = null;
        try
        {
            external = await device.GetExternalIPAsync().ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is MappingException or IOException or TimeoutException or System.Net.Sockets.SocketException)
        {
        }

        if (mapping.Lifetime > 0)
        {
            _renewCts = new CancellationTokenSource();
            _ = RenewLoopAsync(device, port, _renewCts.Token);
        }

        return new PortMappingResult(true, external, null);
    }

    public async Task UnmapAsync()
    {
        _renewCts?.Cancel();
        _renewCts?.Dispose();
        _renewCts = null;

        if (_device is { } device && _mapping is { } mapping)
        {
            _device = null;
            _mapping = null;
            try
            {
                await device.DeletePortMapAsync(mapping).WaitAsync(TimeSpan.FromSeconds(3)).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is MappingException or IOException or TimeoutException or System.Net.Sockets.SocketException)
            {
            }
        }
    }

    public async ValueTask DisposeAsync() => await UnmapAsync().ConfigureAwait(false);

    private static async Task<Mapping> CreateMappingAsync(INatDevice device, int port, int lifetimeSeconds)
    {
        var mapping = new Mapping(Protocol.Tcp, port, port, lifetimeSeconds, Description);
        return await device.CreatePortMapAsync(mapping).WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
    }

    private static async Task<INatDevice?> DiscoverAsync(TimeSpan timeout, CancellationToken ct)
    {
        await DiscoveryLock.WaitAsync(ct).ConfigureAwait(false);
        var found = new TaskCompletionSource<INatDevice>(TaskCreationOptions.RunContinuationsAsynchronously);
        void OnFound(object? sender, DeviceEventArgs e) => found.TrySetResult(e.Device);

        NatUtility.DeviceFound += OnFound;
        try
        {
            NatUtility.StartDiscovery(NatProtocol.Upnp, NatProtocol.Pmp);
            return await found.Task.WaitAsync(timeout, ct).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            return null;
        }
        finally
        {
            NatUtility.DeviceFound -= OnFound;
            NatUtility.StopDiscovery();
            DiscoveryLock.Release();
        }
    }

    private async Task RenewLoopAsync(INatDevice device, int port, CancellationToken ct)
    {
        try
        {
            while (true)
            {
                await Task.Delay(RenewEvery, ct).ConfigureAwait(false);
                try
                {
                    _mapping = await CreateMappingAsync(device, port, (int)Lease.TotalSeconds).ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is MappingException or IOException or TimeoutException or System.Net.Sockets.SocketException)
                {
                }
            }
        }
        catch (OperationCanceledException)
        {
        }
    }
}
