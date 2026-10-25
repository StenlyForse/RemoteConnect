namespace RemoteConnect.Core.Protocol;

public enum MessageType : byte
{
    Hello = 1,
    AuthChallenge = 2,
    AuthResponse = 3,
    AuthResult = 4,
    ConsentResult = 5,
    ScreenInfo = 6,
    FrameTiles = 7,
    FrameAck = 8,
    MouseInput = 9,
    KeyInput = 10,
    ReleaseAll = 11,
    SelectMonitor = 12,
    InputEnabled = 13,
    Ping = 14,
    Pong = 15,
    Bye = 16,
    ClipboardData = 17,
    ClipboardEnabled = 18,
    FileOffer = 19,
    FileOpen = 20,
    FileChunk = 21,
    FileAck = 22,
    FileEnd = 23,
    FileError = 24,
    FileCancel = 25,

    // Начиная с v0.4 формат и номера Hello, AuthResult и AppVersion не меняются: по ним
    // стороны разных версий показывают понятное сообщение о несовместимости (plan-chat §3).
    AppVersion = 26,
    ChatMessage = 27,
    StreamMode = 28,
}

/// <summary>Роль стороны в сессии. Не зависит от того, кто к кому подключился по сети.</summary>
public enum SessionRole : byte
{
    Controller = 1,
    Controlled = 2,
}

public enum AuthStatus : byte
{
    Ok = 0,
    WrongPassword = 1,
    Blocked = 2,
    Busy = 3,
    VersionMismatch = 4,
}

public enum ConsentDecision : byte
{
    Allowed = 0,
    Denied = 1,
    TimedOut = 2,
}

public enum DisconnectReason : byte
{
    UserClosed = 0,
    ConsentDenied = 1,
    Timeout = 2,
    ProtocolError = 3,
    AppExit = 4,
    Error = 5,
}

public enum MouseAction : byte
{
    Move = 0,
    Down = 1,
    Up = 2,
    Wheel = 3,
    HorizontalWheel = 4,
}

public enum MouseButton : byte
{
    None = 0,
    Left = 1,
    Right = 2,
    Middle = 3,
    X1 = 4,
    X2 = 5,
}

public enum ClipboardFormat : byte
{
    /// <summary>Текст в UTF-8.</summary>
    Text = 1,

    /// <summary>Картинка в PNG.</summary>
    Image = 2,
}

/// <summary>Режим подстройки изображения под канал, выбранный управляющим (FR-ADP-6).</summary>
public enum StreamModeKind : byte
{
    Auto = 0,

    /// <summary>Чёткость важнее плавности.</summary>
    BestQuality = 1,

    /// <summary>Плавность важнее чёткости.</summary>
    Faster = 2,
}

public enum FileErrorCode : byte
{
    /// <summary>Набор заменён новым или отозван.</summary>
    OfferExpired = 1,
    NotFound = 2,
    AccessDenied = 3,

    /// <summary>Размер файла изменился после копирования.</summary>
    Changed = 4,
    ReadError = 5,

    /// <summary>Неверный индекс, папка вместо файла, слишком много одновременных передач.</summary>
    BadRequest = 6,
}

public abstract record Message
{
    public abstract MessageType Type { get; }
}

public sealed record Hello(int ProtocolVersion, string Name, SessionRole ClientRole) : Message
{
    public override MessageType Type => MessageType.Hello;
}

public sealed record AuthChallenge(byte[] Salt, int Iterations, byte[] Nonce) : Message
{
    public override MessageType Type => MessageType.AuthChallenge;
}

public sealed record AuthResponse(byte[] Proof) : Message
{
    public override MessageType Type => MessageType.AuthResponse;
}

public sealed record AuthResult(AuthStatus Status, int AttemptsLeft, int BlockedSeconds, string HostName) : Message
{
    public override MessageType Type => MessageType.AuthResult;
}

public sealed record ConsentResult(ConsentDecision Decision) : Message
{
    public override MessageType Type => MessageType.ConsentResult;
}

public sealed record MonitorDescriptor(string Name, int Width, int Height, bool IsPrimary);

public sealed record ScreenInfo(IReadOnlyList<MonitorDescriptor> Monitors, int ActiveMonitor, int Width, int Height) : Message
{
    public override MessageType Type => MessageType.ScreenInfo;
}

public sealed record EncodedTile(int X, int Y, int Width, int Height, byte[] Data);

