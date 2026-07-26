@echo off
setlocal EnableExtensions
cd /d "%~dp0\.."

echo === Build Classic Windows IPTV Player - Release Folder ===
where dotnet >nul 2>nul
if errorlevel 1 (
  echo .NET SDK is not installed. Install .NET SDK first.
  pause
  exit /b 1
)

if exist release\classic-windows-iptv-player rmdir /s /q release\classic-windows-iptv-player
mkdir release\classic-windows-iptv-player

dotnet publish src\ClassicWindowsIptvPlayer.Windows\ClassicWindowsIptvPlayer.Windows.csproj -c Release -r win-x64 --self-contained true -o release\classic-windows-iptv-player /p:PublishSingleFile=true /p:EnableCompressionInSingleFile=true --nologo
if errorlevel 1 (
  echo Publish failed.
  pause
  exit /b 1
)

powershell -NoProfile -ExecutionPolicy Bypass -File scripts\RepairWindowsLibVlc.ps1

echo.
echo Done. Release folder:
echo release\classic-windows-iptv-player
echo.
echo Keep the folder together. Do not copy only the EXE.
pause
