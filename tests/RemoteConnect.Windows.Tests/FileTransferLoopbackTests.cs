using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using RemoteConnect.Core.Files;
using RemoteConnect.Core.Protocol;
using RemoteConnect.Core.Security;
using RemoteConnect.Core.Session;
using RemoteConnect.Windows.Clipboard;
using RemoteConnect.Windows.Security;
using ComIStream = System.Runtime.InteropServices.ComTypes.IStream;
using Message = RemoteConnect.Core.Protocol.Message;

namespace RemoteConnect.Windows.Tests;

/// <summary>
/// Сквозная передача файлов по loopback через TLS (план file-clipboard, этап 5): источник на одной стороне,
/// виртуальные файлы и <see cref="RemoteFileStream"/> — на другой.
/// </summary>
public sealed class FileTransferLoopbackTests : IDisposable
{
    private static readonly X509Certificate2 Certificate = HostCertificate.Import(HostCertificate.Create());

    private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
    private readonly string _root = Directory.CreateTempSubdirectory("rc-fcb-e2e-").FullName;

    public FileTransferLoopbackTests() => _listener.Start();

    public void Dispose()
    {
        _listener.Stop();
        Directory.Delete(_root, recursive: true);
    }

    [Fact]
    public async Task FolderWithFiles_ArrivesIntact_WhileFramesKeepFlowing()
    {
        var folder = Directory.CreateDirectory(Path.Combine(_root, "Проект")).FullName;
        Directory.CreateDirectory(Path.Combine(folder, @"Документы\Старые\пустая"));
        var files = new Dictionary<string, byte[]>
        {
            [@"Проект\readme.txt"] = "Привет из RemoteConnect"u8.ToArray(),
            [@"Проект\Документы\отчёт за год.docx"] = RandomNumberGenerator.GetBytes(300_000),
            [@"Проект\Документы\Старые\пусто.txt"] = [],
            [@"Проект\видео.bin"] = RandomNumberGenerator.GetBytes(20 * 1024 * 1024),
        };
        foreach (var (relative, bytes) in files)
        {
            await File.WriteAllBytesAsync(Path.Combine(_root, relative), bytes);
        }

        var (host, client) = await ConnectAsync();
        using var source = new FileTransferSource((m, background) => host.Send(m, background));
        var receiver = new FileTransferReceiver(m => client.Send(m));
        var frameLatencies = new List<TimeSpan>();
        var frameSent = new Dictionary<int, long>();
        var offers = new TaskCompletionSource<FileOffer>();
        _ = PumpAsync(host, m => source.Handle(m));
        _ = PumpAsync(client, m =>
        {
            switch (m)
            {
                case FileOffer offer:
                    offers.TrySetResult(offer);
                    break;
                case FrameAck frame:
                    lock (frameSent)
                    {
                        frameLatencies.Add(Stopwatch.GetElapsedTime(frameSent[frame.FrameNumber]));
                    }

                    break;
                default:
                    receiver.Handle(m);
                    break;
            }
        });

        source.Offer(FileOfferBuilder.Build([folder]));
        var received = await offers.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Null(FileNameValidator.FindInvalid(received.Entries));
        Assert.Equal(
            ["Проект", @"Проект\readme.txt", @"Проект\видео.bin", @"Проект\Документы", @"Проект\Документы\отчёт за год.docx",
             @"Проект\Документы\Старые", @"Проект\Документы\Старые\пустая", @"Проект\Документы\Старые\пусто.txt"],
            received.Entries.Select(e => e.RelativePath));

        // «Кадры» обычной очередью каждые 20 мс, пока идёт передача.
        using var stopFrames = new CancellationTokenSource();
        var frames = Task.Run(async () =>
        {
            for (var i = 0; !stopFrames.IsCancellationRequested; i++)
            {
                lock (frameSent)
                {
                    frameSent[i] = Stopwatch.GetTimestamp();
                }

                host.Send(new FrameAck(i));
                await Task.Delay(20);
            }
        });

        var data = new VirtualFileDataObject(received.Entries, i => receiver.Open(received.OfferId, i, received.Entries[i].Size));
        for (var i = 0; i < received.Entries.Count; i++)
        {
            var entry = received.Entries[i];
            if (!entry.IsDirectory)
            {
                var content = await Task.Run(() => ReadAll(GetContents(data, i))).WaitAsync(TimeSpan.FromSeconds(60));
                Assert.Equal(SHA256.HashData(files[entry.RelativePath]), SHA256.HashData(content));
            }
        }

        await stopFrames.CancelAsync();
        await frames;
        lock (frameSent)
        {
            Assert.NotEmpty(frameLatencies);
            Assert.True(frameLatencies.Max() < TimeSpan.FromMilliseconds(200), $"Задержка кадра {frameLatencies.Max().TotalMilliseconds} мс");
        }

        Assert.Equal(0, receiver.OpenCount);
        await client.CloseAsync(DisconnectReason.UserClosed);
    }

