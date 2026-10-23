using System.Security.Cryptography;

namespace RemoteConnect.Core.Security;

/// <summary>Одноразовый пароль вида <c>k7m-29x</c>. Без похожих символов (0/o, 1/l/i).</summary>
public static class OneTimePassword
{
    public const string Alphabet = "abcdefghjkmnpqrstuvwxyz23456789";

    public static string Generate()
    {
        Span<char> chars = stackalloc char[7];
        for (var i = 0; i < chars.Length; i++)
        {
            chars[i] = i == 3 ? '-' : Alphabet[RandomNumberGenerator.GetInt32(Alphabet.Length)];
        }

        return new string(chars);
    }
}
