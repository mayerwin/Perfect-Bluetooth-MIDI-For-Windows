@echo off
REM =====================================================================
REM  Perfect Bluetooth MIDI For Windows - single-command build script.
REM
REM  Prereqs (one-time):
REM    1. Install the .NET 10 SDK:   winget install Microsoft.DotNet.SDK.10
REM    2. Create a Windows MIDI Services loopback endpoint (see README).
REM
REM  Output: dist\PerfectBluetoothMidi.exe
REM    Self-contained, trimmed, compressed single-file Windows x64 exe
REM    (~22 MB). The .NET 10 runtime IS bundled, so the exe runs on a
REM    machine with no .NET installed at all.
REM
REM    Why self-contained: both PublishTrimmed and EnableCompressionInSingleFile
REM    are only supported for self-contained publishes (the SDK rejects them
REM    otherwise with NETSDK1102 / NETSDK1176). Publishing framework-dependent
REM    therefore means dropping BOTH, which takes the exe from ~22 MB to
REM    ~53 MB *and* still requires the user to install the runtime. Bundling
REM    is smaller and simpler on both counts.
REM =====================================================================

setlocal
pushd "%~dp0"

where dotnet >nul 2>nul
if errorlevel 1 (
    echo [ERROR] 'dotnet' not found on PATH.
    echo        Install the .NET 10 SDK first:   winget install Microsoft.DotNet.SDK.10
    popd & exit /b 1
)

REM If a previous build of the app is running (often minimized to the tray),
REM the single-file publisher can't overwrite dist\PerfectBluetoothMidi.exe.
REM /F forces termination; errors suppressed if it wasn't running.
taskkill /IM PerfectBluetoothMidi.exe /F >nul 2>&1

echo Restoring packages...
dotnet restore PerfectBluetoothMidi\PerfectBluetoothMidi.csproj || goto :fail

echo Publishing single-file exe...
dotnet publish PerfectBluetoothMidi\PerfectBluetoothMidi.csproj ^
    -c Release ^
    -r win-x64 ^
    --self-contained true ^
    -p:PublishSingleFile=true ^
    -p:IncludeNativeLibrariesForSelfExtract=true ^
    -p:DebugType=embedded ^
    -o dist || goto :fail

echo.
echo =====================================================================
echo  Build OK. Run:  dist\PerfectBluetoothMidi.exe
echo =====================================================================
popd
exit /b 0

:fail
echo.
echo [ERROR] Build failed.
popd
exit /b 1
