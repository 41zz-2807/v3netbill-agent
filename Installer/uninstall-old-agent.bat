@echo off
setlocal enabledelayedexpansion
chcp 1252 >nul 2>&1
title v3Netbill Agent - Uninstall

:: ============================================================
::  v3Netbill Agent - Uninstall
::
::  PIN diverifikasi ke server SEBELUM service disentuh.
::  Kalau PIN salah, PIN belum di-set, atau server tidak bisa
::  dihubungi, script berhenti di sini dan service tetap
::  jalan seperti semula.
::
::  Penting: urutan lama (stop service dulu, PIN belakangan)
::  berarti billing bisa dimatikan tanpa otorisasi -- begitu
::  service mati, PC langsung bisa dipakai tanpa penagihan.
:: ============================================================

:: --- Cek hak admin & self-elevate ---
net session >nul 2>&1
if %errorlevel% neq 0 (
  echo Meminta hak Administrator...
  powershell -NoProfile -Command "Start-Process -FilePath '%~f0' -Verb RunAs"
  exit /b
)

echo.
echo ============================================
echo  v3Netbill Agent -- UNINSTALL
echo ============================================
echo.
echo Service dibiarkan berjalan sampai PIN terverifikasi.
echo.

:: ============================================================
::  LANGKAH 1 - Baca identitas PC dari registry
:: ============================================================
set "V3_SERVERURL="
set "V3_PCID="
set "V3_TOKEN="

for /f "tokens=2,*" %%A in ('reg query "HKLM\Software\v3Netbill\Agent" /v ServerUrl 2^>nul ^| find "REG_SZ"') do set "V3_SERVERURL=%%B"
for /f "tokens=2,*" %%A in ('reg query "HKLM\Software\v3Netbill\Agent" /v PcId       2^>nul ^| find "REG_SZ"') do set "V3_PCID=%%B"
for /f "tokens=2,*" %%A in ('reg query "HKLM\Software\v3Netbill\Agent" /v AgentToken 2^>nul ^| find "REG_SZ"') do set "V3_TOKEN=%%B"

if not defined V3_SERVERURL set "V3_SERVERURL=http://localhost:3000"
if not defined V3_PCID      set "V3_PCID="
if not defined V3_TOKEN     set "V3_TOKEN="
goto :pin_step

:: ============================================================
::  LANGKAH 2 - Minta PIN (tersembunyi) lalu verifikasi ke server
::  Dilakukan SEBELUM service dihentikan.
:: ============================================================
:pin_step
:: Registry boleh kosong: versi agent yang sudah pernah di-uninstall script
:: sebelumnya bisa saja sudah menghapus key itu. Kalau begitu operator
:: diminta mengisi sendiri, dan kalau server tidak terjangkau script Offer
:: URL alternatif. Yang tidak boleh terjadi adalah script buntu.
if not defined V3_PCID  (
  echo [i] PcId tidak ada di registry.
  set /p "V3_PCID=Masukkan PC ID  : "
)
if not defined V3_TOKEN (
  echo [i] AgentToken tidak ada di registry.
  set /p "V3_TOKEN=Masukkan Agent Token: "
)
if not defined V3_PCID  goto :abort_manual
if not defined V3_TOKEN goto :abort_manual

call :verify_pin
if "%PIN_RESULT%"=="0" (
  echo.
  echo [OK] PIN benar. Lanjut uninstall.
  goto :after_pin
)
if "%PIN_RESULT%"=="4" goto :server_unreachable
if "%PIN_RESULT%"=="6" goto :server_unreachable
goto :pin_rejected

:server_unreachable
echo.
echo [i] Server di registry tidak bisa dihubungi: %V3_SERVERURL%
set /p "V3_SERVERURL=Masukkan URL server yang benar: "
if not defined V3_SERVERURL (
  echo URL kosong. Batal.
  goto :abort_manual
)
call :verify_pin
if "%PIN_RESULT%"=="0" (
  echo.
  echo [OK] PIN benar. Lanjut uninstall.
  goto :after_pin
)
goto :pin_rejected

