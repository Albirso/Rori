param(
    [string]$IdentityName = "Rori.Local",
    [string]$Publisher = "CN=Rori",
    [string]$PublisherDisplayName = "Rori",
    [string]$Version = "1.0.2.0",
    [Parameter(Mandatory = $true)][string]$Ffmpeg,
    [Parameter(Mandatory = $true)][string]$ToolsDir,
    [string]$LicenseDir = ""
)
$ErrorActionPreference = "Stop"
$root = Split-Path $PSScriptRoot -Parent
$out = Join-Path $root "release"
$layout = Join-Path $out "msix-layout"
if (Test-Path $layout) { Remove-Item $layout -Recurse -Force }
New-Item -ItemType Directory -Force $layout | Out-Null

dotnet publish (Join-Path $root "Rori.csproj") -c Release -o (Join-Path $root "publish") | Out-Null
if ($LASTEXITCODE -ne 0) { throw "publish failed" }

Copy-Item (Join-Path $root "publish\Rori.exe") $layout
Copy-Item $Ffmpeg (Join-Path $layout "ffmpeg.exe")
if ($LicenseDir -and (Test-Path $LicenseDir)) { Copy-Item $LicenseDir (Join-Path $layout "ffmpeg-license") -Recurse }
Copy-Item (Join-Path $PSScriptRoot "Assets") (Join-Path $layout "Assets") -Recurse

$manifest = Get-Content (Join-Path $PSScriptRoot "AppxManifest.xml") -Raw
$manifest = $manifest.Replace('$IdentityName$', $IdentityName).Replace('$Publisher$', [Security.SecurityElement]::Escape($Publisher)).Replace('$PublisherDisplayName$', [Security.SecurityElement]::Escape($PublisherDisplayName)).Replace('$Version$', $Version)
[IO.File]::WriteAllText((Join-Path $layout "AppxManifest.xml"), $manifest, (New-Object Text.UTF8Encoding $false))

$pri = Join-Path $out "priconfig.xml"
& (Join-Path $ToolsDir "makepri.exe") createconfig /cf $pri /dq en-US /pv 10.0.0 /o | Out-Null
& (Join-Path $ToolsDir "makepri.exe") new /pr $layout /cf $pri /mn (Join-Path $layout "AppxManifest.xml") /of (Join-Path $layout "resources.pri") /o | Out-Null
if ($LASTEXITCODE -ne 0) { throw "makepri failed" }
Remove-Item $pri

$msix = Join-Path $out "Rori-$Version-x64.msix"
& (Join-Path $ToolsDir "makeappx.exe") pack /d $layout /p $msix /o | Out-Null
if ($LASTEXITCODE -ne 0) { throw "makeappx failed" }
Write-Output $msix
