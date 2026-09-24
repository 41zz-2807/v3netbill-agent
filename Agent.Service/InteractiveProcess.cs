using System;
using System.ComponentModel;
using System.Runtime.InteropServices;

namespace V3Netbill.Agent.Service;

/// <summary>
/// Meluncurkan proses ke SESI INTERAKTIF (desktop user) dari service SYSTEM.
/// Penting: proses yang di-launch dari service lewat Process.Start berjalan di
/// session 0 (tak terlihat). Dengan CreateProcessAsUser memakai token user yang
/// login, overlay tampil di layar pengguna.
/// </summary>
internal static class InteractiveProcess
{
    public static bool Launch(string exePath, string workingDir)
    {
        uint sessionId = WTSGetActiveConsoleSessionId();
        if (sessionId == 0xFFFFFFFF) return false;

        if (!WTSQueryUserToken(sessionId, out IntPtr userToken) || userToken == IntPtr.Zero)
        {
            return false;
        }

        IntPtr primaryToken = IntPtr.Zero;
        if (!DuplicateTokenEx(userToken, TOKEN_ASSIGN_PRIMARY | TOKEN_DUPLICATE | TOKEN_QUERY, IntPtr.Zero,
                SECURITY_IMPERSONATION, TokenPrimary, out primaryToken) || primaryToken == IntPtr.Zero)
        {
            CloseHandle(userToken);
            return false;
        }

        try
        {
            var si = new STARTUPINFO { cb = Marshal.SizeOf<STARTUPINFO>() };
            var pi = new PROCESS_INFORMATION();
            string commandLine = "\"" + exePath + "\"";
            bool ok = CreateProcessAsUser(
                primaryToken,
                null,
                commandLine,
                IntPtr.Zero,
                IntPtr.Zero,
                false,
                CREATE_UNICODE_ENVIRONMENT | CREATE_NEW_CONSOLE,
                IntPtr.Zero,
                workingDir,
                ref si,
                out pi);

            if (!ok)
            {
                int error = Marshal.GetLastWin32Error();
                throw new Win32Exception(error, "CreateProcessAsUser gagal");
            }

            CloseHandle(pi.hProcess);
            CloseHandle(pi.hThread);
            return true;
        }
        finally
        {
            CloseHandle(primaryToken);
            CloseHandle(userToken);
        }
    }

    // ===== P/Invoke =====
    private const uint CREATE_NEW_CONSOLE = 0x10;
    private const uint CREATE_UNICODE_ENVIRONMENT = 0x400;
    private const uint TOKEN_ASSIGN_PRIMARY = 0x0001;
    private const uint TOKEN_DUPLICATE = 0x0002;
    private const uint TOKEN_QUERY = 0x0008;
    private const int SECURITY_IMPERSONATION = 2;
    private const int TokenPrimary = 1;

    [DllImport("kernel32.dll")]
    private static extern uint WTSGetActiveConsoleSessionId();

    [DllImport("wtsapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool WTSQueryUserToken(uint sessionId, out IntPtr phToken);

    [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DuplicateTokenEx(
        IntPtr hExistingToken,
        uint dwDesiredAccess,
        IntPtr lpTokenAttributes,
        int impersonationLevel,
        int tokenType,
        out IntPtr phNewToken);

    [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CreateProcessAsUser(
        IntPtr hToken,
        string? lpApplicationName,
        string lpCommandLine,
        IntPtr lpProcessAttributes,
        IntPtr lpThreadAttributes,
        bool bInheritHandles,
        uint dwCreationFlags,
        IntPtr lpEnvironment,
        string? lpCurrentDirectory,
        ref STARTUPINFO lpStartupInfo,
        out PROCESS_INFORMATION lpProcessInformation);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseHandle(IntPtr hObject);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct STARTUPINFO
    {
        public int cb;
        public string? lpReserved;
        public string? lpDesktop;
        public string? lpTitle;
        public uint dwX;
        public uint dwY;
        public uint dwXSize;
        public uint dwYSize;
        public uint dwXCountChars;
        public uint dwYCountChars;
        public uint dwFillAttribute;
        public uint dwFlags;
        public ushort wShowWindow;
        public ushort cbReserved2;
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
        public uint dwProcessId;
        public uint dwThreadId;
    }
}