using System.Net;
using System.Net.Sockets;

namespace RemoteConnect.Core.Network;

public enum NatVerdict
{
    /// <summary>Внешний IP роутера совпадает с публичным — белый IP.</summary>
    PublicAddress,

    /// <summary>Роутер получил серый адрес от провайдера — CGNAT (FR-NET-4).</summary>
    Cgnat,

    /// <summary>Данных недостаточно (нет UPnP или нет ответа сервиса определения IP).</summary>
    Unknown,
}

public static class NatClassifier
{
    public static NatVerdict Classify(IPAddress? routerExternal, IPAddress? publicAddress)
    {
        if (routerExternal is not null && IsPrivateOrShared(routerExternal))
        {
            return NatVerdict.Cgnat;
        }

        if (routerExternal is null || publicAddress is null)
        {
            return NatVerdict.Unknown;
        }

        return Normalize(routerExternal).Equals(Normalize(publicAddress)) ? NatVerdict.PublicAddress : NatVerdict.Cgnat;
    }

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
