using System;
using System.ComponentModel;
using System.Runtime.InteropServices;

namespace V3Netbill.Agent.Service;

/// <summary>
/// Memberi ACL "Everyone" (Full Access) ke named pipe service.
/// .NET 8 tidak lagi menyediakan overload constructor dengan PipeSecurity,
/// jadi DACL di-set langsung via Win32 SetNamedSecurityInfoW.
/// </summary>
internal static class SecureNamedPipe
{
    private const int SE_KERNEL_OBJECT = 6;
    private const uint DACL_SECURITY_INFORMATION = 0x00000004;
    private const uint SDDL_REVISION_1 = 1;

    public static void GrantEveryoneAccess(string pipeName)
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

            int result = SetNamedSecurityInfoW(
                @"\\.\pipe\" + pipeName,
                SE_KERNEL_OBJECT,
                DACL_SECURITY_INFORMATION,
                IntPtr.Zero,
                IntPtr.Zero,
                pDacl,
                IntPtr.Zero);

            if (result != 0)
            {
                throw new Win32Exception(result, "SetNamedSecurityInfoW gagal");
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

    [DllImport("kernel32.dll")]
    private static extern IntPtr LocalFree(IntPtr hMem);
}