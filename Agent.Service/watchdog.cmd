@echo off
rem ==============================================================================
rem  watchdog.cmd - jaga agent tetap hidup, dan beri tahu ke Telegram kalau
rem  ada yang menghentikan atau meng-uninstall agent di luar aplikasi.
rem
rem  Dijalankan tiap menit sebagai scheduled task \v3Netbill\Agent Watchdog
rem  (SYSTEM). Sengaja TIDAK bergantung pada agent: kalau yang mengirim
rem  peringatan adalah agent itu sendiri, peringatan tidak akan pernah sampai
rem  tepat untuk kejadian yang justru diobserver.
rem
rem  Keadaan yang dilihat:
rem    1. service tidak ada        -> agent di-uninstall -> BRIERI TAHU
rem    2. service ada tapi STOPPED -> dihentikan paksa -> BRIERI TAHU + hidupkan lagi
rem    3. service tidak bisa start -> exenya hilang      -> BRIERI TAHU (dibatasi 6 jam)
rem
rem  Yang TIDAK dilaporkan:
rem    - operator menekan STOP AGENT (mode maintenance): ada flag-nya
rem    - uninstall dengan PIN admin yang diterima: ada penanda dari UninstallGuardWindow
rem ==============================================================================
setlocal EnableExtensions

set "NAMA=v3NetbillAgent"
set "TASK=\v3Netbill\Agent Watchdog"
set "ALERT=%~dp0kirim-alert.ps1"
set "FLAGSTOP=%PUBLIC%\v3netbill-agent-stop.flag"
set "PENUNDAH=%PUBLIC%\Documents\v3netbill-agent-uninstall-sah.flag"

rem Operator SENGAJA menghentikan agent lewat PIN emergency.
if exist "%FLAGSTOP%" exit /b 0

rem Uninstall yang PIN-nya sudah diterima oleh UninstallGuardWindow. Penanda
rem dihapus di sini supaya tidak membuat peringatan undead diam-diam di
rem kali uninstall berikutnya.
if exist "%PENUNDAH%" (
    del /f /q "%PENUNDAH%" >nul 2>&1
    exit /b 0
)

sc qc %NAMA% >nul 2>&1
if errorlevel 1 goto :hilang

sc query %NAMA% | findstr /I "RUNNING" >nul
if not errorlevel 1 exit /b 0

rem --- service ada tapi tidak berjalan -----------------------------------------
sc config %NAMA% start= auto >nul 2>&1
sc start %NAMA% >nul 2>&1
rem Beri jeda supaya hasil sc start selesai, lalu cek benar-benar jalan.
timeout /t 5 /nobreak >nul 2>&1
sc query %NAMA% | findstr /I "RUNNING" >nul
if not errorlevel 1 (
    if exist "%ALERT%" powershell -NoProfile -ExecutionPolicy Bypass -File "%ALERT%" -Jenis stopped >nul 2>&1
) else (
    if exist "%ALERT%" powershell -NoProfile -ExecutionPolicy Bypass -File "%ALERT%" -Jenis gagal >nul 2>&1
)
exit /b 0

rem --- service hilang sama sekali (uninstall) -----------------------------------
:hilang
if exist "%ALERT%" powershell -NoProfile -ExecutionPolicy Bypass -File "%ALERT%" -Jenis deleted >nul 2>&1
rem Nama yang dihapus harus PERSIS sama dengan yang dibuat
rem EnsureWatchdogScheduledTask (Worker.cs).
schtasks /Delete /TN "%TASK%" /F >nul 2>&1
exit /b 0