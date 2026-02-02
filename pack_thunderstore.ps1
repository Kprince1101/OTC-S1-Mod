param(
    [string]$Configuration = "Debug"
)

$ErrorActionPreference = "Stop"

$root = Split-Path -Parent $MyInvocation.MyCommand.Path
$tsDir = Join-Path $root "thunderstore"
$coreCs = Join-Path $root "OverTheCounter\Core.cs"

# Parse version from Core.cs MelonInfo attribute
$coreContent = Get-Content $coreCs -Raw
if ($coreContent -match 'MelonInfo\([^,]+,\s*"[^"]+",\s*"([^"]+)"') {
    $version = $Matches[1]
} else {
    Write-Host "ERROR: Could not parse version from Core.cs" -ForegroundColor Red
    exit 1
}

Write-Host "Version: $version" -ForegroundColor Cyan

$dll = Join-Path $root "OverTheCounter\bin\$Configuration\net6.0\OverTheCounter.dll"
$out = Join-Path $root "OverTheCounter-$version.zip"

if (-not (Test-Path $dll)) {
    Write-Host "ERROR: DLL not found at $dll" -ForegroundColor Red
    Write-Host "Run 'dotnet build --configuration $Configuration' first."
    exit 1
}

# Patch manifest.json version to match Core.cs
$manifestPath = Join-Path $tsDir "manifest.json"
$manifest = Get-Content $manifestPath -Raw | ConvertFrom-Json
if ($manifest.version_number -ne $version) {
    Write-Host "Updating manifest.json version: $($manifest.version_number) -> $version" -ForegroundColor Yellow
    $manifest.version_number = $version
    $manifest | ConvertTo-Json -Depth 10 | Set-Content $manifestPath -Encoding UTF8
}

# Stage files
$stage = Join-Path $root "_ts_stage"
if (Test-Path $stage) { Remove-Item $stage -Recurse -Force }

New-Item -ItemType Directory -Path $stage | Out-Null
New-Item -ItemType Directory -Path (Join-Path $stage "Mods") | Out-Null

Copy-Item (Join-Path $tsDir "icon.png")      (Join-Path $stage "icon.png")
Copy-Item $manifestPath                       (Join-Path $stage "manifest.json")
Copy-Item (Join-Path $tsDir "README.md")     (Join-Path $stage "README.md")
Copy-Item $dll                                (Join-Path $stage "Mods\OverTheCounter.dll")

Write-Host "Staged:" -ForegroundColor Cyan
Get-ChildItem $stage -Recurse | ForEach-Object {
    Write-Host ("  " + $_.FullName.Replace($stage, ""))
}

# Create zip
if (Test-Path $out) { Remove-Item $out -Force }
Compress-Archive -Path (Join-Path $stage "*") -DestinationPath $out -Force

# Cleanup
Remove-Item $stage -Recurse -Force

if (Test-Path $out) {
    $size = (Get-Item $out).Length
    Write-Host ""
    Write-Host "Created: $out ($size bytes)" -ForegroundColor Green
} else {
    Write-Host "ERROR: Failed to create archive." -ForegroundColor Red
    exit 1
}
