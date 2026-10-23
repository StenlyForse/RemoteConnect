using System.Security.Cryptography;

namespace RemoteConnect.Windows.Security;

/// <summary>Шифрование локальных секретов через DPAPI для текущего пользователя Windows (FR-SEC-3).</summary>
public static class DataProtector
{
    private static readonly byte[] Entropy = "RemoteConnect/v1"u8.ToArray();

    public static byte[] Protect(byte[] data) =>
        ProtectedData.Protect(data, Entropy, DataProtectionScope.CurrentUser);

    public static byte[] Unprotect(byte[] data) =>
        ProtectedData.Unprotect(data, Entropy, DataProtectionScope.CurrentUser);
}
