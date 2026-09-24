@echo off
setlocal EnableExtensions
set "OUT=%~dp0diagnosa.txt"

echo ================================================================= > "%OUT%"
echo  DIAGNOSA v3Netbill Agent >> "%OUT%"
echo  %DATE% %TIME% >> "%OUT%"
echo ================================================================= >> "%OUT%"
echo. >> "%OUT%"

echo [1] Registry normal: HKLM\SOFTWARE\v3Netbill\Agent >> "%OUT%"
reg query "HKLM\SOFTWARE\v3Netbill\Agent" >> "%OUT%" 2>&1
echo. >> "%OUT%"

echo [2] Registry WOW6432Node: HKLM\SOFTWARE\WOW6432Node\v3Netbill\Agent >> "%OUT%"
reg query "HKLM\SOFTWARE\WOW6432Node\v3Netbill\Agent" >> "%OUT%" 2>&1
echo. >> "%OUT%"

echo [3] Folder install x64 >> "%OUT%"
dir "C:\Program Files\v3NetbillAgent\Agent.Service\Agent.Service.exe" >> "%OUT%" 2>&1
echo. >> "%OUT%"

echo [4] Folder install x86 >> "%OUT%"
dir "C:\Program Files (x86)\v3NetbillAgent\Agent.Service\Agent.Service.exe" >> "%OUT%" 2>&1
echo. >> "%OUT%"

echo [5] appsettings.json (x64) >> "%OUT%"
type "C:\Program Files\v3NetbillAgent\Agent.Service\appsettings.json" >> "%OUT%" 2>&1
echo. >> "%OUT%"

echo [6] appsettings.json (x86) >> "%OUT%"
type "C:\Program Files (x86)\v3NetbillAgent\Agent.Service\appsettings.json" >> "%OUT%" 2>&1
echo. >> "%OUT%"

echo [7] Status service >> "%OUT%"
sc query v3NetbillAgent >> "%OUT%" 2>&1
echo. >> "%OUT%"

echo [8] Proses agent >> "%OUT%"
tasklist /fi "imagename eq Agent.Service.exe" >> "%OUT%" 2>&1
tasklist /fi "imagename eq Agent.Overlay.exe" >> "%OUT%" 2>&1
echo. >> "%OUT%"

echo ================================================================= >> "%OUT%"

type "%OUT%"
echo.
echo Hasil tersimpan di: %OUT%
echo Buka file itu dengan Notepad, Ctrl+A, Ctrl+C, lalu paste.
pause
