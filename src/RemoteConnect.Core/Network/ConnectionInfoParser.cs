using System.Diagnostics.CodeAnalysis;
using System.Net;
using System.Text.RegularExpressions;

namespace RemoteConnect.Core.Network;

public sealed record ConnectionTarget(string Host, int Port, string? Password)
{
    public override string ToString() => Host.Contains(':') ? $"[{Host}]:{Port}" : $"{Host}:{Port}";
}

/// <summary>
/// Разбор адреса подключения: <c>host</c>, <c>host:port</c>, <c>[ipv6]:port</c>,
/// а также текста, скопированного кнопкой «Скопировать» на хосте («Адрес: …», «Пароль: …»).
/// </summary>
public static partial class ConnectionInfoParser
{
    public const int DefaultPort = 47800;

    public static string Format(string host, int port, string? password)
    {
        var target = new ConnectionTarget(host, port, null).ToString();
        return password is null ? $"Адрес: {target}" : $"Адрес: {target}\r\nПароль: {password}";
    }

    public static bool TryParse(string? text, [NotNullWhen(true)] out ConnectionTarget? target)
    {
        target = null;
        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        string? password = null;
        var addressText = text.Trim();

        var addressMatch = AddressLine().Match(text);
        if (addressMatch.Success)
        {
            addressText = addressMatch.Groups[1].Value.Trim();
            var passwordMatch = PasswordLine().Match(text);
            if (passwordMatch.Success)
            {
                password = passwordMatch.Groups[1].Value.Trim();
            }
        }

        if (!TryParseAddress(addressText, out var host, out var port))
        {
            return false;
        }

        target = new ConnectionTarget(host, port, password);
        return true;
    }

    private static bool TryParseAddress(string text, [NotNullWhen(true)] out string? host, out int port)
    {
        host = null;
        port = DefaultPort;

        if (text.StartsWith('['))
        {
            var end = text.IndexOf(']');
            if (end < 0)
            {
                return false;
            }

            host = text[1..end];
            var rest = text[(end + 1)..];
            if (rest.Length > 0 && (!rest.StartsWith(':') || !TryParsePort(rest[1..], out port)))
            {
                return false;
            }

            return IPAddress.TryParse(host, out _);
        }

        var colons = text.Count(c => c == ':');
        if (colons > 1)
        {
            host = text;
            return IPAddress.TryParse(text, out _);
        }

        if (colons == 1)
        {
            var parts = text.Split(':');
            host = parts[0];
            if (!TryParsePort(parts[1], out port))
            {
                return false;
            }
        }
        else
        {
            host = text;
        }

        return host.Length > 0 && Uri.CheckHostName(host) != UriHostNameType.Unknown;
    }

    private static bool TryParsePort(string text, out int port) =>
        int.TryParse(text, out port) && port is > 0 and <= 65535;

    [GeneratedRegex(@"Адрес:\s*(\S+)", RegexOptions.IgnoreCase)]
    private static partial Regex AddressLine();

    [GeneratedRegex(@"Пароль:\s*(\S+)", RegexOptions.IgnoreCase)]
    private static partial Regex PasswordLine();
}
