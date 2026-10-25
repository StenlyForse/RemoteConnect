using Microsoft.Extensions.Time.Testing;
using RemoteConnect.Core.Chat;
using RemoteConnect.Core.Protocol;

namespace RemoteConnect.Core.Tests;

public class ChatTextTests
{
    [Theory]
    [InlineData("a\r\nb\rc\nd", "a\nb\nc\nd")]
    [InlineData("  привет \n ", "привет")]
    [InlineData("таб\tостаётся", "таб\tостаётся")]
    [InlineData("без\u0007звонка\u0000", "беззвонка")]
    [InlineData("https://a.ru/\u202Efdp.exe", "https://a.ru/fdp.exe")]
    [InlineData("\u2066x\u2069\u200E\u200F\u061C", "x")]
    public void Normalize(string input, string expected) => Assert.Equal(expected, ChatText.Normalize(input));

    [Theory]
    [InlineData("", false)]
    [InlineData("x", true)]
    public void IsSendable(string text, bool expected) => Assert.Equal(expected, ChatText.IsSendable(text));

    [Fact]
    public void IsSendable_Limit()
    {
        Assert.True(ChatText.IsSendable(new string('a', ChatMessage.MaxLength)));
        Assert.False(ChatText.IsSendable(new string('a', ChatMessage.MaxLength + 1)));
    }

    [Fact]
    public void Normalize_WhitespaceOnly_IsNotSendable() => Assert.False(ChatText.IsSendable(ChatText.Normalize(" \n\t \r\n")));
}

public class ChatLinkParserTests
{
    [Theory]
    [InlineData("https://example.com/a?b=1")]
    [InlineData("http://example.com")]
    [InlineData("HTTPS://EXAMPLE.COM/Path")]
    [InlineData("https://пример.рф/путь")]
    [InlineData("https://en.wikipedia.org/wiki/Foo_(bar)")]
    [InlineData("https://example.com:8443/x#frag")]
    public void WholeText_IsLink(string url)
    {
        var fragment = Assert.Single(ChatLinkParser.Parse(url));
        Assert.Equal(url, fragment.Text);
        Assert.NotNull(fragment.Link);
    }

    [Theory]
    [InlineData("file:///C:/Windows/notepad.exe")]
    [InlineData("javascript:alert(1)")]
    [InlineData("ms-settings:privacy")]
    [InlineData(@"\\server\share\x.exe")]
    [InlineData(@"C:\Windows\notepad.exe")]
    [InlineData("www.example.com")]
    [InlineData("https://")]
    [InlineData("https://bank.ru@evil.example/login")]
    [InlineData("xhttps://example.com")]
    public void NotALink(string text)
    {
        var fragment = Assert.Single(ChatLinkParser.Parse(text));
        Assert.Equal(text, fragment.Text);
        Assert.Null(fragment.Link);
    }

    [Fact]
    public void LinkInsideText_KeepsSurroundingTextAndPunctuation()
    {
        var fragments = ChatLinkParser.Parse("Смотри (https://example.com/a), и https://b.ru/x.\nВсё");
        Assert.Equal(
            [("Смотри (", false), ("https://example.com/a", true), ("), и ", false), ("https://b.ru/x", true), (".\nВсё", false)],
            fragments.Select(f => (f.Text, f.Link is not null)));
    }

    [Fact]
    public void Fragments_ConcatenateToOriginal()
    {
        const string text = "a https://x.ru «https://y.ru/п?q=1» file:///c:/z https://z.ru/(1)";
        Assert.Equal(text, string.Concat(ChatLinkParser.Parse(text).Select(f => f.Text)));
    }

    [Fact]
    public void EmptyText_HasNoFragments() => Assert.Empty(ChatLinkParser.Parse(""));

    [Theory]
    [InlineData("https://example.com", true)]
    [InlineData("http://example.com", true)]
    [InlineData("file:///C:/x", false)]
    [InlineData("ftp://example.com", false)]
    [InlineData("https://user@example.com", false)]
    public void IsSafe(string url, bool expected) => Assert.Equal(expected, ChatLinkParser.IsSafe(new Uri(url)));
}

public class ChatSessionTests
{
    private readonly List<Message> _sent = [];
    private readonly FakeTimeProvider _time = new(new DateTimeOffset(2026, 10, 25, 14, 30, 0, TimeSpan.Zero));
    private bool _connectionOpen = true;

    private ChatSession Create(bool autoOpen) =>
        new(m => _connectionOpen && Record(m), "Я-ПК", "Друг", autoOpen, _time);

    private bool Record(Message m)
    {
        _sent.Add(m);
        return true;
    }

    [Fact]
    public void Send_NormalizesAndAddsMine()
    {
        var chat = Create(autoOpen: false);
        Assert.True(chat.TrySend("  привет\r\nhttps://a.ru  "));

        Assert.Equal("привет\nhttps://a.ru", Assert.IsType<ChatMessage>(Assert.Single(_sent)).Text);
        var entry = Assert.Single(chat.Messages);
        Assert.Equal((ChatAuthor.Me, "Я-ПК", "привет\nhttps://a.ru"), (entry.Author, entry.Name, entry.Text));
        Assert.Equal(_time.GetLocalNow(), entry.Time);
        Assert.Equal(2, entry.Fragments.Count);
    }

