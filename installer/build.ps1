# Builds dist\bro-setup.exe: publishes Bro (self-contained, so the target PC needs no .NET),
# zips it, and embeds the zip in the small installer.
#
#   powershell -ExecutionPolicy Bypass -File installer\build.ps1

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$dist = Join-Path $root 'dist'
$app = Join-Path $dist 'app'
$zip = Join-Path $dist 'payload.zip'

if (Test-Path $dist) { Remove-Item $dist -Recurse -Force }
New-Item -ItemType Directory -Force $dist | Out-Null

Write-Host '1/3 Publishing Bro...'
dotnet publish (Join-Path $root 'Bro.csproj') -c Release -r win-x64 --self-contained true -o $app `
    -p:DebugType=none -p:SatelliteResourceLanguages=en -nologo -v q
if ($LASTEXITCODE -ne 0) { throw 'publish failed' }

Write-Host '2/3 Packing...'
Add-Type -AssemblyName System.IO.Compression.FileSystem
[System.IO.Compression.ZipFile]::CreateFromDirectory($app, $zip, [System.IO.Compression.CompressionLevel]::Optimal, $false)

Write-Host '3/3 Building installer...'
$setupOut = Join-Path $dist 'setup-build'
Remove-Item (Join-Path $PSScriptRoot 'Setup\obj'), (Join-Path $PSScriptRoot 'Setup\bin') -Recurse -Force -ErrorAction SilentlyContinue
dotnet build (Join-Path $PSScriptRoot 'Setup\Setup.csproj') -c Release -o $setupOut "-p:PayloadZip=$zip" -nologo -v q
if ($LASTEXITCODE -ne 0) { throw 'installer build failed' }
Copy-Item (Join-Path $setupOut 'bro-setup.exe') (Join-Path $dist 'bro-setup.exe')

Remove-Item $setupOut, $app, $zip -Recurse -Force
$size = (Get-Item (Join-Path $dist 'bro-setup.exe')).Length / 1MB
Write-Host ("Done: dist\bro-setup.exe ({0:N0} MB)" -f $size)

