@echo off
setlocal EnableExtensions
cd /d "%~dp0\.."

echo === Build CyrusIptv.Core ===
dotnet build src\CyrusIptv.Core\CyrusIptv.Core.csproj -c Release
if errorlevel 1 (
  echo Core build failed.
  pause
  exit /b 1
)
echo Done.
pause