    [Fact]
    public async Task ConnectionLost_DuringRead_FailsQuickly()
    {
        var path = Path.Combine(_root, "большой.bin");
        await File.WriteAllBytesAsync(path, RandomNumberGenerator.GetBytes(5 * 1024 * 1024));

        var (host, client) = await ConnectAsync();
        using var source = new FileTransferSource((m, background) => host.Send(m, background));
        var receiver = new FileTransferReceiver(m => client.Send(m));
        _ = PumpAsync(host, m => source.Handle(m));
        _ = PumpAsync(client, m => receiver.Handle(m));
        _ = client.Completion.ContinueWith(_ => receiver.FailAll(), TaskScheduler.Default);

        var offer = source.Offer(FileOfferBuilder.Build([path]));
        using var stream = receiver.Open(offer.OfferId, 0, offer.Entries[0].Size);
        var buffer = new byte[64 * 1024];
        Assert.True(stream.Read(buffer) > 0);

        // Источник «молчит»: окно заполнено, подтверждений больше нет — и тут обрыв.
        await host.CloseAsync(DisconnectReason.Error);
        var started = Stopwatch.GetTimestamp();
        Assert.ThrowsAny<IOException>(() =>
        {
            while (stream.Read(buffer) > 0)
            {
            }
        });
        Assert.True(Stopwatch.GetElapsedTime(started) < TimeSpan.FromSeconds(5));
    }

