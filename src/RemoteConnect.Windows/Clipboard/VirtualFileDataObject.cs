using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;
using RemoteConnect.Windows.Interop;
using ComIDataObject = System.Runtime.InteropServices.ComTypes.IDataObject;
using ComIStream = System.Runtime.InteropServices.ComTypes.IStream;

namespace RemoteConnect.Windows.Clipboard;

/// <summary>Элемент набора: относительный путь (через <c>\</c>), папка или файл, размер, дата изменения.</summary>
public sealed record VirtualFileEntry(string RelativePath, bool IsDirectory, long Size, DateTime LastWriteUtc);

/// <summary>
/// «Виртуальные файлы» для буфера обмена Windows (spec-file-clipboard FR-FCB-6..10):
/// Проводник получает список файлов (<c>FileGroupDescriptorW</c>) и при вставке запрашивает
/// содержимое каждого файла потоком (<c>FileContents</c>). Содержимое отдаёт <c>openFile</c>
/// в момент запроса — так передача начинается только при вставке.
/// Поддерживает асинхронную вставку (<see cref="IDataObjectAsyncCapability"/>), чтобы Проводник
/// копировал в фоне со своим окном прогресса.
/// </summary>
[ComVisible(true)]
[ClassInterface(ClassInterfaceType.None)]
public sealed class VirtualFileDataObject : ComIDataObject, IDataObjectAsyncCapability
{
    public const string FileDescriptorFormat = "FileGroupDescriptorW";
    public const string FileContentsFormat = "FileContents";
    public const string PreferredDropEffectFormat = "Preferred DropEffect";

    private const int S_OK = 0;
    private const int S_FALSE = 1;
    private const int DV_E_FORMATETC = unchecked((int)0x80040064);
    private const int DV_E_LINDEX = unchecked((int)0x80040068);
    private const int DV_E_TYMED = unchecked((int)0x80040069);
    private const int E_NOTIMPL = unchecked((int)0x80004001);
    private const int OLE_E_ADVISENOTSUPPORTED = unchecked((int)0x80040003);
    private const int DROPEFFECT_COPY = 1;

    private const uint FD_ATTRIBUTES = 0x4;
    private const uint FD_WRITESTIME = 0x20;
    private const uint FD_FILESIZE = 0x40;
    private const uint FD_PROGRESSUI = 0x4000;
    private const uint FD_UNICODE = 0x80000000;
    private const uint FILE_ATTRIBUTE_DIRECTORY = 0x10;
    private const uint FILE_ATTRIBUTE_NORMAL = 0x80;
    private const int FileDescriptorSize = 592;
    private const int FileNameOffset = 72;
    private const int MaxPathChars = 260;

    private static readonly short CfDescriptor = (short)NativeMethods.RegisterClipboardFormat(FileDescriptorFormat);
    private static readonly short CfContents = (short)NativeMethods.RegisterClipboardFormat(FileContentsFormat);
    private static readonly short CfDropEffect = (short)NativeMethods.RegisterClipboardFormat(PreferredDropEffectFormat);

    private readonly IReadOnlyList<VirtualFileEntry> _entries;
    private readonly Func<int, Stream> _openFile;
    private bool _asyncMode = true;
    private bool _inOperation;

    public VirtualFileDataObject(IReadOnlyList<VirtualFileEntry> entries, Func<int, Stream> openFile)
    {
        if (entries.Any(e => e.RelativePath.Length >= MaxPathChars))
        {
            throw new ArgumentException("Путь длиннее 259 символов", nameof(entries));
        }

        _entries = entries;
        _openFile = openFile;
    }

    /// <summary>Диагностика вызовов Проводника (для журнала).</summary>
    public static Action<string>? Trace { get; set; }

    /// <summary>Асинхронная вставка закончилась (успешно, с ошибкой или отменой).</summary>
    public event Action<int>? OperationEnded;

    public IReadOnlyList<VirtualFileEntry> Entries => _entries;

    // ---------------- IDataObject ----------------

