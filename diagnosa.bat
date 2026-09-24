@echo off
setlocal
set "OUT=%~dp0diagnosa.txt"
if exist "%OUT%" del "%OUT%"

(
echo ================================================================
echo  DIAGNOSA v3Netbill Agent  -  %DATE% %TIME%
echo ================================================================
echo.
echo --- [1] Registry normal: HKLM\SOFTWARE\v3Netbill\Agent ---
reg query "HKLM\SOFTWARE\v3Netbill\Agent"
echo.
echo --- [2] Registry WOW6432Node: HKLM\SOFTWARE\WOW6432Node\v3Netbill\Agent ---
reg query "HKLM\SOFTWARE\WOW6432Node\v3Netbill\Agent"
echo.
echo --- [3] Lokasi folder install ---
if exist "C:\Program Files\v3NetbillAgent\" (
    echo KETEMU: C:\Program Files\v3NetbillAgent\
    dir "C:\Program Files\v3NetbillAgent\Agent.Service\Agent.Service.exe"
) else (
    echo tidak ada di Program Files ^(x64^)
)
if exist "C:\Program Files (x86)\v3NetbillAgent\" (
    echo KETEMU: C:\Program Files (x86)\v3NetbillAgent\
    dir "C:\Program Files (x86)\v3NetbillAgent\Agent.Service\Agent.Service.exe"
) else (
    echo tidak ada di Program Files (x86)
)
echo.
echo --- [4] Isi appsettings.json ---
if exist "C:\Program Files\v3NetbillAgent\Agent.Service\appsettings.json" (
    echo === C:\Program Files\v3NetbillAgent\Agent.Service\appsettings.json ===
    type "C:\Program Files\v3NetbillAgent\Agent.Service\appsettings.json"
)
if exist "C:\Program Files (x86)\v3NetbillAgent\Agent.Service\appsettings.json" (
    echo === C:\Program Files (x86)\v3NetbillAgent\Agent.Service\appsettings.json ===
    type "C:\Program Files (x86)\v3NetbillAgent\Agent.Service\appsettings.json"
)
echo.
echo --- [5] Status service v3NetbillAgent ---
sc query v3NetbillAgent
echo.
echo --- [6] Proses overlay ---
tasklist /fi "imagename eq Agent.Overlay.exe"
tasklist /fi "imagename eq Agent.Service.exe"
echo.
echo ================================================================
) > "%OUT%" 2>&1

type "%OUT%"
echo.
echo HASIL TERSIMPAN DI: %OUT%
echo Buka file itu di Notepad, tekan Ctrl+A lalu Ctrl+C, lalu paste.
pause
