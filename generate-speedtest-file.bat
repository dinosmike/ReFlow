@echo off
setlocal enableextensions

REM Creates 2GB random speedtest file: speedtest.bin
REM Output location: same folder as this .bat

set "OUT=%~dp0speedtest.bin"
set "SIZE=1GB"

echo.
echo == ReFlow: generating %SIZE% file ==
echo Output: "%OUT%"
echo This can take a while...
echo.

powershell -NoProfile -ExecutionPolicy Bypass -Command ^
  "$path='%OUT%'; $sizeBytes=%SIZE%; " ^
  "$fs=[System.IO.File]::Open($path,[System.IO.FileMode]::Create,[System.IO.FileAccess]::Write,[System.IO.FileShare]::None);" ^
  "try { $rng=[System.Security.Cryptography.RandomNumberGenerator]::Create(); $buf=New-Object byte[] (4MB); $remaining=[int64]$sizeBytes; " ^
  "while($remaining -gt 0){ $toWrite=[int][Math]::Min([int64]$buf.Length,$remaining); $rng.GetBytes($buf); $fs.Write($buf,0,$toWrite); $remaining-=$toWrite } }" ^
  "finally { $fs.Dispose() }"

if errorlevel 1 (
  echo.
  echo [ERROR] Failed to generate file.
  pause
  exit /b 1
)

echo.
echo Done.
echo File: "%OUT%"
pause
exit /b 0