    public void GetData(ref FORMATETC format, out STGMEDIUM medium)
    {
        Trace?.Invoke($"GetData cf={FormatName(format.cfFormat)} lindex={format.lindex} tymed={format.tymed}");
        medium = default;
        if (format.cfFormat == CfDescriptor && (format.tymed & TYMED.TYMED_HGLOBAL) != 0)
        {
            medium = HGlobalMedium(BuildDescriptor());
            return;
        }

        if (format.cfFormat == CfDropEffect && (format.tymed & TYMED.TYMED_HGLOBAL) != 0)
        {
            medium = HGlobalMedium(BitConverter.GetBytes(DROPEFFECT_COPY));
            return;
        }

        if (format.cfFormat == CfContents && (format.tymed & TYMED.TYMED_ISTREAM) != 0)
        {
            var index = format.lindex;
            if (index < 0 || index >= _entries.Count || _entries[index].IsDirectory)
            {
                throw new COMException("Нет такого файла", DV_E_LINDEX);
            }

            var entry = _entries[index];
            var stream = new ComStreamAdapter(_openFile(index), entry.Size, Path.GetFileName(entry.RelativePath), entry.LastWriteUtc);
            medium = new STGMEDIUM
            {
                tymed = TYMED.TYMED_ISTREAM,
                unionmember = Marshal.GetComInterfaceForObject(stream, typeof(ComIStream)),
                pUnkForRelease = null,
            };
            return;
        }

        throw new COMException("Формат не поддерживается", DV_E_FORMATETC);
    }

    public void GetDataHere(ref FORMATETC format, ref STGMEDIUM medium) =>
        throw new COMException("Не поддерживается", E_NOTIMPL);

    public int QueryGetData(ref FORMATETC format)
    {
        Trace?.Invoke($"QueryGetData cf={FormatName(format.cfFormat)} tymed={format.tymed}");
        if (format.cfFormat == CfDescriptor || format.cfFormat == CfDropEffect)
        {
            return (format.tymed & TYMED.TYMED_HGLOBAL) != 0 ? S_OK : DV_E_TYMED;
        }

        if (format.cfFormat == CfContents)
        {
            return (format.tymed & TYMED.TYMED_ISTREAM) != 0 ? S_OK : DV_E_TYMED;
        }

        return DV_E_FORMATETC;
    }

    public int GetCanonicalFormatEtc(ref FORMATETC formatIn, out FORMATETC formatOut)
    {
        formatOut = formatIn;
        formatOut.ptd = IntPtr.Zero;
        return DATA_S_SAMEFORMATETC;
    }

    /// <summary>Проводник сообщает о результатах вставки («Performed DropEffect» и т.п.) — принимаем и игнорируем.</summary>
    public void SetData(ref FORMATETC formatIn, ref STGMEDIUM medium, bool release)
    {
        Trace?.Invoke($"SetData cf={FormatName(formatIn.cfFormat)}");
        if (release)
        {
            ReleaseStgMedium(ref medium);
        }
    }

    public IEnumFORMATETC EnumFormatEtc(DATADIR direction)
    {
        if (direction != DATADIR.DATADIR_GET)
        {
            throw new COMException("Не поддерживается", E_NOTIMPL);
        }

        return new FormatEnumerator(
        [
            Format(CfDescriptor, TYMED.TYMED_HGLOBAL, -1),
            Format(CfContents, TYMED.TYMED_ISTREAM, -1),
            Format(CfDropEffect, TYMED.TYMED_HGLOBAL, -1),
        ]);
    }

    public int DAdvise(ref FORMATETC pFormatetc, ADVF advf, IAdviseSink adviseSink, out int connection)
    {
        connection = 0;
        return OLE_E_ADVISENOTSUPPORTED;
    }

    public void DUnadvise(int connection) => throw new COMException("Не поддерживается", OLE_E_ADVISENOTSUPPORTED);

    public int EnumDAdvise(out IEnumSTATDATA? enumAdvise)
    {
        enumAdvise = null;
        return OLE_E_ADVISENOTSUPPORTED;
    }

    // ---------------- IDataObjectAsyncCapability ----------------

    public void SetAsyncMode(int doOpAsync)
    {
        Trace?.Invoke($"SetAsyncMode {doOpAsync}");
        _asyncMode = doOpAsync != 0;
    }

    public void GetAsyncMode(out int isOpAsync)
    {
        Trace?.Invoke("GetAsyncMode");
        isOpAsync = _asyncMode ? 1 : 0;
    }

    public void StartOperation(IntPtr bindContext)
    {
        Trace?.Invoke("StartOperation");
        _inOperation = true;
    }

    public void InOperation(out int inAsyncOp) => inAsyncOp = _inOperation ? 1 : 0;

    public void EndOperation(int result, IntPtr bindContext, uint effects)
    {
        Trace?.Invoke($"EndOperation hr=0x{result:X8} effects={effects}");
        _inOperation = false;
        OperationEnded?.Invoke(result);
    }

