using System.ComponentModel;
using System.Diagnostics;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;

namespace RemoteConnect.Windows.Network;

/// <summary>Определение адресов и проверка доступности (FR-NET-3, FR-NET-6, FR-NET-8).</summary>
public static class NetworkInfo
{
    private static readonly string[] PublicIpServices = ["https://api.ipify.org", "https://icanhazip.com"];
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(5) };

    /// <summary>Публичный IP по данным внешнего сервиса; null — не удалось определить.</summary>
    public static async Task<IPAddress?> GetPublicAddressAsync(CancellationToken ct)
    {
        foreach (var url in PublicIpServices)
        {
            try
            {
                var text = await Http.GetStringAsync(url, ct).ConfigureAwait(false);
                if (IPAddress.TryParse(text.Trim(), out var address))
                {
                    return address;
                }
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
            {
            }
        }

        return null;
    }

    /// <summary>IPv4-адреса активных сетевых интерфейсов (включая VPN вроде Tailscale/ZeroTier).</summary>
    public static IReadOnlyList<(string Interface, IPAddress Address)> GetLocalAddresses()
    {
        var result = new List<(string, IPAddress)>();
        foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
        {
            if (nic.OperationalStatus != OperationalStatus.Up || nic.NetworkInterfaceType == NetworkInterfaceType.Loopback)
            {
                continue;
            }

            foreach (var unicast in nic.GetIPProperties().UnicastAddresses)
            {
                if (unicast.Address.AddressFamily == AddressFamily.InterNetwork)
                {
                    result.Add((nic.Name, unicast.Address));
                }
            }
        }

        return result;
    }

    /// <summary>
    /// Пробное подключение к собственному внешнему адресу. Успех доказывает доступность,
    /// неудача ничего не доказывает: роутер может не поддерживать hairpin NAT (В1).
    /// </summary>
    public static async Task<bool> TryConnectAsync(IPAddress address, int port, TimeSpan timeout, CancellationToken ct)
    {
        using var client = new TcpClient(address.AddressFamily);
        try
        {
            await client.ConnectAsync(address, port, ct).AsTask().WaitAsync(timeout, ct).ConfigureAwait(false);
            return client.Connected;
        }
        catch (Exception ex) when (ex is SocketException or TimeoutException)
        {
            return false;
        }
    }

    /// <summary>Добавляет входящее правило брандмауэра (FR-NET-7). Требует подтверждения UAC.</summary>
    public static bool TryAddFirewallRule(int port, string programPath, out string? error)
    {
        error = null;
        var arguments = $"advfirewall firewall add rule name=\"RemoteConnect TCP {port}\" dir=in action=allow protocol=TCP localport={port} program=\"{programPath}\" enable=yes";
        try
        {
            using var process = Process.Start(new ProcessStartInfo("netsh", arguments)
            {
                UseShellExecute = true,
                Verb = "runas",
                WindowStyle = ProcessWindowStyle.Hidden,
            });
            if (process is null)
            {
                error = "Не удалось запустить netsh";
                return false;
            }

            process.WaitForExit(15_000);
            if (process.ExitCode != 0)
            {
                error = $"netsh завершился с кодом {process.ExitCode}";
                return false;
            }

            return true;
        }
        catch (Win32Exception ex) when (ex.NativeErrorCode == 1223)
        {
            error = "Действие отменено";
            return false;
        }
        catch (Win32Exception ex)
        {
            error = ex.Message;
            return false;
        }
    }
}
