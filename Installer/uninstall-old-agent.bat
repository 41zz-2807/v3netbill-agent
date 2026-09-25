@echo off
setlocal enabledelayedexpansion
chcp 1252 >nul 2>&1
title v3Netbill Agent - Uninstall versi lama

:: ============================================================
::  v3Netbill Agent - Uninstall versi yang terpasang sekarang
::  (versi lama yang masih running / diproteksi & tidak punya
::   tombol Remove). Jalankan SEKALI sebagai Administrator.
::  Tidak meminta PIN (versi lama belum punya PIN guard).
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
echo  v3Netbill Agent -- UNINSTALL VERSI LAMA
echo ============================================
echo.

:: 1) Matikan watchdog task agar tidak start ulang service
schtasks /Delete /TN "\v3Netbill\Agent Watchdog" /F >nul 2>&1
schtasks /Delete /TN "v3NetbillAgentWatchdog" /F >nul 2>&1
echo [OK] watchdog task dihapus

:: 2) Stop service & pastikan tidak auto-start
sc stop v3NetbillAgent >nul 2>&1
sc config v3NetbillAgent start= disabled >nul 2>&1
echo [OK] service v3NetbillAgent dihentikan

:: 3) Matikan semua proses agent
taskkill /F /IM Agent.Overlay.exe >nul 2>&1
taskkill /F /IM Agent.Service.exe >nul 2>&1
echo [OK] proses agent dihentikan

:: 4) Cari Product Code MSI lalu uninstall resmi
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

:: 5) Hapus service kalau masih tercatat
sc stop v3NetbillAgent >nul 2>&1
sc delete v3NetbillAgent >nul 2>&1
echo [OK] service (jika ada) dihapus

:: 6) Bersihkan file instalasi & shortcut startup
rd /s /q "%ProgramFiles%\v3NetbillAgent" >nul 2>&1
rd /s /q "%ProgramFiles(x86)%\v3NetbillAgent" >nul 2>&1
del /f /q "%ProgramData%\Microsoft\Windows\Start Menu\Programs\StartUp\v3Netbill Agent Overlay.lnk" >nul 2>&1
del /f /q "%AppData%\Microsoft\Windows\Start Menu\Programs\Startup\v3Netbill Agent Overlay.lnk" >nul 2>&1
for /d %%D in ("%ProgramData%\Microsoft\Windows\Start Menu\Programs\v3Netbill Agent") do rd /s /q "%%D" >nul 2>&1
for /d %%D in ("%AppData%\Microsoft\Windows\Start Menu\Programs\v3Netbill Agent") do rd /s /q "%%D" >nul 2>&1
del /f /q "%PUBLIC%\Documents\v3netbill-agent-stop.flag" >nul 2>&1
del /f /q "%USERPROFILE%\Documents\v3netbill-agent-stop.flag" >nul 2>&1
echo [OK] file & shortcut dibersihkan

:: 7) Bersihkan registry agent
reg delete "HKLM\Software\v3Netbill" /f >nul 2>&1
reg delete "HKCU\Software\v3Netbill" /f >nul 2>&1
reg delete "HKCU\Software\Microsoft\Windows\CurrentVersion\Policies\System" /v DisableTaskMgr /f >nul 2>&1
if defined PRODUCTCODE (
  reg delete "HKLM\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\{%PRODUCTCODE%}" /f >nul 2>&1
  reg delete "HKLM\SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall\{%PRODUCTCODE%}" /f >nul 2>&1
)
echo [OK] registry dibersihkan

:: 8) Pastikan tidak hidup lagi
sc query v3NetbillAgent >nul 2>&1
if errorlevel 1 (
  echo [OK] service sudah tidak ada.
) else (
  echo [WARN] service masih ada - cek ulang manual.
)

echo.
echo ============================================
echo  Selesai! Versi lama sudah dihapus.
echo  Sekarang jalankan installer MSI versi baru.
echo  (Major upgrade otomatis tidak lagi terkunci)
echo ============================================
echo.
pause