@echo off
setlocal EnableExtensions
set "OUT=%~dp0diagnosa.txt"
if exist "%OUT%" del "%OUT%"

echo ================================================================= >> "%OUT%"
echo  DIAGNOSA v2 v3Netbill Agent  %DATE% %TIME% >> "%OUT%"
echo ================================================================= >> "%OUT%"
echo. >> "%OUT%"

echo [1] Service: binary path (exe yang dipakai service) >> "%OUT%"
sc qc v3NetbillAgent >> "%OUT%" 2>&1
echo. >> "%OUT%"

echo [2] Folder install x64 >> "%OUT%"
dir "C:\Program Files\v3NetbillAgent\Agent.Service\Agent.Service.exe" >> "%OUT%" 2>&1
dir "C:\Program Files\v3NetbillAgent\Agent.Overlay\Agent.Overlay.exe" >> "%OUT%" 2>&1
echo. >> "%OUT%"

echo [3] Folder install x86 >> "%OUT%"
dir "C:\Program Files (x86)\v3NetbillAgent\Agent.Service\Agent.Service.exe" >> "%OUT%" 2>&1
dir "C:\Program Files (x86)\v3NetbillAgent\Agent.Overlay\Agent.Overlay.exe" >> "%OUT%" 2>&1
echo. >> "%OUT%"

echo [4] Proses agent (Perhatikan kolom SESSION NAME: Services=0 tak terlihat, Console=terlihat) >> "%OUT%"
tasklist /fi "imagename eq Agent.Service.exe" /v >> "%OUT%" 2>&1
tasklist /fi "imagename eq Agent.Overlay.exe" /v >> "%OUT%" 2>&1
echo. >> "%OUT%"

echo [5] Registry normal >> "%OUT%"
reg query "HKLM\SOFTWARE\v3Netbill\Agent" >> "%OUT%" 2>&1
echo. >> "%OUT%"

echo [6] Registry WOW6432Node >> "%OUT%"
reg query "HKLM\SOFTWARE\WOW6432Node\v3Netbill\Agent" >> "%OUT%" 2>&1
echo. >> "%OUT%"

echo [7] appsettings.json x64 >> "%OUT%"
type "C:\Program Files\v3NetbillAgent\Agent.Service\appsettings.json" >> "%OUT%" 2>&1
echo. >> "%OUT%"

echo [8] appsettings.json x86 >> "%OUT%"
type "C:\Program Files (x86)\v3NetbillAgent\Agent.Service\appsettings.json" >> "%OUT%" 2>&1
echo. >> "%OUT%"

echo [9] Event log APP: error terakhir 1 hari (1000/1026) >> "%OUT%"
wevtutil qe Application /q:"*[System[(EventID=1000 or EventID=1026 or EventID=1027)]]" /c:10 /rd:true /f:text >> "%OUT%" 2>&1

echo ================================================================= >> "%OUT%"

type "%OUT%"
echo.
echo Hasil tersimpan di: %OUT%
echo Buka di Notepad, Ctrl+A, Ctrl+C, lalu paste.
pause