    // ---------------- helpers ----------------

    private const int DATA_S_SAMEFORMATETC = 0x00040130;

    private static string FormatName(short cf) =>
        cf == CfDescriptor ? FileDescriptorFormat
        : cf == CfContents ? FileContentsFormat
        : cf == CfDropEffect ? PreferredDropEffectFormat
        : $"#{(ushort)cf}";

    private byte[] BuildDescriptor()
    {
        var buffer = new byte[4 + (_entries.Count * FileDescriptorSize)];
        BitConverter.TryWriteBytes(buffer.AsSpan(0, 4), (uint)_entries.Count);
        for (var i = 0; i < _entries.Count; i++)
        {
            var entry = _entries[i];
            var d = buffer.AsSpan(4 + (i * FileDescriptorSize), FileDescriptorSize);
            BitConverter.TryWriteBytes(d[0..4], FD_ATTRIBUTES | FD_WRITESTIME | FD_FILESIZE | FD_PROGRESSUI | FD_UNICODE);
            BitConverter.TryWriteBytes(d[36..40], entry.IsDirectory ? FILE_ATTRIBUTE_DIRECTORY : FILE_ATTRIBUTE_NORMAL);
            BitConverter.TryWriteBytes(d[56..64], entry.LastWriteUtc.ToFileTimeUtc());
            BitConverter.TryWriteBytes(d[64..68], (uint)(entry.Size >> 32));
            BitConverter.TryWriteBytes(d[68..72], (uint)(entry.Size & 0xFFFFFFFF));
            System.Text.Encoding.Unicode.GetBytes(entry.RelativePath).CopyTo(d[FileNameOffset..]);
        }

        return buffer;
    }

    private static STGMEDIUM HGlobalMedium(byte[] data)
    {
        var handle = NativeMethods.GlobalAlloc(NativeMethods.GMEM_MOVEABLE, (UIntPtr)data.Length);
        if (handle == IntPtr.Zero)
        {
            throw new OutOfMemoryException();
        }

        var pointer = NativeMethods.GlobalLock(handle);
        Marshal.Copy(data, 0, pointer, data.Length);
        NativeMethods.GlobalUnlock(handle);
        return new STGMEDIUM { tymed = TYMED.TYMED_HGLOBAL, unionmember = handle, pUnkForRelease = null };
    }

    private static FORMATETC Format(short cf, TYMED tymed, int lindex) => new()
    {
        cfFormat = cf,
        dwAspect = DVASPECT.DVASPECT_CONTENT,
        lindex = lindex,
        ptd = IntPtr.Zero,
        tymed = tymed,
    };

    [DllImport("ole32.dll")]
    private static extern void ReleaseStgMedium(ref STGMEDIUM medium);

    [ComVisible(true)]
    [ClassInterface(ClassInterfaceType.None)]
    private sealed class FormatEnumerator(FORMATETC[] formats) : IEnumFORMATETC
    {
        private int _position;

        public int Next(int celt, FORMATETC[] rgelt, int[]? pceltFetched)
        {
            var fetched = 0;
            while (fetched < celt && _position < formats.Length)
            {
                rgelt[fetched++] = formats[_position++];
            }

            if (pceltFetched is { Length: > 0 })
            {
                pceltFetched[0] = fetched;
            }

            return fetched == celt ? S_OK : S_FALSE;
        }

        public int Skip(int celt)
        {
            _position = Math.Min(formats.Length, _position + celt);
            return _position < formats.Length ? S_OK : S_FALSE;
        }

        public int Reset()
        {
            _position = 0;
            return S_OK;
        }

        public void Clone(out IEnumFORMATETC newEnum) => newEnum = new FormatEnumerator(formats) { _position = _position };
    }
}

/// <summary>
/// Асинхронная вставка (IDataObjectAsyncCapability, он же IAsyncOperation): Проводник копирует в фоне
/// со своим окном прогресса и сообщает об окончании.
/// </summary>
[ComImport]
[Guid("3D8B0590-F691-11d2-8EA9-006097DF5BD4")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
public interface IDataObjectAsyncCapability
{
    void SetAsyncMode(int doOpAsync);

    void GetAsyncMode(out int isOpAsync);

    void StartOperation(IntPtr bindContext);

    void InOperation(out int inAsyncOp);

    void EndOperation(int result, IntPtr bindContext, uint effects);
}
