using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace AntigravityQuotaTray.Services;

/// <summary>
/// Launches console subprocesses on a dedicated hidden Windows desktop and inside a
/// kill-on-close Job Object so neither the process nor any of its descendants
/// (e.g. conhost.exe, cmd.exe, uvx.exe, node.exe, python.exe) can ever flash a
/// visible console window on the user's desktop or linger after completion.
/// </summary>
public sealed class HiddenProcessRunner : IDisposable
{
    private const uint GenericAll = 0x10000000;
    private const uint GenericRead = 0x80000000;
    private const uint FileShareRead = 0x00000001;
    private const uint FileShareWrite = 0x00000002;
    private const uint OpenExisting = 3;

    private const uint CreateNoWindow = 0x08000000;
    private const uint CreateSuspended = 0x00000004;
    private const int StartfUseShowWindow = 0x00000001;
    private const int StartfUseStdHandles = 0x00000100;
    private const short SwHide = 0;
    private const uint HandleFlagInherit = 0x00000001;
    private const uint JobObjectLimitKillOnJobClose = 0x00002000;
    private const int JobObjectExtendedLimitInformationClass = 9;

    private readonly object _desktopLock = new();
    private readonly object _spawnLock = new();
    private IntPtr _hiddenDesktop;
    private string? _desktopName;
    private bool _desktopInitAttempted;
    private bool _disposed;

    public bool UsesHiddenDesktop
    {
        get
        {
            EnsureHiddenDesktop();
            return _hiddenDesktop != IntPtr.Zero;
        }
    }

