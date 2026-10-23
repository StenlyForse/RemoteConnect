using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;
using System.Security.Cryptography;
using RemoteConnect.Core.Files;
using RemoteConnect.Core.Protocol;
using RemoteConnect.Windows.Clipboard;
using ComIDataObject = System.Runtime.InteropServices.ComTypes.IDataObject;
using ComIStream = System.Runtime.InteropServices.ComTypes.IStream;

namespace RemoteConnect.Windows.Tests;

/// <summary>
/// Виртуальные файлы: дескриптор и содержимое так, как их запрашивает Проводник.
/// Тест <see cref="OleClipboard_RoundTrip"/> перезаписывает буфер обмена Windows.
/// </summary>
public class VirtualFileDataObjectTests
{
    private const int DV_E_LINDEX = unchecked((int)0x80040068);
    private const uint FD_ATTRIBUTES = 0x4;
    private const uint FD_WRITESTIME = 0x20;
    private const uint FD_FILESIZE = 0x40;
    private const uint FD_PROGRESSUI = 0x4000;
    private const uint FD_UNICODE = 0x80000000;

    private static readonly DateTime Date = new(2025, 3, 4, 5, 6, 7, DateTimeKind.Utc);

    private static readonly FileOfferEntry[] Entries =
    [
        new("Папка", true, 0, Date),
        new(@"Папка\пустая", true, 0, Date),
        new(@"Папка\отчёт.txt", false, 5, Date),
        new("большой.bin", false, 5_000_000_000, Date.AddHours(1)),
    ];

    [Fact]
    public void Descriptor_DescribesAllEntries()
    {
        var data = new VirtualFileDataObject(Entries, _ => Stream.Null);

        var descriptors = ParseDescriptor(GetHGlobal(data, VirtualFileDataObject.FileDescriptorFormat));

        Assert.Equal(Entries.Select(e => e.RelativePath), descriptors.Select(d => d.Name));
        Assert.All(descriptors, d => Assert.Equal(FD_ATTRIBUTES | FD_WRITESTIME | FD_FILESIZE | FD_PROGRESSUI | FD_UNICODE, d.Flags));
        Assert.Equal([0x10u, 0x10u, 0x80u, 0x80u], descriptors.Select(d => d.Attributes));
        Assert.Equal([0L, 0, 5, 5_000_000_000], descriptors.Select(d => d.Size));
        Assert.Equal(Entries.Select(e => e.LastWriteUtc), descriptors.Select(d => d.LastWriteUtc));
    }

    [Fact]
    public void PreferredDropEffect_IsCopy()
    {
        var data = new VirtualFileDataObject(Entries, _ => Stream.Null);

        Assert.Equal(1, BitConverter.ToInt32(GetHGlobal(data, VirtualFileDataObject.PreferredDropEffectFormat)));
    }

    [Fact]
    public void FileContents_ReturnsStreamForFile()
    {
        var content = System.Text.Encoding.UTF8.GetBytes("Привет");
        var data = new VirtualFileDataObject(Entries, i => i == 2 ? new MemoryStream(content) : throw new InvalidOperationException());

        var stream = GetContents(data, 2);

        Assert.Equal(content, ReadAll(stream));
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(0)] // папка
    [InlineData(4)]
    public void FileContents_RejectsBadIndex(int index)
    {
        var data = new VirtualFileDataObject(Entries, _ => Stream.Null);

        var error = Assert.Throws<COMException>(() => GetContents(data, index));
        Assert.Equal(DV_E_LINDEX, error.HResult);
    }

