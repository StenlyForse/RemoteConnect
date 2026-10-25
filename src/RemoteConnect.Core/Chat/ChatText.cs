using System.Text;
using RemoteConnect.Core.Protocol;

namespace RemoteConnect.Core.Chat;

/// <summary>Приведение текста сообщения чата к простому тексту (FR-CHT-2, FR-CHT-3).</summary>
public static class ChatText
{
    /// <summary>
    /// Переводы строк — к <c>\n</c>; удаляются управляющие символы (кроме <c>\n</c> и <c>\t</c>) и символы
    /// управления направлением текста, которыми можно визуально «перевернуть» ссылку (FR-CHT-15); пробелы по краям обрезаются.
    /// </summary>
    public static string Normalize(string text)
    {
        var sb = new StringBuilder(text.Length);
        for (var i = 0; i < text.Length; i++)
        {
            var c = text[i];
            if (c == '\r')
            {
                sb.Append('\n');
                if (i + 1 < text.Length && text[i + 1] == '\n')
                {
                    i++;
                }
            }
            else if (c is '\n' or '\t' || !(char.IsControl(c) || IsBidiControl(c)))
            {
                sb.Append(c);
            }
        }

        return sb.ToString().Trim();
    }

    /// <summary>Нормализованный текст можно отправить: не пустой и не длиннее лимита.</summary>
    public static bool IsSendable(string normalized) => normalized.Length is > 0 and <= ChatMessage.MaxLength;

    private static bool IsBidiControl(char c) => c is
        '؜' or '‎' or '‏' or
        (>= '‪' and <= '‮') or
        (>= '⁦' and <= '⁩');
}