    [Theory]
    [InlineData("")]
    [InlineData("  \n ")]
    public void Send_Empty_DoesNothing(string text)
    {
        var chat = Create(autoOpen: false);
        Assert.False(chat.TrySend(text));
        Assert.Empty(_sent);
        Assert.Empty(chat.Messages);
    }

    [Fact]
    public void Send_TooLong_DoesNothing()
    {
        var chat = Create(autoOpen: false);
        Assert.False(chat.TrySend(new string('a', ChatMessage.MaxLength + 1)));
        Assert.Empty(_sent);
    }

    [Fact]
    public void Send_ConnectionClosed_NotAddedAsSent()
    {
        _connectionOpen = false;
        var chat = Create(autoOpen: false);
        Assert.False(chat.TrySend("привет"));
        Assert.Empty(chat.Messages);
    }

    [Fact]
    public void Incoming_AddedAsPeer_WithLocalTime()
    {
        var chat = Create(autoOpen: false);
        _time.Advance(TimeSpan.FromMinutes(5));
        Assert.True(chat.TryHandle(new ChatMessage("ответ")));

        var entry = Assert.Single(chat.Messages);
        Assert.Equal((ChatAuthor.Peer, "Друг", "ответ"), (entry.Author, entry.Name, entry.Text));
        Assert.Equal(_time.GetLocalNow(), entry.Time);
    }

    [Fact]
    public void Incoming_Empty_IsIgnored()
    {
        var chat = Create(autoOpen: false);
        Assert.True(chat.TryHandle(new ChatMessage("\u202E \u0000")));
        Assert.Empty(chat.Messages);
        Assert.Equal(0, chat.UnreadCount);
    }

    [Fact]
    public void OtherMessages_AreNotHandled() => Assert.False(Create(autoOpen: false).TryHandle(new FrameAck(1)));

    [Fact]
    public void Controller_NeverAutoOpens_CountsUnread()
    {
        var chat = Create(autoOpen: false);
        var opened = 0;
        chat.OpenRequested += () => opened++;

        chat.TryHandle(new ChatMessage("1"));
        chat.TryHandle(new ChatMessage("2"));

        Assert.Equal(0, opened);
        Assert.False(chat.IsPanelOpen);
        Assert.Equal(2, chat.UnreadCount);

        chat.SetPanelOpen(true);
        Assert.Equal(0, chat.UnreadCount);
    }

    [Fact]
    public void Controlled_FirstIncoming_AutoOpens_ThenManualCloseDisablesIt()
    {
        var chat = Create(autoOpen: true);
        var opened = 0;
        chat.OpenRequested += () => opened++;

        chat.TryHandle(new ChatMessage("первое"));
        Assert.Equal(1, opened);
        Assert.True(chat.IsPanelOpen);
        Assert.Equal(0, chat.UnreadCount);

        chat.TryHandle(new ChatMessage("в открытую панель"));
        Assert.Equal(0, chat.UnreadCount);

        chat.SetPanelOpen(false);
        chat.TryHandle(new ChatMessage("после сворачивания"));
        Assert.Equal(1, opened);
        Assert.False(chat.IsPanelOpen);
        Assert.Equal(1, chat.UnreadCount);
    }

    [Fact]
    public void Controlled_AutoOpensOnlyOnce_EvenWithoutManualClose()
    {
        var chat = Create(autoOpen: true);
        var opened = 0;
        chat.OpenRequested += () => opened++;

        chat.TryHandle(new ChatMessage("1"));
        chat.SetPanelOpen(false);
        chat.SetPanelOpen(true);
        chat.SetPanelOpen(false);
        chat.TryHandle(new ChatMessage("2"));

        Assert.Equal(1, opened);
    }

    [Fact]
    public void Controlled_ClosedManuallyBeforeFirstIncoming_DoesNotAutoOpen()
    {
        var chat = Create(autoOpen: true);
        var opened = 0;
        chat.OpenRequested += () => opened++;

        chat.SetPanelOpen(true);
        chat.SetPanelOpen(false);
        chat.TryHandle(new ChatMessage("1"));

        Assert.Equal(0, opened);
        Assert.Equal(1, chat.UnreadCount);
    }

    [Fact]
    public void Controlled_IncomingWhileOpen_IsNotUnread()
    {
        var chat = Create(autoOpen: true);
        chat.SetPanelOpen(true);
        chat.TryHandle(new ChatMessage("пока открыта"));
        Assert.Equal(0, chat.UnreadCount);
    }

    [Fact]
    public void OwnMessages_DoNotCountAsUnread()
    {
        var chat = Create(autoOpen: false);
        chat.TrySend("моё");
        Assert.Equal(0, chat.UnreadCount);
    }

    [Fact]
    public void History_KeepsLast1000()
    {
        var chat = Create(autoOpen: false);
        var removed = new List<string>();
        chat.OldestRemoved += e => removed.Add(e.Text);

        for (var i = 0; i < ChatSession.MaxMessages + 2; i++)
        {
            chat.TryHandle(new ChatMessage(i.ToString()));
        }

        Assert.Equal(ChatSession.MaxMessages, chat.Messages.Count);
        Assert.Equal(["0", "1"], removed);
        Assert.Equal("2", chat.Messages[0].Text);
    }
}
