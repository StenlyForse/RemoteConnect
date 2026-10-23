using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;
using ComIStream = System.Runtime.InteropServices.ComTypes.IStream;
using STATSTG = System.Runtime.InteropServices.ComTypes.STATSTG;

namespace RemoteConnect.Windows.Clipboard;

/// <summary>
/// COM <c>IStream</c> только для последовательного чтения поверх .NET <see cref="Stream"/>.
/// Отдаётся Проводнику как содержимое виртуального файла. Чтение может блокироваться (данные идут по сети) —
/// вызовы приходят на потоки RPC, а не на UI-поток приложения.
/// Поток .NET закрывается после чтения до конца, при ошибке или когда Проводник освобождает объект.
/// </summary>
[ComVisible(true)]
[ClassInterface(ClassInterfaceType.None)]
public sealed class ComStreamAdapter(Stream source, long size, string name, DateTime lastWriteUtc) : ComIStream
{
    private const int STG_E_INVALIDFUNCTION = unchecked((int)0x80030001);
    private const int STG_E_ACCESSDENIED = unchecked((int)0x80030005);
    private const int STG_E_READFAULT = unchecked((int)0x8003001E);
    private const int STATFLAG_NONAME = 1;
    private const int STGTY_STREAM = 2;
    private const int STREAM_SEEK_SET = 0;
    private const int STREAM_SEEK_CUR = 1;
    private const int STREAM_SEEK_END = 2;

    private long _position;
    private bool _closed;

    public void Read(byte[] pv, int cb, IntPtr pcbRead)
    {
        var total = 0;
        if (!_closed)
        {
            try
            {
                while (total < cb)
                {
                    var read = source.Read(pv, total, cb - total);
                    if (read == 0)
                    {
                        Close();
                        break;
                    }

                    total += read;
                }
            }
            catch (Exception ex) when (ex is IOException or TimeoutException or ObjectDisposedException or OperationCanceledException or UnauthorizedAccessException)
            {
                Close();
                throw new COMException(ex.Message, STG_E_READFAULT);
            }
        }

        _position += total;
        if (pcbRead != IntPtr.Zero)
        {
            Marshal.WriteInt32(pcbRead, total);
        }
    }

    public void Write(byte[] pv, int cb, IntPtr pcbWritten) => throw new COMException("Только чтение", STG_E_ACCESSDENIED);

    /// <summary>Разрешены только «узнать позицию» и переход в уже текущую позицию.</summary>
    public void Seek(long dlibMove, int dwOrigin, IntPtr plibNewPosition)
    {
        var target = dwOrigin switch
        {
            STREAM_SEEK_SET => dlibMove,
            STREAM_SEEK_CUR => _position + dlibMove,
            STREAM_SEEK_END => size + dlibMove,
            _ => -1,
        };

        if (target != _position)
        {
            throw new COMException("Поток только последовательный", STG_E_INVALIDFUNCTION);
        }

        if (plibNewPosition != IntPtr.Zero)
        {
            Marshal.WriteInt64(plibNewPosition, _position);
        }
    }

    public void Stat(out STATSTG pstatstg, int grfStatFlag)
    {
        var time = new FILETIME
        {
            dwLowDateTime = (int)(lastWriteUtc.ToFileTimeUtc() & 0xFFFFFFFF),
            dwHighDateTime = (int)(lastWriteUtc.ToFileTimeUtc() >> 32),
        };
        pstatstg = new STATSTG
        {
            pwcsName = (grfStatFlag & STATFLAG_NONAME) != 0 ? null! : name,
            type = STGTY_STREAM,
            cbSize = size,
            mtime = time,
            ctime = time,
            atime = time,
        };
    }

    public void CopyTo(ComIStream pstm, long cb, IntPtr pcbRead, IntPtr pcbWritten)
    {
        var buffer = new byte[64 * 1024];
        long copied = 0;
        var readCount = Marshal.AllocCoTaskMem(4);
        try
        {
            while (copied < cb)
            {
                Read(buffer, (int)Math.Min(buffer.Length, cb - copied), readCount);
                var read = Marshal.ReadInt32(readCount);
                if (read == 0)
                {
                    break;
                }

                pstm.Write(buffer, read, IntPtr.Zero);
                copied += read;
            }
        }
        finally
        {
            Marshal.FreeCoTaskMem(readCount);
        }

        if (pcbRead != IntPtr.Zero)
        {
            Marshal.WriteInt64(pcbRead, copied);
        }

        if (pcbWritten != IntPtr.Zero)
        {
            Marshal.WriteInt64(pcbWritten, copied);
        }
    }

    public void SetSize(long libNewSize) => throw new COMException("Только чтение", STG_E_ACCESSDENIED);

    public void Commit(int grfCommitFlags)
    {
    }

    public void Revert() => throw new COMException("Не поддерживается", STG_E_INVALIDFUNCTION);

    public void LockRegion(long libOffset, long cb, int dwLockType) => throw new COMException("Не поддерживается", STG_E_INVALIDFUNCTION);

    public void UnlockRegion(long libOffset, long cb, int dwLockType) => throw new COMException("Не поддерживается", STG_E_INVALIDFUNCTION);

    public void Clone(out ComIStream ppstm) => throw new COMException("Не поддерживается", STG_E_INVALIDFUNCTION);

    /// <summary>Закрывает поток-источник (например, чтобы отменить передачу по сети).</summary>
    public void Close()
    {
        if (!_closed)
        {
            _closed = true;
            source.Dispose();
        }
    }

    ~ComStreamAdapter()
    {
        // Проводник отпустил поток, не дочитав (отмена): закрытие источника отменит передачу.
        Close();
    }
}
