using RemoteConnect.Core.Protocol;

namespace RemoteConnect.Core.Files;

/// <summary>
/// Проверка путей из набора, пришедшего с другого ПК, до помещения в буфер (FR-FCB-17).
/// Допустим только относительный путь через <c>\</c> из «обычных» имён Windows; набор с хотя бы одним
/// недопустимым элементом отклоняется целиком.
/// </summary>
public static class FileNameValidator
{
    public const int MaxComponentLength = 255;

    private static readonly char[] ForbiddenChars = ['<', '>', ':', '"', '/', '|', '?', '*'];

    private static readonly HashSet<string> ReservedNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "CON", "PRN", "AUX", "NUL", "CONIN$", "CONOUT$",
        "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9", "COM¹", "COM²", "COM³",
        "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9", "LPT¹", "LPT²", "LPT³",
    };

    /// <summary>Возвращает null, если набор допустим, иначе — первый недопустимый путь.</summary>
    public static string? FindInvalid(IReadOnlyList<FileOfferEntry> entries)
    {
        if (entries.Count == 0)
        {
            return string.Empty;
        }

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in entries)
        {
            if (!IsValidPath(entry.RelativePath) || !seen.Add(entry.RelativePath))
            {
                return entry.RelativePath;
            }
        }

        return null;
    }

    public static bool IsValidPath(string path)
    {
        if (path.Length is 0 or > FileOfferBuilder.MaxRelativePathChars)
        {
            return false;
        }

        foreach (var component in path.Split('\\'))
        {
            if (!IsValidName(component))
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>Одно имя файла или папки (без разделителей).</summary>
    public static bool IsValidName(string name)
    {
        if (name.Length is 0 or > MaxComponentLength || name is "." or "..")
        {
            return false;
        }

        // Windows отбрасывает пробелы и точки в конце имени — «a.» и «a» были бы одним файлом.
        if (name[^1] is ' ' or '.')
        {
            return false;
        }

        foreach (var c in name)
        {
            if (c < 32 || c == '\\' || Array.IndexOf(ForbiddenChars, c) >= 0)
            {
                return false;
            }
        }

        // «CON», «con.txt», «NUL.tar.gz» — зарезервированы с любым расширением.
        var dot = name.IndexOf('.');
        var stem = (dot < 0 ? name : name[..dot]).TrimEnd(' ');
        return !ReservedNames.Contains(stem);
    }
}
