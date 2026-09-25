using System;
using System.ComponentModel;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;
using V3Netbill.Agent.Core;

namespace V3Netbill.Agent.Service;

/// <summary>
/// Memberi ACL "Everyone" (Full Access) ke named pipe service.
/// .NET 8 tidak lagi menyediakan overload constructor dengan PipeSecurity,
/// jadi DACL di-set langsung via Win32:
///   1) SetSecurityInfo pada HANDLE instance yang terbuka (SE_KERNEL_OBJECT) — cara andal.
///   2) SetNamedSecurityInfoW pada NAMA pipe (SE_FILE_OBJECT) — untuk referensi.
/// </summary>
internal static class SecureNamedPipe
{
    private const int SE_KERNEL_OBJECT = 6;
    private const int SE_FILE_OBJECT = 1;
    private const uint DACL_SECURITY_INFORMATION = 0x00000004;
    private const uint SDDL_REVISION_1 = 1;

    public static void GrantEveryoneAccess(SafePipeHandle pipeHandle, string pipeName)
    {
        IntPtr pSd = IntPtr.Zero;
        try
        {
            if (!ConvertStringSecurityDescriptorToSecurityDescriptorW(
                    @"D:(A;;GA;;;WD)", SDDL_REVISION_1, out pSd, out _))
            {
                throw new Win32Exception(Marshal.GetLastWin32Error(), "ConvertStringSecurityDescriptorToSecurityDescriptorW gagal");
            }

            if (!GetSecurityDescriptorDacl(pSd, out bool daclPresent, out IntPtr pDacl, out bool daclDefaulted))
            {
                throw new Win32Exception(Marshal.GetLastWin32Error(), "GetSecurityDescriptorDacl gagal");
            }

            if (!daclPresent || pDacl == IntPtr.Zero)
            {
                throw new Win32Exception(0, "Tidak ada DACL pada security descriptor");
            }

            // 1) Set DACL pada handle instance yang terbuka (paling andal untuk instance aktif).
            int rHandle = SetSecurityInfo(
                pipeHandle,
                SE_KERNEL_OBJECT,
                DACL_SECURITY_INFORMATION,
                IntPtr.Zero,
                IntPtr.Zero,
                pDacl,
                IntPtr.Zero);
            if (rHandle == 0)
            {
                AgentLog.Write("Pipe ACL OK via handle (SE_KERNEL_OBJECT)");
            }
            else
            {
                AgentLog.Write($"Pipe ACL via handle (SE_KERNEL_OBJECT) gagal: 0x{rHandle:X8}");
            }

            // 2) Set DACL pada nama pipe (SE_FILE_OBJECT) untuk instansi berikutnya.
            int rName = SetNamedSecurityInfoW(
                @"\\.\pipe\" + pipeName,
                SE_FILE_OBJECT,
                DACL_SECURITY_INFORMATION,
                IntPtr.Zero,
                IntPtr.Zero,
                pDacl,
                IntPtr.Zero);
            if (rName == 0)
            {
                AgentLog.Write("Pipe ACL OK via nama (SE_FILE_OBJECT)");
            }
            else
            {
                AgentLog.Write($"Pipe ACL via nama (SE_FILE_OBJECT) gagal: 0x{rName:X8}");
            }

            if (rHandle != 0 && rName != 0)
            {
                throw new Win32Exception(rHandle, "SetSecurityInfo (KernelObject) dan SetNamedSecurityInfoW (FileObject) sama-sama gagal");
            }
        }
        finally
        {
            if (pSd != IntPtr.Zero) LocalFree(pSd);
        }
    }

    [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ConvertStringSecurityDescriptorToSecurityDescriptorW(
        string stringSecurityDescriptor,
        uint stringSDRevision,
        out IntPtr securityDescriptor,
        out uint securityDescriptorSize);

    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetSecurityDescriptorDacl(
        IntPtr securityDescriptor,
        out bool lpbDaclPresent,
        out IntPtr pDacl,
        out bool lpbDaclDefaulted);

    [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern int SetNamedSecurityInfoW(
        string pObjectName,
        int objectType,
        uint securityInfo,
        IntPtr psidOwner,
        IntPtr psidGroup,
        IntPtr pDacl,
        IntPtr pSacl);

    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern int SetSecurityInfo(
        SafeHandle pHandle,
        int objectType,
        uint securityInfo,
        IntPtr psidOwner,
        IntPtr psidGroup,
        IntPtr pDacl,
        IntPtr pSacl);

    [DllImport("kernel32.dll")]
    private static extern IntPtr LocalFree(IntPtr hMem);
}