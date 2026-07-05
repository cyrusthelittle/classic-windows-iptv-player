@echo off
setlocal EnableExtensions
cd /d "%~dp0\.."

echo === Build Cyrus IPTV Modern Windows - Release Folder ===
where dotnet >nul 2>nul
if errorlevel 1 (
  echo .NET SDK is not installed. Install .NET SDK first.
  pause
  exit /b 1
)

if exist release\windows-modern rmdir /s /q release\windows-modern
mkdir release\windows-modern

dotnet publish src\CyrusIptv.Windows\CyrusIptv.Windows.csproj -c Release -r win-x64 --self-contained true -o release\windows-modern /p:PublishSingleFile=true /p:EnableCompressionInSingleFile=true --nologo
if errorlevel 1 (
  echo Publish failed.
  pause
  exit /b 1
)

powershell -NoProfile -ExecutionPolicy Bypass -File scripts\RepairWindowsLibVlc.ps1

echo.
echo Done. Release folder:
echo release\windows-modern
echo.
echo Keep the folder together. Do not copy only the EXE.
pause
