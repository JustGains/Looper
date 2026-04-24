using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace JustCode.Services;

/// <summary>
/// Thin managed wrapper around Windows' ConPTY (Pseudo Console) API. Spawns a
/// shell process attached to a pseudo-console so everything that expects a
/// real TTY — ANSI colors, progress bars, Ctrl+C, password prompts, PSReadLine
/// line editing, Tab completion — works exactly as it does in Windows Terminal.
/// </summary>
public sealed class ConPtyTerminal : IDisposable
{
    public event EventHandler<ReadOnlyMemory<byte>>? Output;
    public event EventHandler? Exited;

    private SafeFileHandle? _hPC;
    private SafeFileHandle? _ptyIn;    // our side: write bytes TO the shell
    private SafeFileHandle? _ptyOut;   // our side: read bytes FROM the shell
    private FileStream? _writer;
    private FileStream? _reader;
    private Thread? _readerThread;
    private Process? _process;
    private IntPtr _attrList = IntPtr.Zero;
    private int _cols;
    private int _rows;
    private volatile bool _disposed;
    private readonly object _ioLock = new();

    public bool IsRunning => _process is { HasExited: false };
    public int ExitCode => _process?.HasExited == true ? _process.ExitCode : 0;

    public void Start(string workingDirectory, string exe, string args, int cols, int rows)
    {
        if (_hPC != null) throw new InvalidOperationException("Terminal already started.");
        if (cols <= 0) cols = 80;
        if (rows <= 0) rows = 24;
        _cols = cols;
        _rows = rows;

        // Build the two anonymous pipes that connect us to the PTY.
        //   shellOutPipeRead  ← shellOutPipeWrite  (shell's stdout → us)
        //   shellInPipeRead   ← shellInPipeWrite   (us → shell's stdin)
        if (!CreatePipe(out var shellOutRead, out var shellOutWrite, IntPtr.Zero, 0))
            throw new Win32Exception(Marshal.GetLastWin32Error(), "CreatePipe (out) failed");
        if (!CreatePipe(out var shellInRead, out var shellInWrite, IntPtr.Zero, 0))
        {
            shellOutRead.Dispose(); shellOutWrite.Dispose();
            throw new Win32Exception(Marshal.GetLastWin32Error(), "CreatePipe (in) failed");
        }

        var size = new COORD { X = (short)cols, Y = (short)rows };
        var hr = CreatePseudoConsole(size, shellInRead, shellOutWrite, 0, out var hPC);
        // Once the pty owns the inner handles we can drop our refs.
        shellInRead.Dispose();
        shellOutWrite.Dispose();
        if (hr != 0)
        {
            shellOutRead.Dispose(); shellInWrite.Dispose();
            throw new Win32Exception(hr, "CreatePseudoConsole failed");
        }

        _hPC = hPC;
        _ptyIn = shellInWrite;
        _ptyOut = shellOutRead;

        try
        {
            StartChild(workingDirectory, exe, args);
        }
        catch
        {
            DisposeCore();
            throw;
        }

        _writer = new FileStream(_ptyIn, FileAccess.Write, bufferSize: 4096, isAsync: false);
        _reader = new FileStream(_ptyOut, FileAccess.Read, bufferSize: 4096, isAsync: false);

        _readerThread = new Thread(ReaderLoop)
        {
            IsBackground = true,
            Name = "ConPtyTerminal.Reader",
        };
        _readerThread.Start();
    }

    public void Write(ReadOnlySpan<byte> bytes)
    {
        if (_disposed || bytes.IsEmpty) return;
        var w = _writer;
        if (w == null) return;
        try
        {
            lock (_ioLock)
            {
                w.Write(bytes);
                w.Flush();
            }
        }
        catch (IOException) { /* pipe closed — child exited */ }
        catch (ObjectDisposedException) { }
    }

    public void Resize(int cols, int rows)
    {
        if (_disposed || _hPC == null || _hPC.IsInvalid) return;
        if (cols <= 0 || rows <= 0) return;
        if (cols == _cols && rows == _rows) return;
        _cols = cols;
        _rows = rows;
        var size = new COORD { X = (short)cols, Y = (short)rows };
        ResizePseudoConsole(_hPC, size);
    }

    private void StartChild(string workingDirectory, string exe, string args)
    {
        // EXTENDED_STARTUPINFO_PRESENT startup is required to attach a
        // pseudo-console to the new process via UpdateProcThreadAttribute.
        var si = new STARTUPINFOEX();
        si.StartupInfo.cb = Marshal.SizeOf<STARTUPINFOEX>();

        var attrListSize = IntPtr.Zero;
        InitializeProcThreadAttributeList(IntPtr.Zero, 1, 0, ref attrListSize);
        _attrList = Marshal.AllocHGlobal(attrListSize);
        if (!InitializeProcThreadAttributeList(_attrList, 1, 0, ref attrListSize))
            throw new Win32Exception(Marshal.GetLastWin32Error(), "InitializeProcThreadAttributeList failed");

        if (!UpdateProcThreadAttribute(
                _attrList, 0,
                (IntPtr)PROC_THREAD_ATTRIBUTE_PSEUDOCONSOLE,
                _hPC!.DangerousGetHandle(),
                (IntPtr)IntPtr.Size,
                IntPtr.Zero, IntPtr.Zero))
        {
            throw new Win32Exception(Marshal.GetLastWin32Error(), "UpdateProcThreadAttribute failed");
        }

        si.lpAttributeList = _attrList;

        var cmd = string.IsNullOrEmpty(args) ? exe : $"\"{exe}\" {args}";
        var cmdBuffer = new char[cmd.Length + 1];
        cmd.CopyTo(0, cmdBuffer, 0, cmd.Length);

        var pi = new PROCESS_INFORMATION();

        if (!CreateProcess(
                lpApplicationName: null,
                lpCommandLine: cmdBuffer,
                lpProcessAttributes: IntPtr.Zero,
                lpThreadAttributes: IntPtr.Zero,
                bInheritHandles: false,
                dwCreationFlags: EXTENDED_STARTUPINFO_PRESENT | CREATE_UNICODE_ENVIRONMENT,
                lpEnvironment: IntPtr.Zero,
                lpCurrentDirectory: string.IsNullOrEmpty(workingDirectory) ? null : workingDirectory,
                lpStartupInfo: ref si,
                lpProcessInformation: out pi))
        {
            throw new Win32Exception(Marshal.GetLastWin32Error(), $"CreateProcess '{exe}' failed");
        }

        // We don't need the thread handle. Keep the process handle wrapped in a
        // .NET Process so we can observe exit and get the exit code.
        CloseHandle(pi.hThread);
        _process = Process.GetProcessById((int)pi.dwProcessId);
        _process.EnableRaisingEvents = true;
        _process.Exited += (_, _) =>
        {
            try { Exited?.Invoke(this, EventArgs.Empty); } catch { }
        };
        CloseHandle(pi.hProcess);
    }

