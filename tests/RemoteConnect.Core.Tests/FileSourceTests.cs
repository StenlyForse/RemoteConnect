using System.Threading.Channels;
using RemoteConnect.Core.Files;
using RemoteConnect.Core.Protocol;

namespace RemoteConnect.Core.Tests;

public sealed class FileSourceTests : IDisposable
{
    private static readonly TimeSpan Wait = TimeSpan.FromSeconds(5);
    private readonly string _root = Path.Combine(Path.GetTempPath(), "rc-fcb-" + Guid.NewGuid().ToString("N"));
    private readonly Channel<(Message Message, bool Background)> _sent = Channel.CreateUnbounded<(Message, bool)>();

    public FileSourceTests()
    {
        Directory.CreateDirectory(_root);
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    // ---------------- FileOfferBuilder ----------------

    [Fact]
    public void Build_FolderWithSubfoldersAndEmptyFolder()
    {
        var folder = Dir("Папка");
        Dir(@"Папка\Вложенная\пустая");
        File(@"Папка\б.txt", 5);
        File(@"Папка\Вложенная\а.bin", 100);
        var date = new DateTime(2025, 1, 2, 3, 4, 5, DateTimeKind.Utc);
        System.IO.File.SetLastWriteTimeUtc(Path.Combine(_root, @"Папка\б.txt"), date);
        File("отдельный.txt", 7);

        var result = FileOfferBuilder.Build([folder, Path.Combine(_root, "отдельный.txt")]);

        Assert.True(result.IsOk);
        Assert.Equal(
            ["Папка", @"Папка\б.txt", @"Папка\Вложенная", @"Папка\Вложенная\а.bin", @"Папка\Вложенная\пустая", "отдельный.txt"],
            result.Entries.Select(e => e.RelativePath));
        Assert.Equal([true, false, true, false, true, false], result.Entries.Select(e => e.IsDirectory));
        Assert.Equal([0L, 5, 0, 100, 0, 7], result.Entries.Select(e => e.Size));
        Assert.Equal(112, result.TotalBytes);
        Assert.Equal(date, result.Entries[1].LastWriteUtc);
        Assert.Equal(Path.Combine(_root, @"Папка\Вложенная\а.bin"), result.SourcePaths[3]);
    }

    [Fact]
    public void Build_IncludesHiddenFiles()
    {
        var folder = Dir("f");
        var hidden = File(@"f\hidden.txt", 1);
        System.IO.File.SetAttributes(hidden, FileAttributes.Hidden);

        var result = FileOfferBuilder.Build([folder]);

        Assert.Contains(@"f\hidden.txt", result.Entries.Select(e => e.RelativePath));
    }

    [Fact]
    public void Build_SameNamesFromDifferentFolders_GetSuffix()
    {
        var a = File(@"a\отчёт.txt", 1);
        var b = File(@"b\отчёт.txt", 2);

        var result = FileOfferBuilder.Build([a, b]);

        Assert.Equal(["отчёт.txt", "отчёт (2).txt"], result.Entries.Select(e => e.RelativePath));
    }

    [Fact]
    public void Build_TooLarge()
    {
        var big = Path.Combine(_root, "big.bin");
        using (var stream = System.IO.File.Create(big))
        {
            stream.SetLength(FileOfferBuilder.MaxTotalBytes + 1);
        }

        var result = FileOfferBuilder.Build([big]);

        Assert.Equal(FileOfferBuildStatus.TooLarge, result.Status);
        Assert.Equal(FileOfferBuilder.MaxTotalBytes + 1, result.TotalBytes);
        Assert.Empty(result.Entries);
    }

    [Fact]
    public void Build_TooManyItems()
    {
        var folder = Dir("many");
        for (var i = 0; i < FileOfferBuilder.MaxEntries; i++)
        {
            System.IO.File.Create(Path.Combine(folder, $"{i}.txt")).Dispose();
        }

        // Папка + 10 000 файлов = 10 001 элемент.
        Assert.Equal(FileOfferBuildStatus.TooManyItems, FileOfferBuilder.Build([folder]).Status);
    }

    [Fact]
    public void Build_TimesOut()
    {
        var folder = Dir("slow");
        File(@"slow\a.txt", 1);

        Assert.Equal(FileOfferBuildStatus.TimedOut, FileOfferBuilder.Build([folder], TimeSpan.Zero).Status);
    }

    [Fact]
    public void Build_MissingPath_IsError()
    {
        Assert.Equal(FileOfferBuildStatus.Error, FileOfferBuilder.Build([Path.Combine(_root, "нет")]).Status);
    }

    // ---------------- FileTransferSource ----------------

    [Fact]
    public async Task Transfer_SendsWholeFileThenEnd()
    {
        var data = File("данные.bin", 200_000);
        using var source = Source();
        var offer = source.Offer(FileOfferBuilder.Build([data]));
        await Next<FileOffer>();

        source.Handle(new FileOpen(1, offer.OfferId, 0));
        var received = await ReceiveFileAsync(1);

        Assert.Equal(await System.IO.File.ReadAllBytesAsync(data), received);
        await WaitUntil(() => source.ActiveTransfers.Count == 0);
    }

    [Fact]
    public async Task Transfer_ChunksEndAndErrorsGoToBackgroundQueue()
    {
        var data = File("a.bin", 10);
        using var source = Source();
        var offer = source.Offer(FileOfferBuilder.Build([data]));
        var (_, offerBackground) = await _sent.Reader.ReadAsync().AsTask().WaitAsync(Wait);
        Assert.False(offerBackground);

        source.Handle(new FileOpen(1, offer.OfferId, 0));
        var chunk = await _sent.Reader.ReadAsync().AsTask().WaitAsync(Wait);
        var end = await _sent.Reader.ReadAsync().AsTask().WaitAsync(Wait);

        Assert.IsType<FileChunk>(chunk.Message);
        Assert.IsType<FileEnd>(end.Message);
        Assert.True(chunk.Background && end.Background);
    }

    [Fact]
    public async Task Transfer_WithoutAcks_StopsAtWindow()
    {
        var data = File("big.bin", 3 * FileTransferSource.WindowBytes);
        using var source = Source();
        var offer = source.Offer(FileOfferBuilder.Build([data]));
        await Next<FileOffer>();

        source.Handle(new FileOpen(1, offer.OfferId, 0));
        var sent = await DrainBytesAsync();
        Assert.Equal(FileTransferSource.WindowBytes, sent);

        source.Handle(new FileAck(1, 512 * 1024));
        sent += await DrainBytesAsync();
        Assert.Equal(FileTransferSource.WindowBytes + (512 * 1024), sent);
    }

    [Fact]
    public async Task Open_WrongOffer_IsExpired()
    {
        var data = File("a.txt", 1);
        using var source = Source();
        var first = source.Offer(FileOfferBuilder.Build([data]));
        var second = source.Offer(FileOfferBuilder.Build([data]));

        source.Handle(new FileOpen(1, first.OfferId, 0));
        source.Handle(new FileOpen(2, second.OfferId + 100, 0));

        Assert.Equal(FileErrorCode.OfferExpired, (await Next<FileError>()).Code);
        Assert.Equal(FileErrorCode.OfferExpired, (await Next<FileError>()).Code);
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(3)]
    [InlineData(0)] // папка
    public async Task Open_BadIndex_IsRejected(int index)
    {
        var folder = Dir("f");
        File(@"f\a.txt", 1);
        File(@"f\b.txt", 1);
        using var source = Source();
        var offer = source.Offer(FileOfferBuilder.Build([folder]));

        source.Handle(new FileOpen(7, offer.OfferId, index));

        var error = await Next<FileError>();
        Assert.Equal((7, FileErrorCode.BadRequest), (error.RequestId, error.Code));
    }

    [Fact]
    public async Task Open_DeletedFile_IsNotFound()
    {
        var data = File("a.txt", 1);
        using var source = Source();
        var offer = source.Offer(FileOfferBuilder.Build([data]));
        System.IO.File.Delete(data);

        source.Handle(new FileOpen(1, offer.OfferId, 0));

        Assert.Equal(FileErrorCode.NotFound, (await Next<FileError>()).Code);
    }

    [Fact]
    public async Task Open_ChangedSize_IsChanged()
    {
        var data = File("a.txt", 10);
        using var source = Source();
        var offer = source.Offer(FileOfferBuilder.Build([data]));
        await System.IO.File.AppendAllTextAsync(data, "ещё");

        source.Handle(new FileOpen(1, offer.OfferId, 0));

        Assert.Equal(FileErrorCode.Changed, (await Next<FileError>()).Code);
    }

    [Fact]
    public async Task Open_LockedFile_IsAccessDenied()
    {
        var data = File("a.txt", 10);
        using var source = Source();
        var offer = source.Offer(FileOfferBuilder.Build([data]));
        using var exclusive = new FileStream(data, FileMode.Open, FileAccess.ReadWrite, FileShare.None);

        source.Handle(new FileOpen(1, offer.OfferId, 0));

        Assert.Equal(FileErrorCode.AccessDenied, (await Next<FileError>()).Code);
    }

    [Fact]
    public async Task Cancel_StopsTransferSilently()
    {
        var data = File("big.bin", 3 * FileTransferSource.WindowBytes);
        using var source = Source();
        var offer = source.Offer(FileOfferBuilder.Build([data]));
        await Next<FileOffer>();
        source.Handle(new FileOpen(1, offer.OfferId, 0));
        await DrainBytesAsync();

        source.Handle(new FileCancel(1));
        source.Handle(new FileAck(1, FileTransferSource.WindowBytes));

        await WaitUntil(() => source.ActiveTransfers.Count == 0);
        Assert.False(_sent.Reader.TryRead(out _));
    }

    [Fact]
    public async Task Revoke_FailsActiveTransferAndRejectsNewRequests()
    {
        var data = File("big.bin", 3 * FileTransferSource.WindowBytes);
        using var source = Source();
        var offer = source.Offer(FileOfferBuilder.Build([data]));
        await Next<FileOffer>();
        source.Handle(new FileOpen(1, offer.OfferId, 0));
        await DrainBytesAsync();

        source.Revoke();
        Assert.Equal((1, FileErrorCode.OfferExpired), Describe(await Next<FileError>()));

        source.Handle(new FileOpen(2, offer.OfferId, 0));
        Assert.Equal((2, FileErrorCode.OfferExpired), Describe(await Next<FileError>()));
        Assert.Null(source.CurrentOfferId);
    }

    [Fact]
    public async Task ActiveTransfers_ReportsProgress()
    {
        var data = File("отчёт.pdf", 3 * FileTransferSource.WindowBytes);
        using var source = Source();
        var offer = source.Offer(FileOfferBuilder.Build([data]));
        source.Handle(new FileOpen(1, offer.OfferId, 0));
        await WaitUntil(() => source.ActiveTransfers is [{ Sent: FileTransferSource.WindowBytes }]);

        Assert.Equal(new FileTransferProgress("отчёт.pdf", FileTransferSource.WindowBytes, 3 * FileTransferSource.WindowBytes), source.ActiveTransfers[0]);
    }

    private static (int, FileErrorCode) Describe(FileError error) => (error.RequestId, error.Code);

    private static async Task WaitUntil(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow + Wait;
        while (!condition())
        {
            Assert.True(DateTime.UtcNow < deadline, "Условие не выполнилось");
            await Task.Delay(10);
        }
    }

    private FileTransferSource Source() => new((message, background) => _sent.Writer.TryWrite((message, background)));

    private string Dir(string relative) => Directory.CreateDirectory(Path.Combine(_root, relative)).FullName;

    private string File(string relative, int size)
    {
        var path = Path.Combine(_root, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var data = new byte[size];
        new Random(size).NextBytes(data);
        System.IO.File.WriteAllBytes(path, data);
        return path;
    }

    /// <summary>Следующее отправленное сообщение; <see cref="FileOffer"/> пропускается, если ждём не его.</summary>
    private async Task<T> Next<T>() where T : Message
    {
        while (true)
        {
            var (message, _) = await _sent.Reader.ReadAsync().AsTask().WaitAsync(Wait);
            if (message is FileOffer && typeof(T) != typeof(FileOffer))
            {
                continue;
            }

            return Assert.IsType<T>(message);
        }
    }

    /// <summary>Читает части файла, пока отправка не остановится на 300 мс; возвращает число байт.</summary>
    private async Task<long> DrainBytesAsync()
    {
        long total = 0;
        while (true)
        {
            using var idle = new CancellationTokenSource(TimeSpan.FromMilliseconds(300));
            try
            {
                var (message, _) = await _sent.Reader.ReadAsync(idle.Token);
                total += Assert.IsType<FileChunk>(message).Data.Length;
            }
            catch (OperationCanceledException)
            {
                return total;
            }
        }
    }

    /// <summary>Принимает файл меньше окна отправки (подтверждения не нужны).</summary>
    private async Task<byte[]> ReceiveFileAsync(int requestId)
    {
        var result = new MemoryStream();
        while (true)
        {
            var (message, _) = await _sent.Reader.ReadAsync().AsTask().WaitAsync(Wait);
            switch (message)
            {
                case FileChunk chunk when chunk.RequestId == requestId:
                    result.Write(chunk.Data);
                    break;
                case FileEnd end when end.RequestId == requestId:
                    return result.ToArray();
                default:
                    Assert.Fail($"Неожиданное сообщение {message}");
                    break;
            }
        }
    }
}
