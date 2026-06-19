@echo off
setlocal EnableExtensions
cd /d "%~dp0\.."

echo === Build Cyrus IPTV Modern Windows - Fast ===
where dotnet >nul 2>nul
if errorlevel 1 (
  echo .NET SDK is not installed. Install .NET SDK first.
  pause
  exit /b 1
)

dotnet restore src\CyrusIptv.Windows\CyrusIptv.Windows.csproj -r win-x64 --nologo
if errorlevel 1 (
  echo Restore failed.
  pause
  exit /b 1
)

dotnet build src\CyrusIptv.Windows\CyrusIptv.Windows.csproj -c Release -r win-x64 --no-restore --nologo
if errorlevel 1 (
  echo Build failed.
  pause
  exit /b 1
)

powershell -NoProfile -ExecutionPolicy Bypass -File scripts\RepairWindowsLibVlc.ps1 -Configuration Release

echo.
echo Done. Fast modern Windows EXE:
echo src\CyrusIptv.Windows\bin\Release\net10.0-windows\win-x64\Cyrus IPTV Modern.exe
echo.
echo Keep the EXE together with libvlc.dll, libvlccore.dll, and plugins folder.
pause
