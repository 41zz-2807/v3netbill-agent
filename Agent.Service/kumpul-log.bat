@echo off
rem ==============================================================================
rem  kumpul-log.bat - kumpulkan log & status agent untuk dikirim ke server.
rem
rem  Dipanggil OTOMATIS oleh Agent.Service ketika socket ke server dinyatakan
rem  mati (lihat TandaiSocketMati di Agent.Core/ServerConnection.cs). Hasilnya
rem  diletakkan di folder staging, lalu Agent.Core meng-zip dan mengunggah.
rem
rem  ⚠️ YANG SENGAJA TIDAK DIKUMPULKAN — JANGAN TAMBAHKAN:
rem     - registry HKLM\Software\v3Netbill\Agent  (berisi AgentToken yang
rem       mengizinkan create_password + stop_session, OtpBotToken yang adalah
rem       token bot Telegram AKTIF, dan BypassPinHash)
rem     - appsettings.json
rem  Jangan pernah menulis credential ke berkas yang dikirim ke server. Kalau ini
rem  bocor, siapa pun yang bisa membaca hasil diagnosa bisa mengambil alih PC.
rem ==============================================================================
setlocal EnableExtensions
set "WORK=%ProgramData%\v3NetbillAgent\diagnosa"
set "LOGS=%ProgramData%\v3NetbillAgent\logs"
set "OUT=%WORK%\ringkasan.txt"

if exist "%WORK%" rd /s /q "%WORK%" 2>nul
mkdir "%WORK%" 2>nul
if not exist "%WORK%" exit /b 1

echo ================================================================= >> "%OUT%"
echo  DIAGNOSA v3Netbill Agent  %DATE% %TIME% >> "%OUT%"
echo ================================================================= >> "%OUT%"
echo. >> "%OUT%"

rem --- [1] Service -------------------------------------------------------------
echo [1] Service >> "%OUT%"
sc qc v3NetbillAgent >> "%OUT%" 2>&1
echo. >> "%OUT%"
sc query v3NetbillAgent >> "%OUT%" 2>&1
echo. >> "%OUT%"

rem --- [2] Scheduled task watchdog --------------------------------------------
echo [2] Scheduled task watchdog >> "%OUT%"
schtasks /Query /TN "\v3Netbill\Agent Watchdog" /V /FO LIST >> "%OUT%" 2>&1
echo. >> "%OUT%"

rem --- [3] Proses --------------------------------------------------------------
echo [3] Proses agent >> "%OUT%"
tasklist /fi "imagename eq Agent.Service.exe" >> "%OUT%" 2>&1
tasklist /fi "imagename eq Agent.Overlay.exe" >> "%OUT%" 2>&1
echo. >> "%OUT%"

rem --- [4] Berkas log 7 hari terakhir -----------------------------------------
echo [4] Berkas log (7 hari terakhir) >> "%OUT%"
dir /b /o-d "%LOGS%\*.log" >> "%OUT%" 2>&1
echo. >> "%OUT%"

rem Hitung mundur 7 hari lewat PowerShell; kalau tidak tersedia, tetap jalan
rem tanpa menyalin log (rangkuman status di atas tetap berguna).
for /f "usebackq delims=" %%D in (`powershell -NoProfile -NonInteractive -Command ^
  "(Get-Date).AddDays(-7).ToString('yyyyMMdd')" 2^>nul`) do set "MULAI=%%D"

if defined MULAI (
  echo [5] Isi log (mulai %MULAI%) >> "%OUT%"
  for %%F in ("%LOGS%\*.log") do (
    echo. >> "%OUT%"
    echo ----- %%~nxF ----- >> "%OUT%"
    copy /y "%%F" "%WORK%\%%~nxF" >nul 2>&1
  )
  echo. >> "%OUT%"
)

rem --- [6] Event log: service stop tak terduga & crash -------------------------
echo [6] Event Log (7031/7034/1000, 15 terakhir) >> "%OUT%"
wevtutil qe Application /q:"*[System[(EventID=7031 or EventID=7034 or EventID=1000)]]" /c:15 /rd:true /f:text >> "%OUT%" 2>&1
echo. >> "%OUT%"

echo [7] Selesai dikumpulkan: %DATE% %TIME% >> "%OUT%"
exit /b 0