@echo off
setlocal EnableExtensions
cd /d "%~dp0\.."

set "VARIANT=%~1"
if "%VARIANT%"=="" set "VARIANT=full"
if /i not "%VARIANT%"=="light" if /i not "%VARIANT%"=="full" (
  echo Usage: BUILD_WINDOWS_RELEASE.bat [light^|full]
  exit /b 1
)
set "OUT=release\%VARIANT%"
set "SELFCONTAINED=true"
set "BUNDLE=true"
if /i "%VARIANT%"=="light" (
  set "SELFCONTAINED=false"
  set "BUNDLE=false"
)

echo === Build Classic Windows IPTV Player - Release Folder ===
where dotnet >nul 2>nul
if errorlevel 1 (
  echo .NET SDK is not installed. Install .NET SDK first.
  pause
  exit /b 1
)

REM Preserve portable user data when rebuilding an existing release folder.
if not exist "%OUT%" mkdir "%OUT%"

dotnet publish src\ClassicWindowsIptvPlayer.Windows\ClassicWindowsIptvPlayer.Windows.csproj -c Release -r win-x64 --self-contained %SELFCONTAINED% -o "%OUT%" /p:BundleVlc=%BUNDLE% /p:PublishSingleFile=false --nologo
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



REM The native NuGet publish includes the complete bundled runtime for full.
REM Do not run the repair script here: light intentionally omits native VLC.

echo.
echo Done. Release folder:
echo %OUT%
if /i "%VARIANT%"=="light" echo Requires .NET 10 Windows Desktop Runtime and 64-bit VLC 3.
echo.
echo Keep the folder together. Do not copy only the EXE.
pause
