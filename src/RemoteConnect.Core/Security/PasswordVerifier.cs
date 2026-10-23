using System.Security.Cryptography;
using System.Text;

namespace RemoteConnect.Core.Security;

/// <summary>Ключ, производный от пароля. Хост хранит только его, сам пароль не хранится.</summary>
public sealed record PasswordKey(byte[] Salt, int Iterations, byte[] Key);

/// <summary>
/// Проверка пароля по схеме challenge-response:
/// proof = HMAC-SHA256(PBKDF2(пароль, соль), nonce ‖ SHA-256 сертификата хоста).
/// Пароль не передаётся по сети, а привязка к сертификату не даёт переиспользовать ответ
/// через подменённый хост.
/// </summary>
public static class PasswordVerifier
{
    public const int DefaultIterations = 600_000;
    public const int MaxIterations = 2_000_000;
    public const int SaltSize = 16;
    public const int NonceSize = 32;
    public const int KeySize = 32;

    public static PasswordKey CreateKey(string password, int iterations = DefaultIterations)
    {
        var salt = RandomNumberGenerator.GetBytes(SaltSize);
        return new PasswordKey(salt, iterations, DeriveKey(password, salt, iterations));
    }

    public static byte[] DeriveKey(string password, byte[] salt, int iterations)
    {
        if (iterations is < 1 or > MaxIterations)
        {
            throw new ArgumentOutOfRangeException(nameof(iterations));
        }

        return Rfc2898DeriveBytes.Pbkdf2(Encoding.UTF8.GetBytes(password), salt, iterations, HashAlgorithmName.SHA256, KeySize);
    }

    public static byte[] CreateNonce() => RandomNumberGenerator.GetBytes(NonceSize);

    public static byte[] ComputeProof(byte[] key, byte[] nonce, byte[] certificateHash)
    {
        var data = new byte[nonce.Length + certificateHash.Length];
        nonce.CopyTo(data, 0);
        certificateHash.CopyTo(data, nonce.Length);
        return HMACSHA256.HashData(key, data);
    }

    public static bool Verify(PasswordKey key, byte[] nonce, byte[] certificateHash, byte[] proof)
    {
        var expected = ComputeProof(key.Key, nonce, certificateHash);
        return CryptographicOperations.FixedTimeEquals(expected, proof);
    }
}
