namespace RemoteConnect.Core.Chat;

/// <summary>Фрагмент сообщения: простой текст или ссылка (<see cref="Link"/> не null).</summary>
public sealed record ChatFragment(string Text, Uri? Link = null);

/// <summary>
/// Распознавание ссылок в тексте чата (FR-CHT-13, FR-CHT-15): только <c>http://</c> и <c>https://</c>;
/// всё остальное, включая <c>file:</c>, <c>javascript:</c> и пути, остаётся текстом.
/// </summary>
public static class ChatLinkParser
{
    private static readonly string[] Prefixes = ["https://", "http://"];
    private const string TrailingPunctuation = ".,;:!?…»\"'";

    public static IReadOnlyList<ChatFragment> Parse(string text)
    {
        var fragments = new List<ChatFragment>();
        var textStart = 0;
        var i = 0;
        while (i < text.Length)
        {
            if (!StartsLink(text, i))
            {
                i++;
                continue;
            }

            var end = i;
            while (end < text.Length && !IsUrlTerminator(text[end]))
            {
                end++;
            }

            var candidate = TrimTrailing(text[i..end]);
            if (TryCreateSafe(candidate, out var uri))
            {
                if (i > textStart)
                {
                    fragments.Add(new ChatFragment(text[textStart..i]));
                }

                fragments.Add(new ChatFragment(candidate, uri));
                i += candidate.Length;
                textStart = i;
            }
            else
            {
                i = end;
            }
        }

        if (textStart < text.Length)
        {
            fragments.Add(new ChatFragment(text[textStart..]));
        }

        return fragments;
    }

    /// <summary>
    /// Ссылку можно открыть: абсолютный адрес http/https с именем хоста и без «логина» перед <c>@</c>
    /// (<c>https://bank.ru@evil.example</c> ведёт не туда, куда кажется). Проверяется и при разборе, и перед открытием.
    /// </summary>
    public static bool IsSafe(Uri uri) =>
        uri.IsAbsoluteUri
        && (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps)
        && uri.Host.Length > 0
        && uri.UserInfo.Length == 0;

    private static bool TryCreateSafe(string candidate, out Uri? uri)
    {
        uri = null;
        if (!Uri.TryCreate(candidate, UriKind.Absolute, out var parsed) || !IsSafe(parsed))
        {
            return false;
        }

        uri = parsed;
        return true;
    }

    private static bool StartsLink(string text, int i)
    {
        if (i > 0 && char.IsLetterOrDigit(text[i - 1]))
        {
            return false;
        }

        foreach (var prefix in Prefixes)
        {
            if (string.Compare(text, i, prefix, 0, prefix.Length, StringComparison.OrdinalIgnoreCase) == 0
                && text.Length > i + prefix.Length)
            {
                return true;
            }
        }

        return false;
    }

    private static bool IsUrlTerminator(char c) => char.IsWhiteSpace(c) || c is '<' or '>' or '"' or '`';

    /// <summary>Отрезает пунктуацию в конце и закрывающие скобки без пары: «(см. https://a.ru/x).» → https://a.ru/x.</summary>
    private static string TrimTrailing(string url)
    {
        while (url.Length > 0)
        {
            var last = url[^1];
            if (TrailingPunctuation.Contains(last)
                || (last == ')' && Count(url, '(') < Count(url, ')'))
                || (last == ']' && Count(url, '[') < Count(url, ']')))
            {
                url = url[..^1];
            }
            else
            {
                break;
            }
        }

        return url;
    }

    private static int Count(string s, char c) => s.Count(x => x == c);
}