    private void ReaderLoop()
    {
        var reader = _reader;
        if (reader == null) return;
        var buffer = new byte[4096];
        try
        {
            while (!_disposed)
            {
                int read;
                try { read = reader.Read(buffer, 0, buffer.Length); }
                catch { break; }
                if (read <= 0) break;
                var copy = new byte[read];
                Buffer.BlockCopy(buffer, 0, copy, 0, read);
                try { Output?.Invoke(this, copy); } catch { }
            }
        }
        finally
        {
            try { Exited?.Invoke(this, EventArgs.Empty); } catch { }
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        DisposeCore();
    }

    private void DisposeCore()
    {
        try { _writer?.Dispose(); } catch { }
        _writer = null;

        // Closing the pseudo-console severs the child's console and unblocks
        // our reader. Do this before disposing _reader so Read() returns 0.
        try { _hPC?.Dispose(); } catch { }
        _hPC = null;

        try { _reader?.Dispose(); } catch { }
        _reader = null;

        try { _ptyIn?.Dispose(); } catch { }
        _ptyIn = null;
        try { _ptyOut?.Dispose(); } catch { }
        _ptyOut = null;

        if (_attrList != IntPtr.Zero)
        {
            try { DeleteProcThreadAttributeList(_attrList); } catch { }
            try { Marshal.FreeHGlobal(_attrList); } catch { }
            _attrList = IntPtr.Zero;
        }

        if (_process != null)
        {
            try
            {
                if (!_process.HasExited)
                {
                    _process.Kill(entireProcessTree: true);
                }
            }
            catch { }
            try { _process.Dispose(); } catch { }
            _process = null;
        }

        var t = _readerThread;
        _readerThread = null;
        try { t?.Join(500); } catch { }
    }

    // ---- Win32 interop ----

    private const uint EXTENDED_STARTUPINFO_PRESENT = 0x00080000;
    private const uint CREATE_UNICODE_ENVIRONMENT = 0x00000400;
    private const int PROC_THREAD_ATTRIBUTE_PSEUDOCONSOLE = 0x00020016;

    [StructLayout(LayoutKind.Sequential)]
    private struct COORD
    {
        public short X;
        public short Y;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct STARTUPINFO
    {
        public int cb;
        public IntPtr lpReserved;
        public IntPtr lpDesktop;
        public IntPtr lpTitle;
        public int dwX;
        public int dwY;
        public int dwXSize;
        public int dwYSize;
        public int dwXCountChars;
        public int dwYCountChars;
        public int dwFillAttribute;
        public int dwFlags;
        public short wShowWindow;
        public short cbReserved2;
        public IntPtr lpReserved2;
        public IntPtr hStdInput;
        public IntPtr hStdOutput;
        public IntPtr hStdError;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct STARTUPINFOEX
    {
        public STARTUPINFO StartupInfo;
        public IntPtr lpAttributeList;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct PROCESS_INFORMATION
    {
        public IntPtr hProcess;
        public IntPtr hThread;
        public uint dwProcessId;
        public uint dwThreadId;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CreatePipe(
        out SafeFileHandle hReadPipe,
        out SafeFileHandle hWritePipe,
        IntPtr lpPipeAttributes,
        int nSize);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern int CreatePseudoConsole(
        COORD size,
        SafeFileHandle hInput,
        SafeFileHandle hOutput,
        uint dwFlags,
        out SafeFileHandle phPC);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern int ResizePseudoConsole(SafeFileHandle hPC, COORD size);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool InitializeProcThreadAttributeList(
        IntPtr lpAttributeList,
        int dwAttributeCount,
        int dwFlags,
        ref IntPtr lpSize);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool UpdateProcThreadAttribute(
        IntPtr lpAttributeList,
        uint dwFlags,
        IntPtr Attribute,
        IntPtr lpValue,
        IntPtr cbSize,
        IntPtr lpPreviousValue,
        IntPtr lpReturnSize);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool DeleteProcThreadAttributeList(IntPtr lpAttributeList);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool CreateProcess(
        string? lpApplicationName,
        char[] lpCommandLine,
        IntPtr lpProcessAttributes,
        IntPtr lpThreadAttributes,
        bool bInheritHandles,
        uint dwCreationFlags,
        IntPtr lpEnvironment,
        string? lpCurrentDirectory,
        ref STARTUPINFOEX lpStartupInfo,
        out PROCESS_INFORMATION lpProcessInformation);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CloseHandle(IntPtr hObject);
}
