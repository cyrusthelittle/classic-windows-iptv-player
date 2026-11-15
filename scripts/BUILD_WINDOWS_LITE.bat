@echo off
setlocal EnableExtensions
cd /d "%~dp0\.."

set "OUT=release\lite"

echo === Build Classic Windows IPTV Player - Lite ===
where dotnet >nul 2>nul
if errorlevel 1 (
  echo .NET SDK is not installed. Install .NET SDK first.
  pause
  exit /b 1
)

REM Preserve portable user data when rebuilding an existing release folder.
if not exist "%OUT%" mkdir "%OUT%"

dotnet publish src\ClassicWindowsIptvPlayer.Windows\ClassicWindowsIptvPlayer.Windows.csproj -c Release -r win-x64 --self-contained false -o "%OUT%" /p:BundleVlc=false /p:PublishSingleFile=false --nologo
if errorlevel 1 (
  echo Publish failed.
  pause
  exit /b 1
)

REM Ship the shared free-to-air accounts. Seeded from the tracked fixture, so a
REM fresh build has working channels on first launch. Users can delete or replace
REM accounts.json; the app recreates it on next sign-in.
if not exist "%OUT%\accounts" mkdir "%OUT%\accounts"
if not exist "%OUT%\accounts\accounts.json" if not exist "%OUT%\accounts.json" copy /y fixtures\accounts.json "%OUT%\accounts\accounts.json" >nul

echo.
echo Done. Release folder:
echo %OUT%
echo Requires .NET 10 Windows Desktop Runtime and 64-bit VLC 3.
echo.
echo Keep the folder together. Do not copy only the EXE.
pause
