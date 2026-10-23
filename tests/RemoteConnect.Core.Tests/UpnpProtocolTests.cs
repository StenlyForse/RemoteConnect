using RemoteConnect.Core.Network;

namespace RemoteConnect.Core.Tests;

public class UpnpProtocolTests
{
    private static readonly Uri Location = new("http://192.168.0.1:52869/picsdesc.xml");

    // Сокращённое описание роутера Realtek/miniupnpd: WAN-сервис вложен во встроенные устройства.
    private const string Description = """
        <?xml version="1.0"?>
        <root xmlns="urn:schemas-upnp-org:device-1-0">
          <device>
            <deviceType>urn:schemas-upnp-org:device:InternetGatewayDevice:1</deviceType>
            <serviceList>
              <service>
                <serviceType>urn:schemas-upnp-org:service:Layer3Forwarding:1</serviceType>
                <controlURL>/upnp/control/L3F</controlURL>
              </service>
            </serviceList>
            <deviceList><device><deviceList><device>
              <serviceList>
                <service>
                  <serviceType>urn:schemas-upnp-org:service:WANPPPConnection:1</serviceType>
                  <controlURL>/upnp/control/WANPPPConnection</controlURL>
                </service>
                <service>
                  <serviceType>urn:schemas-upnp-org:service:WANIPConnection:1</serviceType>
                  <controlURL>/upnp/control/WANIPConnection</controlURL>
                </service>
              </serviceList>
            </device></deviceList></device></deviceList>
          </device>
        </root>
        """;

    [Theory]
    [InlineData("HTTP/1.1 200 OK\r\nCACHE-CONTROL: max-age=120\r\nLOCATION: http://192.168.0.1:52869/picsdesc.xml\r\nST: x\r\n\r\n")]
    [InlineData("HTTP/1.1 200 OK\r\nServer: miniupnpd/1.0 UPnP/1.0\r\nLocation: http://192.168.0.1:52869/picsdesc.xml\r\n\r\n")]
    public void ParseLocation_IgnoresHeaderCase(string response)
    {
        Assert.Equal(Location, UpnpProtocol.ParseLocation(response));
    }

    [Fact]
    public void ParseLocation_MissingHeader_ReturnsNull()
    {
        Assert.Null(UpnpProtocol.ParseLocation("HTTP/1.1 200 OK\r\nST: x\r\n\r\n"));
    }

    [Fact]
    public void FindWanService_PrefersWanIpConnection_AndResolvesRelativeUrl()
    {
        var service = UpnpProtocol.FindWanService(Description, Location);
        Assert.NotNull(service);
        Assert.Equal("urn:schemas-upnp-org:service:WANIPConnection:1", service.ServiceType);
        Assert.Equal(new Uri("http://192.168.0.1:52869/upnp/control/WANIPConnection"), service.ControlUrl);
    }

    [Fact]
    public void FindWanService_UsesUrlBase()
    {
        var xml = Description.Replace("<device>\n    <deviceType>", "<device>\n    <deviceType>")
            .Replace("<root xmlns=\"urn:schemas-upnp-org:device-1-0\">", "<root xmlns=\"urn:schemas-upnp-org:device-1-0\"><URLBase>http://192.168.0.1:5000/</URLBase>");
        Assert.Equal(new Uri("http://192.168.0.1:5000/upnp/control/WANIPConnection"), UpnpProtocol.FindWanService(xml, Location)!.ControlUrl);
    }

    [Fact]
    public void FindWanService_NoWanService_ReturnsNull()
    {
        Assert.Null(UpnpProtocol.FindWanService("<root><device><serviceList/></device></root>", Location));
    }

    [Fact]
    public void SoapBody_EscapesValues()
    {
        var body = UpnpProtocol.BuildSoapBody("AddPortMapping", "urn:x", ("NewPortMappingDescription", "a<b&c"));
        Assert.Contains("<u:AddPortMapping xmlns:u=\"urn:x\">", body);
        Assert.Contains("<NewPortMappingDescription>a&lt;b&amp;c</NewPortMappingDescription>", body);
    }

    [Fact]
    public void GetValue_And_ErrorCode()
    {
        const string ok = "<s:Envelope xmlns:s=\"http://schemas.xmlsoap.org/soap/envelope/\"><s:Body><u:GetExternalIPAddressResponse xmlns:u=\"urn:x\"><NewExternalIPAddress>198.51.100.20</NewExternalIPAddress></u:GetExternalIPAddressResponse></s:Body></s:Envelope>";
        const string fault = "<s:Envelope xmlns:s=\"http://schemas.xmlsoap.org/soap/envelope/\"><s:Body><s:Fault><detail><UPnPError xmlns=\"urn:schemas-upnp-org:control-1-0\"><errorCode>718</errorCode><errorDescription>ConflictInMappingEntry</errorDescription></UPnPError></detail></s:Fault></s:Body></s:Envelope>";
        Assert.Equal("198.51.100.20", UpnpProtocol.GetValue(ok, "NewExternalIPAddress"));
        Assert.Equal(718, UpnpProtocol.GetErrorCode(fault));
        Assert.Null(UpnpProtocol.GetErrorCode("not xml"));
    }
}
