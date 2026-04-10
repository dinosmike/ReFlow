@echo off
setlocal

set "ROOT=%~dp0"
set "PUBLISH=%ROOT%publish"

cd /d "%ROOT%ReFlow"

taskkill /IM ReFlow.exe /F >nul 2>&1
ping -n 2 127.0.0.1 >nul

REM Без DeleteExistingFiles=true — в .\publish могут лежать ваши файлы рядом с exe.
dotnet publish "%ROOT%ReFlow\ReFlow.csproj" -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:EnableCompressionInSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:DebugType=None --nologo -v q -o "%PUBLISH%"
if errorlevel 1 (
    echo BUILD FAILED
    pause
    exit /b 1
)

set "EXE=%PUBLISH%\ReFlow.exe"

if not exist "%EXE%" (
    echo ERROR: %EXE% not found
    pause
    exit /b 1
)

start "" "%EXE%"
