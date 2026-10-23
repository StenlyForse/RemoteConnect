using System.Security.Cryptography;

namespace RemoteConnect.Core.Security;

/// <summary>Короткий отпечаток сертификата для сверки голосом (FR-SEC-7), например <c>3F9A-07C2-B41E-55D0</c>.</summary>
public static class Fingerprint
{
    public static byte[] Hash(byte[] rawCertificate) => SHA256.HashData(rawCertificate);

    public static string Format(byte[] certificateHash)
    {
        var hex = Convert.ToHexString(certificateHash, 0, 8);
        return $"{hex[..4]}-{hex[4..8]}-{hex[8..12]}-{hex[12..16]}";
    }
}
