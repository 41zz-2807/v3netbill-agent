using System;
using System.IO;

namespace V3Netbill.Agent.Core;

/// <summary>
/// Log sederhana ke file, dipakai service &amp; overlay agar masalah mudah
/// diinspeksi di lapangan (Windows Event Log tidak tersedia tanpa provider).
/// File: C:\ProgramData\v3NetbillAgent\logs\&lt;FileName&gt;.log
/// </summary>
public static class AgentLog
{
    private static readonly object Gate = new();
    private const long MaxBytes = 1_000_000;
    private const long TrimToBytes = 250_000;

    public static string FileName { get; set; } = "agent.log";

    private static string LogsDir => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
        "v3NetbillAgent", "logs");

    private static string LogPath => Path.Combine(LogsDir, FileName);

    public static void Write(string message)
    {
        try
        {
            lock (Gate)
            {
                Directory.CreateDirectory(LogsDir);
                var line = $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} {message}{Environment.NewLine}";
                TrimIfNeeded();
                File.AppendAllText(LogPath, line);
            }
        }
        catch
        {
            // Log tidak boleh menggagalkan proses utama.
        }
    }

    public static void Write(Exception? ex, string context)
    {
        string detail = ex == null
            ? string.Empty
            : $"{ex.GetType().Name}: {ex.Message} (0x{ex.HResult:X8}){Environment.NewLine}  {ex.StackTrace ?? string.Empty}";
        Write($"{context}{Environment.NewLine}  => {detail}");
    }

    private static void TrimIfNeeded()
    {
        try
        {
            var fi = new FileInfo(LogPath);
            if (fi.Exists && fi.Length > MaxBytes)
            {
                using var fs = new FileStream(LogPath, FileMode.Open, FileAccess.ReadWrite, FileShare.ReadWrite);
                fs.SetLength(TrimToBytes);
            }
        }
        catch
        {
        }
    }
}