:pin_rejected
echo.
if "%PIN_RESULT%"=="5" (
  echo [STOP] PIN kosong. Uninstall dibatalkan.
  goto :abort
)
if "%PIN_RESULT%"=="2" (
  echo [STOP] PIN salah. Uninstall dibatalkan.
  goto :abort
)
if "%PIN_RESULT%"=="3" (
  echo [STOP] PIN Uninstall belum pernah diset di halaman Pengaturan.
  echo        Set dulu di web: Settings - PIN Uninstall.
  goto :abort
)
if "%PIN_RESULT%"=="6" (
  echo [STOP] Server menolak PcId atau AgentToken ini.
  echo        Pastikan keduanya sama dengan data PC di web.
  goto :abort
)
if "%PIN_RESULT%"=="4" (
  echo [STOP] Server tetap tidak bisa dihubungi, jadi PIN tidak terverifikasi.
  echo        Uninstall dibatalkan dan service TIDAK dihentikan.
  echo.
  echo        Untuk uninstall paksa, jalankan dari prompt Admin:
  echo          sc stop v3NetbillAgent
  echo          sc delete v3NetbillAgent
  echo        lalu hapus folder %ProgramFiles%\v3NetbillAgent
  goto :abort
)
echo [STOP] Verifikasi PIN gagal dengan kode %PIN_RESULT%. Uninstall dibatalkan.
goto :abort

:abort_manual
echo.
echo Service TIDAK dihentikan. Billing berjalan seperti biasa.
echo.
echo Kalau registry agent sudah hilang, uninstall tidak bisa diverifikasi.
echo Install ulang MSI lebih dulu supaya PcId dan AgentToken terisi,
echo atau jalankan perintah hapus manual yang tertulis di README.
echo.
pause
exit /b 1

:: --- Helper: verifikasi PIN, hasil di PIN_RESULT ---
:: PIN sudah diinput sekali di V3_PIN, jadi saat operator mengganti URL
:: server karena tidak terjangkau, PIN tidak diminta ulang.
:verify_pin
if not defined V3_PIN (
  echo.
  set /p "V3_PIN=Masukkan PIN Uninstall: "
)
if not defined V3_PIN (
  set "PIN_RESULT=5"
  exit /b 0
)
set "V3_PINSENT=%V3_PIN%"
set "V3_PIN="
powershell -NoProfile -ExecutionPolicy Bypass -Command ^
  "$ErrorActionPreference='Stop';" ^
  "$pin=$env:V3_PINSENT;" ^
  "if([string]::IsNullOrWhiteSpace($pin)){Write-Host 'PIN_EMPTY';exit 5};" ^
  "try {" ^
  "  $body=@{pcId=$env:V3_PCID;agentToken=$env:V3_TOKEN;pin=$pin}|ConvertTo-Json -Compress;" ^
  "  $uri=$env:V3_SERVERURL.TrimEnd('/')+'/api/settings/verify-pin';" ^
  "  $r=Invoke-RestMethod -Uri $uri -Method Post -Body $body -ContentType 'application/json' -TimeoutSec 20;" ^
  "  if($r.configured -eq $false){Write-Host 'PIN_UNSET';exit 3};" ^
  "  if($r.valid -eq $true){Write-Host 'PIN_OK';exit 0};" ^
  "  Write-Host 'PIN_WRONG';exit 2;" ^
  "} catch [System.Net.WebException] {" ^
  "  $code=0;" ^
  "  if($_.Exception.Response){$code=[int]$_.Exception.Response.StatusCode};" ^
  "  if($code -eq 401 -or $code -eq 403){Write-Host 'PIN_UNAUTHORIZED';exit 6};" ^
  "  Write-Host 'PIN_UNREACHABLE';exit 4;" ^
  "} catch {" ^
  "  Write-Host 'PIN_UNREACHABLE';exit 4;" ^
  "}"
set "PIN_RESULT=%errorlevel%"
set "V3_PINSENT="
exit /b 0

:abort
echo.
echo Service TIDAK dihentikan. Billing berjalan seperti biasa.
echo.
pause
exit /b 1

:: ============================================================
::  LANGKAH 3 - PIN valid. Baru sekarang service boleh dihentikan.
:: ============================================================
:after_pin
echo.
echo --- Uninstall berjalan ---

