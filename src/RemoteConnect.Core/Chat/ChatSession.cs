using RemoteConnect.Core.Protocol;

namespace RemoteConnect.Core.Chat;

public enum ChatAuthor
{
    Me,
    Peer,
}

/// <summary>Сообщение в истории: кто, имя, время по часам своего ПК, текст и его фрагменты со ссылками.</summary>
public sealed record ChatEntry(ChatAuthor Author, string Name, DateTimeOffset Time, string Text, IReadOnlyList<ChatFragment> Fragments);

/// <summary>
/// Чат одной сессии (spec-chat): история в памяти до конца сессии (FR-CHT-6), отправка и приём,
/// непрочитанные (FR-CHT-23) и правило автораскрытия панели (FR-CHT-21). Текст сообщений никуда не пишется.
/// Не потокобезопасен: все вызовы — с одного (UI) потока.
/// </summary>
public sealed class ChatSession(
    Func<Message, bool> send,
    string myName,
    string peerName,
    bool autoOpenOnFirstIncoming,
    TimeProvider time)
{
    /// <summary>Сколько сообщений хранится; более старые удаляются из начала.</summary>
    public const int MaxMessages = 1000;

    private readonly List<ChatEntry> _messages = [];
    private bool _autoOpenAllowed = autoOpenOnFirstIncoming;

    public IReadOnlyList<ChatEntry> Messages => _messages;

    public bool IsPanelOpen { get; private set; }

    public int UnreadCount { get; private set; }

    /// <summary>Сообщение добавлено в конец истории.</summary>
    public event Action<ChatEntry>? MessageAdded;

    /// <summary>Самое старое сообщение удалено из-за лимита <see cref="MaxMessages"/>.</summary>
    public event Action<ChatEntry>? OldestRemoved;

    /// <summary>Панель нужно раскрыть автоматически — без передачи фокуса (FR-CHT-22).</summary>
    public event Action? OpenRequested;

    public event Action<int>? UnreadChanged;

    /// <summary>
    /// Нормализует и отправляет текст. false — отправлять нечего (пусто, слишком длинно) или соединение закрыто;
    /// в историю в этом случае ничего не добавляется (FR-CHT-5).
    /// </summary>
    public bool TrySend(string text)
    {
        var normalized = ChatText.Normalize(text);
        if (!ChatText.IsSendable(normalized) || !send(new ChatMessage(normalized)))
        {
            return false;
        }

        Add(ChatAuthor.Me, myName, normalized);
        return true;
    }

    /// <summary>Обрабатывает входящее сообщение, если это сообщение чата.</summary>
    public bool TryHandle(Message message)
    {
        if (message is not ChatMessage chat)
        {
            return false;
        }

        var normalized = ChatText.Normalize(chat.Text);
        if (!ChatText.IsSendable(normalized))
        {
            return true;
        }

        Add(ChatAuthor.Peer, peerName, normalized);
        if (IsPanelOpen)
        {
            return true;
        }

        if (_autoOpenAllowed)
        {
            _autoOpenAllowed = false;
            IsPanelOpen = true;
            OpenRequested?.Invoke();
        }
        else
        {
            SetUnread(UnreadCount + 1);
        }

        return true;
    }

    /// <summary>
    /// Пользователь раскрыл или свернул панель. Раскрытие снимает непрочитанные; ручное сворачивание
    /// отключает автораскрытие до конца сессии (FR-CHT-21).
    /// </summary>
    public void SetPanelOpen(bool open)
    {
        IsPanelOpen = open;
        if (open)
        {
            SetUnread(0);
        }
        else
        {
            _autoOpenAllowed = false;
        }
    }

    private void Add(ChatAuthor author, string name, string text)
    {
        var entry = new ChatEntry(author, name, time.GetLocalNow(), text, ChatLinkParser.Parse(text));
        _messages.Add(entry);
        MessageAdded?.Invoke(entry);

        if (_messages.Count > MaxMessages)
        {
            var oldest = _messages[0];
            _messages.RemoveAt(0);
            OldestRemoved?.Invoke(oldest);
        }
    }

    private void SetUnread(int count)
    {
        if (count != UnreadCount)
        {
            UnreadCount = count;
            UnreadChanged?.Invoke(count);
        }
    }
}
