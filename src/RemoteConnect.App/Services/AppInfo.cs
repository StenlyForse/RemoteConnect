using System.Reflection;

namespace RemoteConnect.App.Services;

public static class AppInfo
{
    /// <summary>Версия программы без хеша коммита, например «0.4.0» (FR-VER-1).</summary>
    public static string Version { get; } = ReadVersion();

    /// <summary>Текст отказа по несовместимой версии (FR-VER-2, FR-VER-3). <paramref name="peerVersion"/> null — собеседник старше v0.4.</summary>
    public static string DescribeVersionMismatch(string? peerVersion) => peerVersion is null
        ? $"У собеседника более старая версия RemoteConnect, у вас — {Version}. Обновитесь до одной версии."
        : $"У собеседника версия {peerVersion}, у вас — {Version}. Обновитесь до одной версии.";

    private static string ReadVersion()
    {
        var assembly = typeof(AppInfo).Assembly;
        var informational = assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
        var version = informational ?? assembly.GetName().Version?.ToString(3) ?? "0.0.0";
        var plus = version.IndexOf('+');
        return plus >= 0 ? version[..plus] : version;
    }
}