    /// <summary>План чата, этап 3: сообщение чата во время передачи файла 20 МБ доставляется быстрее 1 с (NFR-CHT-1).</summary>
    [Fact]
    public async Task ChatMessages_OvertakeFileTransfer_BothWays()
    {
        var path = Path.Combine(_root, "большой.bin");
        await File.WriteAllBytesAsync(path, RandomNumberGenerator.GetBytes(20 * 1024 * 1024));

        var (host, client) = await ConnectAsync();
        using var source = new FileTransferSource((m, background) => host.Send(m, background));
        var receiver = new FileTransferReceiver(m => client.Send(m));
        var sentAt = new Dictionary<string, long>();
        var latencies = new List<(string Text, TimeSpan Latency)>();

        void OnChat(ChatMessage chat)
        {
            lock (sentAt)
            {
                latencies.Add((chat.Text, Stopwatch.GetElapsedTime(sentAt[chat.Text])));
            }
        }

        _ = PumpAsync(host, m =>
        {
            if (m is ChatMessage chat)
            {
                OnChat(chat);
            }
            else
            {
                source.Handle(m);
            }
        });
        _ = PumpAsync(client, m =>
        {
            if (m is ChatMessage chat)
            {
                OnChat(chat);
            }
            else
            {
                receiver.Handle(m);
            }
        });

        var offer = source.Offer(FileOfferBuilder.Build([path]));
        using var stopChat = new CancellationTokenSource();
        var chatter = Task.Run(async () =>
        {
            for (var i = 0; !stopChat.IsCancellationRequested; i++)
            {
                // Текст — уникальный ключ для замера; сторона хоста — источник файла (её очередь забита частями файла).
                var fromHost = $"от хоста {i} — https://example.com/{i}";
                var fromClient = $"от клиента {i}";
                lock (sentAt)
                {
                    sentAt[fromHost] = Stopwatch.GetTimestamp();
                    sentAt[fromClient] = Stopwatch.GetTimestamp();
                }

                host.Send(new ChatMessage(fromHost));
                client.Send(new ChatMessage(fromClient));
                await Task.Delay(50);
            }
        });

        var content = await Task.Run(() =>
        {
            using var stream = receiver.Open(offer.OfferId, 0, offer.Entries[0].Size);
            using var copy = new MemoryStream();
            stream.CopyTo(copy, 1024 * 1024);
            return copy.ToArray();
        }).WaitAsync(TimeSpan.FromSeconds(60));

        await stopChat.CancelAsync();
        await chatter;
        await Task.Delay(200);

        Assert.Equal(SHA256.HashData(await File.ReadAllBytesAsync(path)), SHA256.HashData(content));
        lock (sentAt)
        {
            Assert.Contains(latencies, l => l.Text.StartsWith("от хоста", StringComparison.Ordinal));
            Assert.Contains(latencies, l => l.Text.StartsWith("от клиента", StringComparison.Ordinal));
            var worst = latencies.MaxBy(l => l.Latency);
            Assert.True(worst.Latency < TimeSpan.FromSeconds(1), $"«{worst.Text}» шло {worst.Latency.TotalMilliseconds} мс");
        }

        await client.CloseAsync(DisconnectReason.UserClosed);
    }

    private static async Task PumpAsync(Connection connection, Action<Message> handle)
    {
        try
        {
            while (true)
            {
                handle(await connection.ReceiveAsync());
            }
        }
        catch (ConnectionClosedException)
        {
        }
    }

    private async Task<(Connection Host, Connection Client)> ConnectAsync()
    {
        var accept = _listener.AcceptTcpClientAsync();
        var tcp = await SecureTransport.ConnectTcpAsync("127.0.0.1", ((IPEndPoint)_listener.LocalEndpoint).Port, TimeSpan.FromSeconds(5), default);
        var hostTcp = await accept;
        var serverTask = SecureTransport.AuthenticateAsServerAsync(hostTcp.GetStream(), Certificate, default);
        var (clientSsl, _) = await SecureTransport.AuthenticateAsClientAsync(tcp.GetStream(), default);
        var host = new Connection(await serverTask, hostTcp.Client.RemoteEndPoint);
        var client = new Connection(clientSsl, tcp.Client.RemoteEndPoint);
        host.Start();
        client.Start();
        return (host, client);
    }

    private static ComIStream GetContents(VirtualFileDataObject data, int index)
    {
        var request = new FORMATETC
        {
            cfFormat = (short)RegisterClipboardFormat(VirtualFileDataObject.FileContentsFormat),
            dwAspect = DVASPECT.DVASPECT_CONTENT,
            lindex = index,
            tymed = TYMED.TYMED_ISTREAM,
        };
        data.GetData(ref request, out var medium);
        var stream = (ComIStream)Marshal.GetObjectForIUnknown(medium.unionmember);
        Marshal.Release(medium.unionmember);
        return stream;
    }

    private static byte[] ReadAll(ComIStream stream)
    {
        var result = new MemoryStream();
        var buffer = new byte[1024 * 1024];
        var count = Marshal.AllocCoTaskMem(4);
        try
        {
            while (true)
            {
                stream.Read(buffer, buffer.Length, count);
                var n = Marshal.ReadInt32(count);
                if (n == 0)
                {
                    return result.ToArray();
                }

                result.Write(buffer, 0, n);
            }
        }
        finally
        {
            Marshal.FreeCoTaskMem(count);
        }
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern uint RegisterClipboardFormat(string format);
}
