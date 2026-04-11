@echo off
setlocal

set "ROOT=%~dp0"
set "PUBLISH=%ROOT%publish"

cd /d "%ROOT%ReFlow"

set NO_PAUSE=0
set NO_START=0
if /i "%~1"=="nopause" set NO_PAUSE=1
if /i "%~2"=="nopause" set NO_PAUSE=1
if /i "%~1"=="nostart" set NO_START=1
if /i "%~2"=="nostart" set NO_START=1

REM Дочерние процессы тоже; иначе publish не может перезаписать ReFlow.exe в publish\
taskkill /IM ReFlow.exe /F /T >nul 2>&1
ping -n 4 127.0.0.1 >nul
if exist "%PUBLISH%\ReFlow.exe" del /f /q "%PUBLISH%\ReFlow.exe" >nul 2>&1

REM Без DeleteExistingFiles=true — в .\publish могут лежать ваши файлы рядом с exe.
dotnet publish "%ROOT%ReFlow\ReFlow.csproj" -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:EnableCompressionInSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:DebugType=None --nologo -v q -o "%PUBLISH%"
if errorlevel 1 (
    echo BUILD FAILED
    if "%NO_PAUSE%"=="0" pause
    exit /b 1
)

set "EXE=%PUBLISH%\ReFlow.exe"

if not exist "%EXE%" (
    echo ERROR: %EXE% not found
    if "%NO_PAUSE%"=="0" pause
    exit /b 1
)

if "%NO_START%"=="0" start "" "%EXE%"
