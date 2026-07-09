# deploy.ps1
#
# Builds MartianGamesAlerts as a self-contained single-file exe, zips it,
# uploads both to the martiangames web root, and re-runs the manifest
# builder so the new build shows up under /pc/manifest.json.
#
# Requires:
#   * dotnet SDK on PATH
#   * OpenSSH client on PATH (`ssh`, `scp`)
#   * SSH key auth to `martiangames` set up so no prompts appear.
#     BatchMode=yes makes ssh/scp fail fast if keys aren't picked up.

$ErrorActionPreference = 'Stop'

# ---- Config ----
$Project      = 'MartianGamesAlerts.csproj'
$ProductId    = 'MartianGamesAlerts'
$Runtime      = 'win-x64'
$Config       = 'Release'
$Tfm          = 'net10.0-windows'
$RemoteHost   = 'martiangames'
$RemotePcDir  = '/var/www/html/pc'
$RemoteScript = '/usr/local/bin/manifest.sh'

# ---- Locate project ----
Set-Location $PSScriptRoot
$PublishDir = Join-Path $PSScriptRoot "bin\$Config\$Tfm\$Runtime\publish"
$ExePath    = Join-Path $PublishDir   "$ProductId.exe"
$ZipPath    = Join-Path $PSScriptRoot "$ProductId.zip"

# ---- Publish ----
Write-Host "==> dotnet publish" -ForegroundColor Cyan
dotnet publish $Project `
    -c $Config `
    -r $Runtime `
    --self-contained `
    -p:PublishSingleFile=true
if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed (exit $LASTEXITCODE)" }
if (-not (Test-Path $ExePath)) { throw "Expected $ExePath after publish; not found." }
Write-Host ("    exe: {0:N1} MB" -f ((Get-Item $ExePath).Length / 1MB))

# ---- Zip (flat: exe at zip root) ----
Write-Host "==> Zipping" -ForegroundColor Cyan
if (Test-Path $ZipPath) { Remove-Item $ZipPath -Force }
Compress-Archive -Path $ExePath -DestinationPath $ZipPath -CompressionLevel Optimal
Write-Host ("    zip: {0:N1} MB" -f ((Get-Item $ZipPath).Length / 1MB))

# ---- Upload ----
$RemoteZip = "$($RemoteHost):$RemotePcDir/$ProductId.zip"
$RemoteExe = "$($RemoteHost):$RemotePcDir/$ProductId.exe"

Write-Host "==> scp zip -> $RemoteZip" -ForegroundColor Cyan
& scp -o BatchMode=yes -q $ZipPath $RemoteZip
if ($LASTEXITCODE -ne 0) { throw "scp of zip failed (exit $LASTEXITCODE)" }

Write-Host "==> scp exe -> $RemoteExe" -ForegroundColor Cyan
& scp -o BatchMode=yes -q $ExePath $RemoteExe
if ($LASTEXITCODE -ne 0) { throw "scp of exe failed (exit $LASTEXITCODE)" }

# ---- Rebuild manifest ----
Write-Host "==> ssh $RemoteHost $RemoteScript" -ForegroundColor Cyan
& ssh -o BatchMode=yes $RemoteHost $RemoteScript
if ($LASTEXITCODE -ne 0) { throw "manifest rebuild failed (exit $LASTEXITCODE)" }

Write-Host "==> Deployed." -ForegroundColor Green