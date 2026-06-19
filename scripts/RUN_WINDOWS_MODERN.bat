@echo off
setlocal EnableExtensions
cd /d "%~dp0\.."

echo === Run Cyrus IPTV Modern Windows ===
where dotnet >nul 2>nul
if errorlevel 1 (
  echo .NET SDK is not installed. Install .NET SDK first.
  pause
  exit /b 1
)

dotnet --version

echo.
echo Restoring packages...
dotnet restore src\CyrusIptv.Windows\CyrusIptv.Windows.csproj -r win-x64 --nologo
if errorlevel 1 (
  echo Restore failed.
  pause
  exit /b 1
)

echo.
echo Starting modern Windows shell...
dotnet run --project src\CyrusIptv.Windows\CyrusIptv.Windows.csproj -c Debug -r win-x64 --no-restore
if errorlevel 1 (
  echo Modern Windows run failed.
  pause
  exit /b 1
)
