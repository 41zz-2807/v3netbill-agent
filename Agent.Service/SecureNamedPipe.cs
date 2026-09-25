using System;
using System.ComponentModel;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;
using V3Netbill.Agent.Core;

namespace V3Netbill.Agent.Service;

/// <summary>
/// Membuat NamedPipeServerStream yang DACL-nya sudah berisi "Everyone" (Full Access)
/// langsung saat pembuatan (SECURITY_ATTRIBUTES → CreateNamedPipe).
///
/// Mengapa tidak SetNamedSecurityInfoW di nama pipe?
/// Karena mengubah security descriptor sebuah named pipe membuat Windows MENUTUP semua
/// instance yang sedang ada (instance yang menunggu pun diputus), dan kalau instance
/// lama tidak di-dispose, nama pipe macet di "All pipe instances are busy" selamanya.
/// Pendekatan ini menghindari itu: ACL diset SEKALI di awal bersama instance dibuat.
/// </summary>
internal static class SecureNamedPipe
{
    private const uint PIPE_ACCESS_DUPLEX = 0x00000003;
    private const uint FILE_FLAG_OVERLAPPED = 0x40000000;
    private const uint PIPE_TYPE_MESSAGE = 0x00000004;
    private const uint PIPE_READMODE_MESSAGE = 0x00000002;
    private const uint PIPE_WAIT = 0x00000000;
    private const uint SDDL_REVISION_1 = 1;
    private const int BUFFER_SIZE = 8192;

    /// <summary>
    /// Buat instance server pipe dengan DACL Everyone tanpa mematikan instance lain.
    /// Kembalikan null + pesan error bila gagal.
    /// </summary>
    public static NamedPipeServerStream? CreateServer(string pipeName, int maxInstances, out string? error)
    {
        error = null;
        IntPtr hPipe = IntPtr.Zero;
        IntPtr pSd = IntPtr.Zero;
        try
        {
            if (!ConvertStringSecurityDescriptorToSecurityDescriptorW(
                    @"D:(A;;GA;;;WD)", SDDL_REVISION_1, out pSd, out _))
            {
                error = $"ConvertStringSecurityDescriptorToSecurityDescriptorW gagal: 0x{Marshal.GetLastWin32Error():X8}";
                AgentLog.Write(error);
                return null;
            }

            var sa = new SECURITY_ATTRIBUTES
            {
                nLength = Marshal.SizeOf<SECURITY_ATTRIBUTES>(),
                lpSecurityDescriptor = pSd,
                bInheritHandle = 0,
            };

            hPipe = CreateNamedPipeW(
                @"\\.\pipe\" + pipeName,
                PIPE_ACCESS_DUPLEX | FILE_FLAG_OVERLAPPED,
                PIPE_TYPE_MESSAGE | PIPE_READMODE_MESSAGE | PIPE_WAIT,
                maxInstances,
                BUFFER_SIZE,
                BUFFER_SIZE,
                0,
                ref sa);

            if (hPipe == new IntPtr(-1))
            {
                error = $"CreateNamedPipe gagal: 0x{Marshal.GetLastWin32Error():X8}";
                AgentLog.Write(error);
                return null;
            }

            var safeHandle = new SafePipeHandle(hPipe, ownsHandle: true);
            hPipe = IntPtr.Zero; // kini milik safeHandle

            var pipe = new NamedPipeServerStream(PipeDirection.InOut, isAsync: true, isConnected: false, safeHandle);
            AgentLog.Write("CreateNamedPipe OK — pipe dibuat DENGAN ACL Everyone saat pembuatan");
            return pipe;
        }
        finally
        {
            if (hPipe != IntPtr.Zero) CloseHandle(hPipe);
            if (pSd != IntPtr.Zero) LocalFree(pSd);
        }
    }

    // ===== Win32 =====

    [StructLayout(LayoutKind.Sequential)]
    private struct SECURITY_ATTRIBUTES
    {
        public int nLength;
        public IntPtr lpSecurityDescriptor;
        public int bInheritHandle;
    }

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern IntPtr CreateNamedPipeW(
        string lpName,
        uint dwOpenMode,
        uint dwPipeMode,
        int nMaxInstances,
        int nOutBufferSize,
        int nInBufferSize,
        int nDefaultTimeOut,
        ref SECURITY_ATTRIBUTES lpSecurityAttributes);

    [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ConvertStringSecurityDescriptorToSecurityDescriptorW(
        string stringSecurityDescriptor,
        uint stringSDRevision,
        out IntPtr securityDescriptor,
        out uint securityDescriptorSize);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseHandle(IntPtr hObject);

    [DllImport("kernel32.dll")]
    private static extern IntPtr LocalFree(IntPtr hMem);
}