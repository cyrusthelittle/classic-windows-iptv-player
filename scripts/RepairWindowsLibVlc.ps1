$ErrorActionPreference = "Stop"
$repoRoot = Split-Path -Parent $PSScriptRoot
$packageRoot = Join-Path $env:USERPROFILE ".nuget\packages\videolan.libvlc.windows"
$outputCandidates = @(
  (Join-Path $repoRoot "release\windows-modern")
)

function Test-CompleteLibVlcFolder([string]$Folder) {
  if (-not (Test-Path $Folder)) { return $false }
  return (Test-Path (Join-Path $Folder "libvlc.dll")) -and
         (Test-Path (Join-Path $Folder "libvlccore.dll")) -and
         (Test-Path (Join-Path $Folder "plugins"))
}

Write-Host "Checking bundled LibVLC files..."
Write-Host "Package root: $packageRoot"

if (-not (Test-Path $packageRoot)) {
  Write-Warning "VideoLAN.LibVLC.Windows package was not found in NuGet cache yet. Run dotnet restore first."
  exit 0
}

$libFiles = Get-ChildItem -Path $packageRoot -Filter "libvlc.dll" -Recurse -ErrorAction SilentlyContinue
if (-not $libFiles -or $libFiles.Count -eq 0) {
  Write-Warning "libvlc.dll was not found inside the VideoLAN.LibVLC.Windows NuGet package."
  exit 0
}

$sourceDir = $libFiles |
  Where-Object { Test-CompleteLibVlcFolder $_.DirectoryName } |
  Sort-Object @{ Expression = { if ($_.DirectoryName -match "\\build\\x64$|\\win-x64\\|\\x64$") { 3 } elseif ($_.DirectoryName -match "x64") { 2 } else { 1 } }; Descending = $true }, FullName -Descending |
  Select-Object -First 1 |
  ForEach-Object { $_.DirectoryName }

if (-not $sourceDir) {
  Write-Warning "A complete x64 LibVLC folder was not found. Need libvlc.dll, libvlccore.dll, and plugins folder."
  exit 0
}

Write-Host "Using complete LibVLC source: $sourceDir"

foreach ($outputDir in $outputCandidates) {
  if (-not (Test-Path $outputDir)) { continue }

  Write-Host "Repairing output folder: $outputDir"

  foreach ($file in Get-ChildItem -Path $sourceDir -File -ErrorAction SilentlyContinue) {
    Copy-Item $file.FullName -Destination (Join-Path $outputDir $file.Name) -Force
  }

  foreach ($dir in Get-ChildItem -Path $sourceDir -Directory -ErrorAction SilentlyContinue) {
    $dest = Join-Path $outputDir $dir.Name
    if (Test-Path $dest) { Remove-Item $dest -Recurse -Force }
    Copy-Item $dir.FullName -Destination $dest -Recurse -Force
  }

  Write-Host "Output complete: $(Test-CompleteLibVlcFolder $outputDir)"
  Write-Host " - libvlc.dll: $(Test-Path (Join-Path $outputDir 'libvlc.dll'))"
  Write-Host " - libvlccore.dll: $(Test-Path (Join-Path $outputDir 'libvlccore.dll'))"
  Write-Host " - plugins: $(Test-Path (Join-Path $outputDir 'plugins'))"
}
