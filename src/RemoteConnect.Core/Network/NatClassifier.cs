using System.Net;
using System.Net.Sockets;

namespace RemoteConnect.Core.Network;

public enum NatVerdict
{
    /// <summary>У роутера белый внешний IP.</summary>
    PublicAddress,

    /// <summary>Роутер получил серый адрес от провайдера — CGNAT (FR-NET-4).</summary>
    Cgnat,

    /// <summary>Внешний IP роутера неизвестен (нет UPnP).</summary>
    Unknown,
}

public static class NatClassifier
{
    /// <summary>
    /// Вердикт по внешнему IP роутера. Ответ внешнего сервиса для вердикта не используется:
    /// при включённом VPN это адрес VPN, а не роутера.
    /// </summary>
    public static NatVerdict Classify(IPAddress? routerExternal) => routerExternal switch
    {
        null => NatVerdict.Unknown,
        _ when IsPrivateOrShared(routerExternal) => NatVerdict.Cgnat,
        _ => NatVerdict.PublicAddress,
    };

    /// <summary>Адрес для строки подключения: белый IP роутера, иначе — ответ внешнего сервиса (FR-NET-3).</summary>
    public static IPAddress? ChooseConnectionAddress(IPAddress? routerExternal, IPAddress? publicAddress) =>
        Classify(routerExternal) == NatVerdict.PublicAddress ? routerExternal : publicAddress;

    /// <summary>Частные (RFC 1918), CGNAT (100.64.0.0/10), loopback и link-local адреса.</summary>
    public static bool IsPrivateOrShared(IPAddress address)
    {
        address = Normalize(address);
        if (IPAddress.IsLoopback(address))
        {
            return true;
        }

        if (address.AddressFamily == AddressFamily.InterNetworkV6)
        {
            return address.IsIPv6LinkLocal || address.IsIPv6SiteLocal || address.IsIPv6UniqueLocal;
        }

        var b = address.GetAddressBytes();
        return b[0] == 10
            || (b[0] == 172 && b[1] >= 16 && b[1] <= 31)
            || (b[0] == 192 && b[1] == 168)
            || (b[0] == 100 && b[1] >= 64 && b[1] <= 127)
            || (b[0] == 169 && b[1] == 254)
            || b[0] == 0;
    }

    private static IPAddress Normalize(IPAddress address) =>
        address.IsIPv4MappedToIPv6 ? address.MapToIPv4() : address;
}