:: 3a) Matikan watchdog task agar tidak start ulang service
schtasks /Delete /TN "\v3Netbill\Agent Watchdog" /F >nul 2>&1
schtasks /Delete /TN "v3NetbillAgentWatchdog" /F >nul 2>&1
echo [OK] watchdog task dihapus

:: 3b) Stop service & pastikan tidak auto-start
sc stop v3NetbillAgent >nul 2>&1
sc config v3NetbillAgent start= disabled >nul 2>&1
echo [OK] service v3NetbillAgent dihentikan

:: 3c) Matikan semua proses agent
taskkill /F /IM Agent.Overlay.exe >nul 2>&1
taskkill /F /IM Agent.Service.exe >nul 2>&1
echo [OK] proses agent dihentikan

:: 3d) Cari Product Code MSI lalu uninstall resmi
set "PRODUCTCODE="
for /f "delims=" %%G in ('powershell -NoProfile -Command "$s=Get-ItemProperty 'HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\*','HKLM:\SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall\*' -ErrorAction SilentlyContinue | Where-Object { $_.DisplayName -eq 'v3Netbill Agent' } | Select-Object -First 1; if($s){ (Split-Path $s.PSPath -Leaf).Trim('{}') }"') do set "PRODUCTCODE=%%G"

if defined PRODUCTCODE (
  echo [MSI] product code ditemukan : {!PRODUCTCODE!}
  echo [MSI] menjalankan msiexec /x ...
  msiexec /x {!PRODUCTCODE!} /qn /norestart
  if errorlevel 1 (
    echo [WARN] msiexec selesai dengan kode error - lanjut hapus manual.
  ) else (
    echo [OK] MSI uninstall selesai.
  )
) else (
  echo [i] product code tidak ditemukan - lanjut hapus manual.
)

:: 3e) Hapus service kalau masih tercatat
sc stop v3NetbillAgent >nul 2>&1
sc delete v3NetbillAgent >nul 2>&1
echo [OK] service (jika ada) dihapus

:: 3f) Bersihkan file instalasi & shortcut startup
rd /s /q "%ProgramFiles%\v3NetbillAgent" >nul 2>&1
rd /s /q "%ProgramFiles(x86)%\v3NetbillAgent" >nul 2>&1
del /f /q "%ProgramData%\Microsoft\Windows\Start Menu\Programs\StartUp\v3Netbill Agent Overlay.lnk" >nul 2>&1
del /f /q "%AppData%\Microsoft\Windows\Start Menu\Programs\Startup\v3Netbill Agent Overlay.lnk" >nul 2>&1
for /d %%D in ("%ProgramData%\Microsoft\Windows\Start Menu\Programs\v3Netbill Agent") do rd /s /q "%%D" >nul 2>&1
for /d %%D in ("%AppData%\Microsoft\Windows\Start Menu\Programs\v3Netbill Agent") do rd /s /q "%%D" >nul 2>&1
del /f /q "%PUBLIC%\Documents\v3netbill-agent-stop.flag" >nul 2>&1
del /f /q "%USERPROFILE%\Documents\v3netbill-agent-stop.flag" >nul 2>&1
echo [OK] file & shortcut dibersihkan

:: 3g) Bersihkan registry agent
reg delete "HKLM\Software\v3Netbill" /f >nul 2>&1
reg delete "HKCU\Software\v3Netbill" /f >nul 2>&1
reg delete "HKCU\Software\Microsoft\Windows\CurrentVersion\Policies\System" /v DisableTaskMgr /f >nul 2>&1
if defined PRODUCTCODE (
  reg delete "HKLM\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\{%PRODUCTCODE%}" /f >nul 2>&1
  reg delete "HKLM\SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall\{%PRODUCTCODE%}" /f >nul 2>&1
)
echo [OK] registry dibersihkan

:: 3h) Pastikan tidak hidup lagi
sc query v3NetbillAgent >nul 2>&1
if errorlevel 1 (
  echo [OK] service sudah tidak ada.
) else (
  echo [WARN] service masih ada - cek ulang manual.
)

echo.
echo ============================================
echo  Selesai! Agent sudah dihapus.
echo  Install lagi lewat installer MSI bila perlu.
echo ============================================
echo.
pause
exit /b 0