/// <summary>
/// Изменённые плитки кадра или его части. <paramref name="Quality"/> — качество JPEG плиток (FR-ADP-7).
/// Большой кадр уходит двумя частями вплотную (оценка канала, plan-v0.5); <paramref name="EndOfFrame"/> — у последней.
/// Каждая часть подтверждается своим <see cref="FrameAck"/>.
/// </summary>
public sealed record FrameTiles(int FrameNumber, byte Quality, bool EndOfFrame, IReadOnlyList<EncodedTile> Tiles) : Message
{
    public override MessageType Type => MessageType.FrameTiles;
}

public sealed record FrameAck(int FrameNumber) : Message
{
    public override MessageType Type => MessageType.FrameAck;
}

/// <summary>Координаты нормализованы в диапазон 0..65535 относительно транслируемого монитора.</summary>
public sealed record MouseInput(MouseAction Action, ushort X, ushort Y, MouseButton Button, short WheelDelta) : Message
{
    public override MessageType Type => MessageType.MouseInput;
}

public sealed record KeyInput(ushort VirtualKey, ushort ScanCode, bool IsDown, bool IsExtended) : Message
{
    public override MessageType Type => MessageType.KeyInput;
}

public sealed record ReleaseAll : Message
{
    public override MessageType Type => MessageType.ReleaseAll;
}

public sealed record SelectMonitor(int Index) : Message
{
    public override MessageType Type => MessageType.SelectMonitor;
}

public sealed record InputEnabled(bool Enabled) : Message
{
    public override MessageType Type => MessageType.InputEnabled;
}

public sealed record Ping(long Timestamp) : Message
{
    public override MessageType Type => MessageType.Ping;
}

public sealed record Pong(long Timestamp) : Message
{
    public override MessageType Type => MessageType.Pong;
}

public sealed record Bye(DisconnectReason Reason) : Message
{
    public override MessageType Type => MessageType.Bye;
}

public sealed record ClipboardData(ClipboardFormat Format, byte[] Data) : Message
{
    public override MessageType Type => MessageType.ClipboardData;
}

public sealed record ClipboardEnabled(bool Enabled) : Message
{
    public override MessageType Type => MessageType.ClipboardEnabled;
}

/// <summary>Элемент набора файлов: относительный путь (через <c>\</c>), папка или файл, размер, дата изменения (UTC).</summary>
public sealed record FileOfferEntry(string RelativePath, bool IsDirectory, long Size, DateTime LastWriteUtc);

/// <summary>Набор файлов в буфере обмена источника (spec-file-clipboard). Новый набор заменяет предыдущий.</summary>
public sealed record FileOffer(int OfferId, IReadOnlyList<FileOfferEntry> Entries) : Message
{
    public override MessageType Type => MessageType.FileOffer;
}

/// <summary>Запрос содержимого файла <paramref name="Index"/> из набора <paramref name="OfferId"/>.</summary>
public sealed record FileOpen(int RequestId, int OfferId, int Index) : Message
{
    public override MessageType Type => MessageType.FileOpen;
}

/// <summary>Очередная часть файла (не больше <see cref="MaxSize"/>). Отправляется фоновой очередью.</summary>
public sealed record FileChunk(int RequestId, byte[] Data) : Message
{
    public const int MaxSize = 64 * 1024;

    public override MessageType Type => MessageType.FileChunk;
}

/// <summary>Сколько байт файла получатель уже прочитал (всего с начала) — открывает окно отправки.</summary>
public sealed record FileAck(int RequestId, long BytesConsumed) : Message
{
    public override MessageType Type => MessageType.FileAck;
}

public sealed record FileEnd(int RequestId) : Message
{
    public override MessageType Type => MessageType.FileEnd;
}

public sealed record FileError(int RequestId, FileErrorCode Code) : Message
{
    public override MessageType Type => MessageType.FileError;
}

public sealed record FileCancel(int RequestId) : Message
{
    public override MessageType Type => MessageType.FileCancel;
}

/// <summary>Версия программы (например «0.4.0»). Отправляется при рукопожатии, только если обе стороны v0.4+.</summary>
public sealed record AppVersion(string Version) : Message
{
    public const int MaxLength = 32;

    public override MessageType Type => MessageType.AppVersion;
}

/// <summary>Сообщение чата (spec-chat): простой текст не длиннее <see cref="MaxLength"/> символов.</summary>
public sealed record ChatMessage(string Text) : Message
{
    public const int MaxLength = 4000;

    /// <summary>Лимит байт UTF-8 при разборе: 4 000 символов UTF-16 — не больше 12 000 байт.</summary>
    public const int MaxBytes = 16 * 1024;

    public override MessageType Type => MessageType.ChatMessage;
}

/// <summary>Управляющий выбрал режим подстройки изображения (spec-v0.5, FR-ADP-6). Действует до конца сессии.</summary>
public sealed record StreamMode(StreamModeKind Mode) : Message
{
    public override MessageType Type => MessageType.StreamMode;
}