    public async Task<(int ExitCode, string Stdout, string Stderr)> RunAsync(
        string fileName,
        string arguments,
        string? workingDirectory,
        TimeSpan timeout,
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        var workDir = string.IsNullOrWhiteSpace(workingDirectory)
            ? Path.GetTempPath()
            : workingDirectory;

        EnsureHiddenDesktop();
        if (_hiddenDesktop == IntPtr.Zero || string.IsNullOrEmpty(_desktopName))
        {
            return await RunFallbackAsync(fileName, arguments, workDir, timeout, cancellationToken)
                .ConfigureAwait(false);
        }

        SafeFileHandle? outRead = null;
        SafeFileHandle? errRead = null;
        SafeProcessHandle? processHandle = null;
        SafeJobHandle? jobHandle = null;

        try
        {
            PROCESS_INFORMATION pi;
            lock (_spawnLock)
            {
                var sa = new SECURITY_ATTRIBUTES
                {
                    nLength = Marshal.SizeOf<SECURITY_ATTRIBUTES>(),
                    bInheritHandle = true
                };

                if (!CreatePipe(out outRead, out var outWrite, ref sa, 0))
                {
                    throw new Win32Exception(Marshal.GetLastWin32Error(), "CreatePipe(stdout) failed.");
                }

                if (!CreatePipe(out errRead, out var errWrite, ref sa, 0))
                {
                    outWrite.Dispose();
                    throw new Win32Exception(Marshal.GetLastWin32Error(), "CreatePipe(stderr) failed.");
                }

                SetHandleInformation(outRead, HandleFlagInherit, 0);
                SetHandleInformation(errRead, HandleFlagInherit, 0);

                using var nulIn = CreateFileW(
                    "NUL",
                    GenericRead,
                    FileShareRead | FileShareWrite,
                    ref sa,
                    OpenExisting,
                    0,
                    IntPtr.Zero);

                using (outWrite)
                using (errWrite)
                {
                    var si = new STARTUPINFO
                    {
                        cb = Marshal.SizeOf<STARTUPINFO>(),
                        lpDesktop = _desktopName,
                        dwFlags = StartfUseShowWindow | StartfUseStdHandles,
                        wShowWindow = SwHide,
                        hStdInput = nulIn is { IsInvalid: false } ? nulIn.DangerousGetHandle() : IntPtr.Zero,
                        hStdOutput = outWrite.DangerousGetHandle(),
                        hStdError = errWrite.DangerousGetHandle()
                    };

                    var cmdLine = new StringBuilder($"\"{fileName}\" {arguments}");
                    var created = CreateProcessW(
                        fileName,
                        cmdLine,
                        IntPtr.Zero,
                        IntPtr.Zero,
                        bInheritHandles: true,
                        CreateNoWindow | CreateSuspended,
                        IntPtr.Zero,
                        workDir,
                        ref si,
                        out pi);

                    if (!created)
                    {
                        throw new Win32Exception(Marshal.GetLastWin32Error(), $"CreateProcessW failed for '{fileName}'.");
                    }
                }
            }

            processHandle = new SafeProcessHandle(pi.hProcess, ownsHandle: true);
            try
            {
                jobHandle = CreateKillOnCloseJob();
                if (jobHandle is { IsInvalid: false })
                {
                    if (!AssignProcessToJobObject(jobHandle.DangerousGetHandle(), processHandle.DangerousGetHandle()))
                    {
                        jobHandle.Dispose();
                        jobHandle = null;
                    }
                }

                if (ResumeThread(pi.hThread) == uint.MaxValue)
                {
                    throw new Win32Exception(Marshal.GetLastWin32Error(), "ResumeThread failed.");
                }
            }
            finally
            {
                CloseHandle(pi.hThread);
            }

            var stdoutTask = ReadPipeToEndAsync(outRead);
            var stderrTask = ReadPipeToEndAsync(errRead);

            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeoutCts.CancelAfter(timeout);

            try
            {
                await WaitForProcessExitAsync(processHandle, timeoutCts.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                TerminateTree(jobHandle, processHandle);
                jobHandle = null;
                _ = stdoutTask.ContinueWith(_ => { }, TaskContinuationOptions.OnlyOnFaulted);
                _ = stderrTask.ContinueWith(_ => { }, TaskContinuationOptions.OnlyOnFaulted);
                throw;
            }

            if (!GetExitCodeProcess(processHandle.DangerousGetHandle(), out var exitCode))
            {
                exitCode = 1;
            }

            // Close the Job Object once the root process has exited so any lingering
            // child processes (e.g. MCP servers) are terminated immediately and cannot
            // keep the stdout/stderr pipes open.
            jobHandle?.Dispose();
            jobHandle = null;

            await Task.WhenAll(stdoutTask, stderrTask).ConfigureAwait(false);
            return (unchecked((int)exitCode), await stdoutTask.ConfigureAwait(false), await stderrTask.ConfigureAwait(false));
        }
        finally
        {
            jobHandle?.Dispose();
            processHandle?.Dispose();
            outRead?.Dispose();
            errRead?.Dispose();
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        lock (_desktopLock)
        {
            if (_hiddenDesktop != IntPtr.Zero)
            {
                CloseDesktop(_hiddenDesktop);
                _hiddenDesktop = IntPtr.Zero;
            }
        }
    }

    private void EnsureHiddenDesktop()
    {
        if (_desktopInitAttempted)
        {
            return;
        }

        lock (_desktopLock)
        {
            if (_desktopInitAttempted || _disposed)
            {
                return;
            }

            _desktopInitAttempted = true;
            var candidateName = $"AntigravityQuotaTray_Hidden_{Environment.ProcessId}_{Guid.NewGuid():N}"[..48];
            var handle = CreateDesktopW(candidateName, IntPtr.Zero, IntPtr.Zero, 0, GenericAll, IntPtr.Zero);
            if (handle != IntPtr.Zero)
            {
                _hiddenDesktop = handle;
                _desktopName = candidateName;
            }
        }
    }

    private static Task<string> ReadPipeToEndAsync(SafeFileHandle readHandle)
    {
        return Task.Run(() =>
        {
            try
            {
                using var fs = new FileStream(readHandle, FileAccess.Read, bufferSize: 4096, isAsync: false);
                using var sr = new StreamReader(fs, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
                return sr.ReadToEnd();
            }
            catch (ObjectDisposedException)
            {
                return string.Empty;
            }
            catch (IOException)
            {
                return string.Empty;
            }
        });
    }

    private static Task WaitForProcessExitAsync(SafeProcessHandle processHandle, CancellationToken cancellationToken)
    {
        var tcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var waitHandle = new ProcessWaitHandle(processHandle);

        RegisteredWaitHandle? rwh = null;
        CancellationTokenRegistration ctr = default;

        rwh = ThreadPool.RegisterWaitForSingleObject(
            waitHandle,
            (_, _) =>
            {
                ctr.Dispose();
                rwh?.Unregister(null);
                waitHandle.Dispose();
                tcs.TrySetResult(true);
            },
            state: null,
            millisecondsTimeOutInterval: -1,
            executeOnlyOnce: true);

        ctr = cancellationToken.Register(() =>
        {
            rwh?.Unregister(null);
            waitHandle.Dispose();
            tcs.TrySetCanceled(cancellationToken);
        });

        return tcs.Task;
    }

    private static void TerminateTree(SafeJobHandle? jobHandle, SafeProcessHandle processHandle)
    {
        try
        {
            jobHandle?.Dispose();
        }
        catch
        {
            // Ignore cleanup errors
        }

        try
        {
            if (!processHandle.IsInvalid && !processHandle.IsClosed)
            {
                TerminateProcess(processHandle.DangerousGetHandle(), 1);
            }
        }
        catch
        {
            // Ignore cleanup errors
        }
    }

    private static SafeJobHandle? CreateKillOnCloseJob()
    {
        var raw = CreateJobObjectW(IntPtr.Zero, null);
        if (raw == IntPtr.Zero)
        {
            return null;
        }

        var job = new SafeJobHandle(raw);
        var info = new JOBOBJECT_EXTENDED_LIMIT_INFORMATION
        {
            BasicLimitInformation = new JOBOBJECT_BASIC_LIMIT_INFORMATION
            {
                LimitFlags = JobObjectLimitKillOnJobClose
            }
        };

        var size = Marshal.SizeOf<JOBOBJECT_EXTENDED_LIMIT_INFORMATION>();
        var ptr = Marshal.AllocHGlobal(size);
        try
        {
            Marshal.StructureToPtr(info, ptr, fDeleteOld: false);
            if (!SetInformationJobObject(job.DangerousGetHandle(), JobObjectExtendedLimitInformationClass, ptr, (uint)size))
            {
                job.Dispose();
                return null;
            }
        }
        finally
        {
            Marshal.FreeHGlobal(ptr);
        }

        return job;
    }

    private static async Task<(int ExitCode, string Stdout, string Stderr)> RunFallbackAsync(
        string fileName,
        string arguments,
        string workingDirectory,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        var psi = new ProcessStartInfo
        {
            FileName = fileName,
            Arguments = arguments,
            WorkingDirectory = workingDirectory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8
        };

        using var process = new Process { StartInfo = psi };
        process.Start();

        var stdoutTask = process.StandardOutput.ReadToEndAsync(cancellationToken);
        var stderrTask = process.StandardError.ReadToEndAsync(cancellationToken);

        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCts.CancelAfter(timeout);

        try
        {
            await process.WaitForExitAsync(timeoutCts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            try
            {
                if (!process.HasExited)
                {
                    process.Kill(entireProcessTree: true);
                }
            }
            catch
            {
                // Ignore kill errors
            }

            throw;
        }

        var stdout = await stdoutTask.ConfigureAwait(false);
        var stderr = await stderrTask.ConfigureAwait(false);
        return (process.ExitCode, stdout, stderr);
    }

    private sealed class ProcessWaitHandle : WaitHandle
    {
        public ProcessWaitHandle(SafeProcessHandle processHandle)
        {
            SafeWaitHandle = new SafeWaitHandle(processHandle.DangerousGetHandle(), ownsHandle: false);
        }
    }

    private sealed class SafeJobHandle : SafeHandleZeroOrMinusOneIsInvalid
    {
        public SafeJobHandle(IntPtr handle) : base(ownsHandle: true)
        {
            SetHandle(handle);
        }

        protected override bool ReleaseHandle() => CloseHandle(handle);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct SECURITY_ATTRIBUTES
    {
        public int nLength;
        public IntPtr lpSecurityDescriptor;
        [MarshalAs(UnmanagedType.Bool)]
        public bool bInheritHandle;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct STARTUPINFO
    {
        public int cb;
        public string? lpReserved;
        public string? lpDesktop;
        public string? lpTitle;
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
    private struct PROCESS_INFORMATION
    {
        public IntPtr hProcess;
        public IntPtr hThread;
        public int dwProcessId;
        public int dwThreadId;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct JOBOBJECT_BASIC_LIMIT_INFORMATION
    {
        public long PerProcessUserTimeLimit;
        public long PerJobUserTimeLimit;
        public uint LimitFlags;
        public UIntPtr MinimumWorkingSetSize;
        public UIntPtr MaximumWorkingSetSize;
        public uint ActiveProcessLimit;
        public UIntPtr Affinity;
        public uint PriorityClass;
        public uint SchedulingClass;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct IO_COUNTERS
    {
        public ulong ReadOperationCount;
        public ulong WriteOperationCount;
        public ulong OtherOperationCount;
        public ulong ReadTransferCount;
        public ulong WriteTransferCount;
        public ulong OtherTransferCount;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct JOBOBJECT_EXTENDED_LIMIT_INFORMATION
    {
        public JOBOBJECT_BASIC_LIMIT_INFORMATION BasicLimitInformation;
        public IO_COUNTERS IoInfo;
        public UIntPtr ProcessMemoryLimit;
        public UIntPtr JobMemoryLimit;
        public UIntPtr PeakProcessMemoryUsed;
        public UIntPtr PeakJobMemoryUsed;
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr CreateDesktopW(
        string lpszDesktop,
        IntPtr lpszDevice,
        IntPtr pDevmode,
        int dwFlags,
        uint dwDesiredAccess,
        IntPtr lpsa);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseDesktop(IntPtr hDesktop);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CreatePipe(
        out SafeFileHandle hReadPipe,
        out SafeFileHandle hWritePipe,
        ref SECURITY_ATTRIBUTES lpPipeAttributes,
        int nSize);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetHandleInformation(
        SafeFileHandle hObject,
        uint dwMask,
        uint dwFlags);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle CreateFileW(
        string lpFileName,
        uint dwDesiredAccess,
        uint dwShareMode,
        ref SECURITY_ATTRIBUTES lpSecurityAttributes,
        uint dwCreationDisposition,
        uint dwFlagsAndAttributes,
        IntPtr hTemplateFile);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CreateProcessW(
        string? lpApplicationName,
        StringBuilder lpCommandLine,
        IntPtr lpProcessAttributes,
        IntPtr lpThreadAttributes,
        [MarshalAs(UnmanagedType.Bool)] bool bInheritHandles,
        uint dwCreationFlags,
        IntPtr lpEnvironment,
        string? lpCurrentDirectory,
        ref STARTUPINFO lpStartupInfo,
        out PROCESS_INFORMATION lpProcessInformation);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern uint ResumeThread(IntPtr hThread);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetExitCodeProcess(IntPtr hProcess, out uint lpExitCode);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool TerminateProcess(IntPtr hProcess, uint uExitCode);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseHandle(IntPtr hObject);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr CreateJobObjectW(IntPtr lpJobAttributes, string? lpName);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetInformationJobObject(
        IntPtr hJob,
        int jobObjectInformationClass,
        IntPtr lpJobObjectInformation,
        uint cbJobObjectInformationLength);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool AssignProcessToJobObject(IntPtr hJob, IntPtr hProcess);
}
