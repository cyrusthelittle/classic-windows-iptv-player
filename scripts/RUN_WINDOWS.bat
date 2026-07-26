@echo off
setlocal EnableExtensions
cd /d "%~dp0\.."

echo === Run Classic Windows IPTV Player ===
where dotnet >nul 2>nul
if errorlevel 1 (
  echo .NET SDK is not installed. Install .NET SDK first.
  pause
  exit /b 1
)

dotnet --version

echo.
echo Restoring packages...
dotnet restore src\ClassicWindowsIptvPlayer.Windows\ClassicWindowsIptvPlayer.Windows.csproj -r win-x64 --nologo
if errorlevel 1 (
  echo Restore failed.
  pause
  exit /b 1
)

echo.
echo Starting Classic Windows IPTV Player...
dotnet run --project src\ClassicWindowsIptvPlayer.Windows\ClassicWindowsIptvPlayer.Windows.csproj -c Debug -r win-x64 --no-restore
if errorlevel 1 (
  echo Classic Windows IPTV Player run failed.
  pause
  exit /b 1
)
