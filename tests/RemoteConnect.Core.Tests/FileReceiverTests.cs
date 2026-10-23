using System.Security.Cryptography;
using System.Threading.Channels;
using RemoteConnect.Core.Files;
using RemoteConnect.Core.Protocol;

namespace RemoteConnect.Core.Tests;

public sealed class FileReceiverTests : IDisposable
{
    private static readonly TimeSpan Wait = TimeSpan.FromSeconds(5);
    private readonly Channel<Message> _sent = Channel.CreateUnbounded<Message>();
    private readonly string _root = Path.Combine(Path.GetTempPath(), "rc-fcb-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    // ---------------- FileNameValidator ----------------

    [Theory]
    [InlineData(@"..\..\Windows\evil.dll")]
    [InlineData(@"a\..\b")]
    [InlineData(@"..")]
    [InlineData(@".")]
    [InlineData(@"C:\x")]
    [InlineData(@"C:x")]
    [InlineData(@"\\server\x")]
    [InlineData(@"\x")]
    [InlineData(@"a\")]
    [InlineData(@"a\\b")]
    [InlineData(@"a/b")]
    [InlineData(@"CON")]
    [InlineData(@"CON.txt")]
    [InlineData(@"папка\nul.tar.gz")]
    [InlineData(@"com1")]
    [InlineData(@"LPT9.log")]
    [InlineData(@"COM¹")]
    [InlineData(@"a:b")]
    [InlineData(@"файл.txt:скрытый")]
    [InlineData("a<b")]
    [InlineData("a>b")]
    [InlineData("a\"b")]
    [InlineData("a|b")]
    [InlineData("a?b")]
    [InlineData("a*b")]
    [InlineData("a\tb")]
    [InlineData("a\u0001b")]
    [InlineData("имя.")]
    [InlineData("имя ")]
    [InlineData("")]
    public void Validator_RejectsDangerousPath(string path)
    {
        Assert.False(FileNameValidator.IsValidPath(path));
    }

    [Theory]
    [InlineData("отчёт за 2026 год.docx")]
    [InlineData(@"Папка\Вложенная\пустая")]
    [InlineData(@"Фото\IMG_0001.JPG")]
    [InlineData(".gitignore")]
    [InlineData("CONSOLE.txt")]
    [InlineData("com10")]
    [InlineData("a..b")]
    [InlineData("日本語のファイル.txt")]
    public void Validator_AcceptsNormalPath(string path)
    {
        Assert.True(FileNameValidator.IsValidPath(path));
    }

    [Fact]
    public void Validator_RejectsTooLongNames()
    {
        Assert.False(FileNameValidator.IsValidPath(new string('я', 256)));
        Assert.True(FileNameValidator.IsValidPath(new string('я', 255)));
        Assert.False(FileNameValidator.IsValidPath(new string('a', 200) + @"\" + new string('b', 59)));
    }

    [Fact]
    public void Validator_RejectsWholeSetWithOneBadEntryOrDuplicates()
    {
        var date = DateTime.UnixEpoch;
        FileOfferEntry[] good = [new("a", true, 0, date), new(@"a\b.txt", false, 1, date)];
        Assert.Null(FileNameValidator.FindInvalid(good));
        Assert.Equal(@"..\evil.dll", FileNameValidator.FindInvalid([.. good, new(@"..\evil.dll", false, 1, date)]));
        Assert.Equal(@"A\B.txt", FileNameValidator.FindInvalid([.. good, new(@"A\B.txt", false, 1, date)]));
        Assert.NotNull(FileNameValidator.FindInvalid([]));
    }

    // ---------------- RemoteFileStream ----------------

    [Fact]
    public async Task Stream_RequestsDataOnlyOnFirstRead()
    {
        var receiver = Receiver();
        using var stream = receiver.Open(offerId: 3, index: 5, size: 10);
        Assert.False(_sent.Reader.TryRead(out _));

        var read = Task.Run(() => stream.Read(new byte[10]));
        var open = Assert.IsType<FileOpen>(await Next());
        Assert.Equal((stream.RequestId, 3, 5), (open.RequestId, open.OfferId, open.Index));

        receiver.Handle(new FileChunk(stream.RequestId, [1, 2, 3]));
        Assert.Equal(3, await read.WaitAsync(Wait));
    }

    [Fact]
    public async Task Stream_ReadsInPartsAndAcknowledges()
    {
        var data = RandomNumberGenerator.GetBytes(700_000);
        var receiver = Receiver();
        using var stream = receiver.Open(1, 0, data.Length);
        foreach (var part in data.Chunk(FileChunk.MaxSize))
        {
            receiver.Handle(new FileChunk(stream.RequestId, part));
        }

        var result = new MemoryStream();
        var buffer = new byte[10_000];
        while (result.Length < data.Length)
        {
            result.Write(buffer, 0, stream.Read(buffer));
        }

        receiver.Handle(new FileEnd(stream.RequestId));
        Assert.Equal(0, stream.Read(buffer));
        Assert.Equal(data, result.ToArray());
        var messages = Drain();
        Assert.IsType<FileOpen>(messages[0]);
        var acks = messages.OfType<FileAck>().Select(a => a.BytesConsumed).ToList();
        Assert.Equal(2, acks.Count);
        Assert.All(acks, a => Assert.True(a >= RemoteFileStream.AckStepBytes));
        Assert.True(acks[1] - acks[0] >= RemoteFileStream.AckStepBytes);
        Assert.DoesNotContain(messages, m => m is FileCancel);
    }

    [Fact]
    public void Stream_EmptyFile_EndsWithoutRequest()
    {
        var receiver = Receiver();
        using var stream = receiver.Open(1, 0, 0);

        Assert.Equal(0, stream.Read(new byte[10]));
        Assert.Empty(Drain());
    }

    [Fact]
    public void Stream_TimesOutAndCancels()
    {
        var receiver = Receiver(TimeSpan.FromMilliseconds(200));
        var stream = receiver.Open(1, 0, 10);

        var started = DateTime.UtcNow;
        var error = Assert.ThrowsAny<IOException>(() => stream.Read(new byte[10]));
        Assert.IsType<TimeoutException>(error.InnerException);
        Assert.InRange(DateTime.UtcNow - started, TimeSpan.FromMilliseconds(150), TimeSpan.FromSeconds(3));

        stream.Dispose();
        Assert.Contains(Drain(), m => m is FileCancel c && c.RequestId == stream.RequestId);
        Assert.Equal(0, receiver.OpenCount);
    }

    [Fact]
    public async Task Stream_ClosedBeforeEnd_SendsCancel()
    {
        var receiver = Receiver();
        var stream = receiver.Open(1, 0, 100);
        var read = Task.Run(() => stream.Read(new byte[10]));
        await Next();
        receiver.Handle(new FileChunk(stream.RequestId, new byte[10]));
        await read.WaitAsync(Wait);

        stream.Dispose();

        Assert.Equal([new FileCancel(stream.RequestId)], Drain());
    }

    [Fact]
    public async Task Stream_SourceError_Throws()
    {
        var receiver = Receiver();
        using var stream = receiver.Open(1, 0, 100);
        var read = Task.Run(() => stream.Read(new byte[10]));
        await Next();

        receiver.Handle(new FileError(stream.RequestId, FileErrorCode.NotFound));

        var error = await Assert.ThrowsAsync<RemoteFileException>(() => read.WaitAsync(Wait));
        Assert.Equal(FileErrorCode.NotFound, error.Code);
        stream.Dispose();
        Assert.DoesNotContain(Drain(), m => m is FileCancel);
    }

    [Fact]
    public void Stream_MoreDataThanSize_IsChanged()
    {
        var receiver = Receiver();
        using var stream = receiver.Open(1, 0, 4);
        receiver.Handle(new FileChunk(stream.RequestId, new byte[5]));

        var error = Assert.Throws<RemoteFileException>(() => stream.Read(new byte[10]));
        Assert.Equal(FileErrorCode.Changed, error.Code);
    }

    [Fact]
    public void Stream_EndBeforeAllData_IsChanged()
    {
        var receiver = Receiver();
        using var stream = receiver.Open(1, 0, 10);
        receiver.Handle(new FileChunk(stream.RequestId, new byte[4]));
        receiver.Handle(new FileEnd(stream.RequestId));

        Assert.Equal(4, stream.Read(new byte[10]));
        Assert.Equal(FileErrorCode.Changed, Assert.Throws<RemoteFileException>(() => stream.Read(new byte[10])).Code);
    }

    [Fact]
    public async Task FailAll_InterruptsBlockedReadImmediately()
    {
        var receiver = Receiver();
        using var stream = receiver.Open(1, 0, 100);
        var read = Task.Run(() => stream.Read(new byte[10]));
        await Next();

        receiver.FailAll();

        await Assert.ThrowsAnyAsync<IOException>(() => read.WaitAsync(TimeSpan.FromSeconds(1)));
        using var later = receiver.Open(1, 1, 100);
        Assert.ThrowsAny<IOException>(() => later.Read(new byte[10]));
    }

    // ---------------- Источник ↔ получатель ----------------

    [Fact]
    public async Task SourceAndReceiver_TransferFileThroughWindow()
    {
        Directory.CreateDirectory(_root);
        var path = Path.Combine(_root, "большой файл.bin");
        var data = RandomNumberGenerator.GetBytes((3 * FileTransferSource.WindowBytes) + 12345);
        await File.WriteAllBytesAsync(path, data);

        FileTransferReceiver? receiver = null;
        using var source = new FileTransferSource((m, _) => receiver!.Handle(m));
        receiver = new FileTransferReceiver(m => source.Handle(m));
        var offer = source.Offer(FileOfferBuilder.Build([path]));

        await using var stream = receiver.Open(offer.OfferId, 0, data.Length);
        var copy = new MemoryStream();
        await Task.Run(() => stream.CopyTo(copy, 50_000)).WaitAsync(TimeSpan.FromSeconds(30));

        Assert.Equal(SHA256.HashData(data), SHA256.HashData(copy.ToArray()));
    }

    private FileTransferReceiver Receiver(TimeSpan? timeout = null) => new(m => _sent.Writer.TryWrite(m), timeout);

    private async Task<Message> Next() => await _sent.Reader.ReadAsync().AsTask().WaitAsync(Wait);

    private List<Message> Drain()
    {
        var result = new List<Message>();
        while (_sent.Reader.TryRead(out var message))
        {
            result.Add(message);
        }

        return result;
    }
}
