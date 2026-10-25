using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using RemoteConnect.Core.Network;
using RemoteConnect.Core.Protocol;
using RemoteConnect.Core.Security;
using RemoteConnect.Windows.Security;
using Serilog;

namespace RemoteConnect.App.Services;

public enum PasswordMode
{
    OneTime,
    Permanent,
}

public static class AppPaths
{
    public static string Roaming { get; } = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "RemoteConnect");

    public static string Local { get; } = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "RemoteConnect");

    public static string Settings => Path.Combine(Roaming, "settings.json");

    public static string Certificate => Path.Combine(Roaming, "host-certificate.bin");

    public static string Logs => Path.Combine(Local, "logs");
}

public sealed class AppSettings
{
    public string DisplayName { get; set; } = Environment.UserName;

    public int Port { get; set; } = ConnectionInfoParser.DefaultPort;

    public PasswordMode PasswordMode { get; set; } = PasswordMode.OneTime;

    /// <summary>PBKDF2-ключ постоянного пароля, зашифрованный DPAPI (base64). Сам пароль не хранится.</summary>
    public string? PermanentPasswordKey { get; set; }

    public int AutoStopMinutes { get; set; } = 30;

    /// <summary>Не используется с v0.5: качество подбирается под канал (spec-v0.5). Оставлено для совместимости файла настроек.</summary>
    public int JpegQuality { get; set; } = 70;

    public SessionRole ConnectRole { get; set; } = SessionRole.Controller;

    public List<string> History { get; set; } = [];

    /// <summary>Запомненные отпечатки сертификатов хостов (TOFU): адрес → SHA-256 в hex.</summary>
    public Dictionary<string, string> KnownHosts { get; set; } = [];
}

public sealed class SettingsService
{
    private const int MaxHistory = 10;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
    };

    private readonly Lock _lock = new();

    public SettingsService()
    {
        Current = Load();
    }

    public AppSettings Current { get; }

    public void Save()
    {
        lock (_lock)
        {
            try
            {
                Directory.CreateDirectory(AppPaths.Roaming);
                var temp = AppPaths.Settings + ".tmp";
                File.WriteAllText(temp, JsonSerializer.Serialize(Current, JsonOptions));
                File.Move(temp, AppPaths.Settings, overwrite: true);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                Log.Warning(ex, "Не удалось сохранить настройки");
            }
        }
    }

    public void AddHistory(string address)
    {
        Current.History.RemoveAll(a => string.Equals(a, address, StringComparison.OrdinalIgnoreCase));
        Current.History.Insert(0, address);
        if (Current.History.Count > MaxHistory)
        {
            Current.History.RemoveRange(MaxHistory, Current.History.Count - MaxHistory);
        }
    }

    public void SetPermanentPassword(string password)
    {
        var key = PasswordVerifier.CreateKey(password);
        var blob = new byte[key.Salt.Length + 4 + key.Key.Length];
        key.Salt.CopyTo(blob, 0);
        BitConverter.GetBytes(key.Iterations).CopyTo(blob, key.Salt.Length);
        key.Key.CopyTo(blob, key.Salt.Length + 4);
        Current.PermanentPasswordKey = Convert.ToBase64String(DataProtector.Protect(blob));
        Save();
    }

    public PasswordKey? GetPermanentPasswordKey()
    {
        if (Current.PermanentPasswordKey is null)
        {
            return null;
        }

        try
        {
            var blob = DataProtector.Unprotect(Convert.FromBase64String(Current.PermanentPasswordKey));
            var salt = blob[..PasswordVerifier.SaltSize];
            var iterations = BitConverter.ToInt32(blob, PasswordVerifier.SaltSize);
            var key = blob[(PasswordVerifier.SaltSize + 4)..];
            return new PasswordKey(salt, iterations, key);
        }
        catch (Exception ex) when (ex is FormatException or System.Security.Cryptography.CryptographicException or ArgumentException)
        {
            Log.Warning(ex, "Сохранённый пароль повреждён");
            return null;
        }
    }

    private static AppSettings Load()
    {
        try
        {
            if (File.Exists(AppPaths.Settings))
            {
                return JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(AppPaths.Settings), JsonOptions) ?? new AppSettings();
            }
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            Log.Warning(ex, "Настройки повреждены, используются значения по умолчанию");
        }

        return new AppSettings();
    }
}
