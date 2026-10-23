using System.Security;
using System.Text;
using System.Xml.Linq;

namespace RemoteConnect.Core.Network;

/// <summary>Сервис роутера, через который открываются порты.</summary>
public sealed record UpnpWanService(Uri ControlUrl, string ServiceType);

/// <summary>
/// Разбор и формирование сообщений UPnP IGD: ответы SSDP, описание устройства, SOAP-запросы (FR-NET-2).
/// </summary>
public static class UpnpProtocol
{
    public const string MulticastAddress = "239.255.255.250";
    public const int SsdpPort = 1900;

    public static readonly string[] SearchTargets =
    [
        "urn:schemas-upnp-org:device:InternetGatewayDevice:1",
        "urn:schemas-upnp-org:device:InternetGatewayDevice:2",
        "urn:schemas-upnp-org:service:WANIPConnection:1",
        "urn:schemas-upnp-org:service:WANPPPConnection:1",
    ];

    private static readonly string[] WanServicePrefixes =
    [
        "urn:schemas-upnp-org:service:WANIPConnection:",
        "urn:schemas-upnp-org:service:WANPPPConnection:",
    ];

    public static string BuildSearch(string searchTarget) =>
        $"M-SEARCH * HTTP/1.1\r\nHOST: {MulticastAddress}:{SsdpPort}\r\nMAN: \"ssdp:discover\"\r\nMX: 2\r\nST: {searchTarget}\r\n\r\n";

    /// <summary>Адрес описания устройства из заголовка LOCATION ответа SSDP (регистр заголовка не важен).</summary>
    public static Uri? ParseLocation(string ssdpResponse)
    {
        foreach (var line in ssdpResponse.Split('\n'))
        {
            var colon = line.IndexOf(':');
            if (colon > 0 && line[..colon].Trim().Equals("LOCATION", StringComparison.OrdinalIgnoreCase)
                && Uri.TryCreate(line[(colon + 1)..].Trim(), UriKind.Absolute, out var uri)
                && uri.Scheme == Uri.UriSchemeHttp)
            {
                return uri;
            }
        }

        return null;
    }

    /// <summary>Находит в описании устройства сервис WANIPConnection/WANPPPConnection.</summary>
    public static UpnpWanService? FindWanService(string descriptionXml, Uri location)
    {
        var doc = XDocument.Parse(descriptionXml);
        var baseText = doc.Descendants().FirstOrDefault(e => e.Name.LocalName == "URLBase")?.Value.Trim();
        var baseUri = !string.IsNullOrEmpty(baseText) && Uri.TryCreate(baseText, UriKind.Absolute, out var b) ? b : location;

        var services = doc.Descendants()
            .Where(e => e.Name.LocalName == "service")
            .Select(e => (
                Type: e.Elements().FirstOrDefault(c => c.Name.LocalName == "serviceType")?.Value.Trim() ?? string.Empty,
                Control: e.Elements().FirstOrDefault(c => c.Name.LocalName == "controlURL")?.Value.Trim() ?? string.Empty))
            .Where(s => s.Control.Length > 0 && WanServicePrefixes.Any(p => s.Type.StartsWith(p, StringComparison.Ordinal)))
            .OrderBy(s => s.Type.Contains("WANIPConnection", StringComparison.Ordinal) ? 0 : 1)
            .ToList();

        if (services.Count == 0)
        {
            return null;
        }

        var (type, control) = services[0];
        return Uri.TryCreate(baseUri, control, out var controlUrl) ? new UpnpWanService(controlUrl, type) : null;
    }

    public static string BuildSoapBody(string action, string serviceType, params (string Name, string Value)[] arguments)
    {
        var body = new StringBuilder();
        body.Append("<?xml version=\"1.0\"?>")
            .Append("<s:Envelope xmlns:s=\"http://schemas.xmlsoap.org/soap/envelope/\" s:encodingStyle=\"http://schemas.xmlsoap.org/soap/encoding/\">")
            .Append("<s:Body>")
            .Append($"<u:{action} xmlns:u=\"{serviceType}\">");
        foreach (var (name, value) in arguments)
        {
            body.Append($"<{name}>{SecurityElement.Escape(value)}</{name}>");
        }

        body.Append($"</u:{action}></s:Body></s:Envelope>");
        return body.ToString();
    }

    public static string? GetValue(string soapResponse, string elementName)
    {
        try
        {
            return XDocument.Parse(soapResponse).Descendants().FirstOrDefault(e => e.Name.LocalName == elementName)?.Value.Trim();
        }
        catch (System.Xml.XmlException)
        {
            return null;
        }
    }

    /// <summary>Код ошибки UPnP из SOAP Fault (например, 718 — порт занят, 725 — только постоянные правила).</summary>
    public static int? GetErrorCode(string soapResponse) =>
        int.TryParse(GetValue(soapResponse, "errorCode"), out var code) ? code : null;
}