    [Fact]
    public void FileContents_FromRemoteSource_RequestsDataOnlyWhenRead()
    {
        var root = Directory.CreateTempSubdirectory("rc-vfdo-").FullName;
        try
        {
            var path = Path.Combine(root, "данные.bin");
            var bytes = RandomNumberGenerator.GetBytes(3_000_000);
            File.WriteAllBytes(path, bytes);

            var requests = 0;
            FileTransferReceiver? receiver = null;
            using var source = new FileTransferSource((m, _) => receiver!.Handle(m));
            receiver = new FileTransferReceiver(m =>
            {
                if (m is FileOpen)
                {
                    Interlocked.Increment(ref requests);
                }

                return source.Handle(m);
            });
            var offer = source.Offer(FileOfferBuilder.Build([path]));
            var data = new VirtualFileDataObject(offer.Entries, i => receiver.Open(offer.OfferId, i, offer.Entries[i].Size));

            var stream = GetContents(data, 0);
            Assert.Equal(0, requests);

            Assert.Equal(SHA256.HashData(bytes), SHA256.HashData(ReadAll(stream)));
            Assert.Equal(1, requests);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    /// <summary>
    /// Настоящий буфер OLE: объект кладётся на одном STA-потоке и читается через OleGetClipboard на другом —
    /// тот же путь, что у Проводника (обёртка буфера, список форматов, lindex).
    /// </summary>
    [Fact]
    public void OleClipboard_RoundTrip()
    {
        var content = System.Text.Encoding.UTF8.GetBytes("содержимое файла");
        var data = new VirtualFileDataObject(Entries, i => new MemoryStream(content));
        using var placed = new ManualResetEventSlim();
        using var done = new ManualResetEventSlim();
        Exception? ownerError = null;

        var owner = new Thread(() =>
        {
            try
            {
                OleClipboard.Set(data);
                placed.Set();
                while (!done.Wait(10))
                {
                    System.Windows.Forms.Application.DoEvents();
                }

                OleClipboard.ClearIfCurrent(data);
            }
            catch (Exception ex)
            {
                ownerError = ex;
                placed.Set();
            }
        });
        owner.SetApartmentState(ApartmentState.STA);
        owner.Start();

        List<Descriptor>? descriptors = null;
        byte[]? read = null;
        Exception? readerError = null;
        try
        {
            Assert.True(placed.Wait(TimeSpan.FromSeconds(5)));
            Assert.Null(ownerError);
            var reader = new Thread(() =>
            {
                try
                {
                    Assert.True(OleInitialize(IntPtr.Zero) >= 0);
                    Marshal.ThrowExceptionForHR(OleGetClipboard(out var clipboard));
                    descriptors = ParseDescriptor(GetHGlobal(clipboard, VirtualFileDataObject.FileDescriptorFormat));
                    read = ReadAll(GetContents(clipboard, 2));
                }
                catch (Exception ex)
                {
                    readerError = ex;
                }
            });
            reader.SetApartmentState(ApartmentState.STA);
            reader.Start();
            Assert.True(reader.Join(TimeSpan.FromSeconds(10)));
        }
        finally
        {
            done.Set();
            owner.Join(TimeSpan.FromSeconds(5));
        }

        Assert.Null(readerError);
        Assert.Equal(Entries.Select(e => e.RelativePath), descriptors!.Select(d => d.Name));
        Assert.Equal(content, read);
    }

    private sealed record Descriptor(uint Flags, uint Attributes, DateTime LastWriteUtc, long Size, string Name);

    private static List<Descriptor> ParseDescriptor(byte[] data)
    {
        var count = BitConverter.ToInt32(data, 0);
        var result = new List<Descriptor>();
        for (var i = 0; i < count; i++)
        {
            var d = data.AsSpan(4 + (i * 592), 592);
            var name = System.Text.Encoding.Unicode.GetString(d[72..]).Split('\0')[0];
            var size = ((long)BitConverter.ToUInt32(d[64..68]) << 32) | BitConverter.ToUInt32(d[68..72]);
            result.Add(new Descriptor(BitConverter.ToUInt32(d[0..4]), BitConverter.ToUInt32(d[36..40]), DateTime.FromFileTimeUtc(BitConverter.ToInt64(d[56..64])), size, name));
        }

        return result;
    }

    private static byte[] GetHGlobal(ComIDataObject data, string format)
    {
        var request = Format(format, TYMED.TYMED_HGLOBAL, -1);
        data.GetData(ref request, out var medium);
        try
        {
            var size = (int)GlobalSize(medium.unionmember);
            var pointer = GlobalLock(medium.unionmember);
            try
            {
                var bytes = new byte[size];
                Marshal.Copy(pointer, bytes, 0, size);
                return bytes;
            }
            finally
            {
                GlobalUnlock(medium.unionmember);
            }
        }
        finally
        {
            ReleaseStgMedium(ref medium);
        }
    }

    private static ComIStream GetContents(ComIDataObject data, int index)
    {
        var request = Format(VirtualFileDataObject.FileContentsFormat, TYMED.TYMED_ISTREAM | TYMED.TYMED_HGLOBAL, index);
        data.GetData(ref request, out var medium);
        Assert.Equal(TYMED.TYMED_ISTREAM, medium.tymed);
        var stream = (ComIStream)Marshal.GetObjectForIUnknown(medium.unionmember);
        Marshal.Release(medium.unionmember);
        return stream;
    }

    private static byte[] ReadAll(ComIStream stream)
    {
        var result = new MemoryStream();
        var buffer = new byte[100_000];
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

    private static FORMATETC Format(string name, TYMED tymed, int lindex) => new()
    {
        cfFormat = (short)RegisterClipboardFormat(name),
        dwAspect = DVASPECT.DVASPECT_CONTENT,
        lindex = lindex,
        tymed = tymed,
    };

    [DllImport("ole32.dll")]
    private static extern int OleInitialize(IntPtr reserved);

    [DllImport("ole32.dll")]
    private static extern int OleGetClipboard(out ComIDataObject dataObject);

    [DllImport("ole32.dll")]
    private static extern void ReleaseStgMedium(ref STGMEDIUM medium);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern uint RegisterClipboardFormat(string format);

    [DllImport("kernel32.dll")]
    private static extern UIntPtr GlobalSize(IntPtr handle);

    [DllImport("kernel32.dll")]
    private static extern IntPtr GlobalLock(IntPtr handle);

    [DllImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GlobalUnlock(IntPtr handle);
}
