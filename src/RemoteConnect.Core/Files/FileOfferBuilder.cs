using System.Diagnostics;
using RemoteConnect.Core.Protocol;

namespace RemoteConnect.Core.Files;

public enum FileOfferBuildStatus
{
    Ok,
    Empty,
    TooLarge,
    TooManyItems,
    TimedOut,
    PathTooLong,
    Error,
}

/// <summary>
/// Набор файлов для предложения: записи протокола и параллельный им список путей на диске источника.
/// <paramref name="Problem"/> — путь или текст ошибки для уведомления, если набор не построен.
/// </summary>
public sealed record FileOfferBuildResult(
    FileOfferBuildStatus Status,
    IReadOnlyList<FileOfferEntry> Entries,
    IReadOnlyList<string> SourcePaths,
    long TotalBytes,
    string? Problem = null)
{
    public bool IsOk => Status == FileOfferBuildStatus.Ok;

    public static FileOfferBuildResult Failed(FileOfferBuildStatus status, long totalBytes, string? problem = null) =>
        new(status, [], [], totalBytes, problem);
}

/// <summary>
/// Строит набор из файлов и папок, скопированных в Проводнике (FR-FCB-2..4): обходит папки целиком, включая пустые,
/// пути — относительно родительской папки каждого выбранного элемента. Папка всегда идёт раньше своего содержимого.
/// Ссылки на папки (junction, symlink) не раскрываются — они попадают в набор пустыми папками (защита от циклов).
/// </summary>
public static class FileOfferBuilder
{
    public const long MaxTotalBytes = 500L * 1024 * 1024;
    public const int MaxEntries = MessageSerializer.MaxFileOfferEntries;
    public const int MaxRelativePathChars = 259;
    public static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(5);

    /// <summary>Как Проводник: скрытые и системные файлы тоже копируются.</summary>
    private static readonly EnumerationOptions AllEntries = new() { AttributesToSkip = 0 };

    public static FileOfferBuildResult Build(IReadOnlyList<string> paths, TimeSpan? timeout = null, CancellationToken ct = default)
    {
        var limit = timeout ?? DefaultTimeout;
        var clock = Stopwatch.StartNew();
        var entries = new List<FileOfferEntry>();
        var sources = new List<string>();
        var usedTopNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        long total = 0;

        try
        {
            foreach (var rawPath in paths)
            {
                var path = Path.TrimEndingDirectorySeparator(Path.GetFullPath(rawPath));
                var topName = UniqueName(Path.GetFileName(path), usedTopNames);
                if (topName.Length == 0)
                {
                    // Корень диска (C:\) — не поддерживается.
                    return FileOfferBuildResult.Failed(FileOfferBuildStatus.Error, total, path);
                }

                var pending = new Stack<(FileSystemInfo Info, string Relative)>();
                pending.Push((Info(path), topName));
                while (pending.Count > 0)
                {
                    ct.ThrowIfCancellationRequested();
                    if (clock.Elapsed > limit)
                    {
                        return FileOfferBuildResult.Failed(FileOfferBuildStatus.TimedOut, total);
                    }

                    var (info, relative) = pending.Pop();
                    if (relative.Length > MaxRelativePathChars)
                    {
                        return FileOfferBuildResult.Failed(FileOfferBuildStatus.PathTooLong, total, relative);
                    }

                    if (!info.Exists)
                    {
                        return FileOfferBuildResult.Failed(FileOfferBuildStatus.Error, total, info.FullName);
                    }

                    if (entries.Count >= MaxEntries)
                    {
                        return FileOfferBuildResult.Failed(FileOfferBuildStatus.TooManyItems, total);
                    }

                    var lastWrite = info.LastWriteTimeUtc;
                    if (info is DirectoryInfo directory)
                    {
                        entries.Add(new FileOfferEntry(relative, true, 0, lastWrite));
                        sources.Add(directory.FullName);
                        if (!directory.Attributes.HasFlag(FileAttributes.ReparsePoint))
                        {
                            // Стек: дети кладутся в обратном порядке, чтобы набор шёл по алфавиту.
                            var children = directory.EnumerateFileSystemInfos("*", AllEntries).OrderByDescending(c => c.Name, StringComparer.OrdinalIgnoreCase);
                            foreach (var child in children)
                            {
                                pending.Push((child, relative + '\\' + child.Name));
                            }
                        }
                    }
                    else
                    {
                        var size = FileSize((FileInfo)info);
                        total += size;
                        if (total > MaxTotalBytes)
                        {
                            return FileOfferBuildResult.Failed(FileOfferBuildStatus.TooLarge, TotalSize(paths, total));
                        }

                        entries.Add(new FileOfferEntry(relative, false, size, lastWrite));
                        sources.Add(info.FullName);
                    }
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            return FileOfferBuildResult.Failed(FileOfferBuildStatus.Error, total, ex.Message);
        }

        return entries.Count == 0
            ? FileOfferBuildResult.Failed(FileOfferBuildStatus.Empty, 0)
            : new FileOfferBuildResult(FileOfferBuildStatus.Ok, entries, sources, total);
    }

    private static FileSystemInfo Info(string path) =>
        Directory.Exists(path) ? new DirectoryInfo(path) : new FileInfo(path);

    /// <summary>Размер файла; для символической ссылки — размер файла, на который она указывает.</summary>
    private static long FileSize(FileInfo file)
    {
        if (file.Attributes.HasFlag(FileAttributes.ReparsePoint) && file.ResolveLinkTarget(returnFinalTarget: true) is FileInfo { Exists: true } target)
        {
            return target.Length;
        }

        return file.Length;
    }

    /// <summary>
    /// Для уведомления «2,0 ГБ — больше лимита»: досчитывает полный размер, не дольше секунды.
    /// Если не успели — возвращает уже насчитанное (оно больше лимита).
    /// </summary>
    private static long TotalSize(IReadOnlyList<string> paths, long counted)
    {
        var clock = Stopwatch.StartNew();
        long total = 0;
        try
        {
            foreach (var path in paths)
            {
                if (File.Exists(path))
                {
                    total += new FileInfo(path).Length;
                    continue;
                }

                var options = new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true, AttributesToSkip = 0 };
                foreach (var file in new DirectoryInfo(path).EnumerateFiles("*", options))
                {
                    total += file.Length;
                    if (clock.ElapsedMilliseconds > 1000)
                    {
                        return Math.Max(total, counted);
                    }
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return counted;
        }

        return Math.Max(total, counted);
    }

    /// <summary>Выбранные элементы из разных папок могут совпадать по имени: второй получает « (2)».</summary>
    private static string UniqueName(string name, HashSet<string> used)
    {
        if (name.Length == 0 || used.Add(name))
        {
            return name;
        }

        var stem = Path.GetFileNameWithoutExtension(name);
        var extension = Path.GetExtension(name);
        for (var i = 2; ; i++)
        {
            var candidate = $"{stem} ({i}){extension}";
            if (used.Add(candidate))
            {
                return candidate;
            }
        }
    }
}
