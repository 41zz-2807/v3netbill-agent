@echo off
rem v3Netbill Agent watchdog scheduled task.
rem Menjaga service tetap hidup: jika berhenti, aktifkan kembali & start.
rem Jika service sudah dihapus (uninstall sah via PIN), hapus task ini.
sc qc v3NetbillAgent >nul 2>&1
if errorlevel 1 goto :gone
sc query v3NetbillAgent | findstr /I "RUNNING" >nul
if errorlevel 1 (
  sc config v3NetbillAgent start= auto >nul 2>&1
  sc start v3NetbillAgent >nul 2>&1
)
exit /b 0

:gone
schtasks /Delete /TN "v3NetbillAgentWatchdog" /F >nul 2>&1
exit /b 0