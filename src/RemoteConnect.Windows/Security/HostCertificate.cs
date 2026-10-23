using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace RemoteConnect.Windows.Security;

/// <summary>
/// Самоподписанный сертификат хоста (FR-SEC-1). Создаётся при первом запуске,
/// хранится в PFX, зашифрованном DPAPI для текущего пользователя.
/// </summary>
public static class HostCertificate
{
    public const string SubjectName = "CN=RemoteConnect";

    public static X509Certificate2 LoadOrCreate(string path)
    {
        if (File.Exists(path))
        {
            try
            {
                var pfx = DataProtector.Unprotect(File.ReadAllBytes(path));
                return Import(pfx);
            }
            catch (CryptographicException)
            {
                // Файл повреждён или создан другим пользователем — создаём новый.
            }
        }

        var created = Create();
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, DataProtector.Protect(created));
        return Import(created);
    }

    /// <summary>Новый сертификат в виде PFX.</summary>
    public static byte[] Create()
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var request = new CertificateRequest(SubjectName, key, HashAlgorithmName.SHA256);
        request.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.DigitalSignature, critical: true));
        request.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension([new Oid("1.3.6.1.5.5.7.3.1")], critical: false));
        var now = DateTimeOffset.UtcNow;
        using var certificate = request.CreateSelfSigned(now.AddDays(-1), now.AddYears(20));
        return certificate.Export(X509ContentType.Pkcs12);
    }

    /// <summary>
    /// Импорт с сохраняемым ключом: SChannel не умеет работать с эфемерными ключами,
    /// поэтому сертификат, созданный в памяти, нужно пропустить через PFX.
    /// </summary>
    public static X509Certificate2 Import(byte[] pfx) =>
        X509CertificateLoader.LoadPkcs12(pfx, null, X509KeyStorageFlags.UserKeySet);
}
