<#
.SYNOPSIS
    Builds the release package Smart Lab's updater knows how to install.

.DESCRIPTION
    Publishes a self-contained win-x64 build, zips it, and writes the checksum list
    beside it. The zip name carries "win-x64" and the checksum file is called
    SHA256SUMS.txt, because those are what UpdatePackage.SelectPackage and
    SelectChecksums look for - the updater refuses a release it cannot verify.

    Self-contained on purpose: a stick doctor is a tool people carry to machines that
    are already in trouble, and "install the .NET runtime first" is a poor thing to
    read on one of them.

.PARAMETER Version
    Must match MainViewModel.AppVersion, which the release notes test already pins to
    the newest entry in AboutViewModel.ReleaseNotes.
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string] $Version,

    [string] $OutputDirectory
)

$ErrorActionPreference = 'Stop'

# Resolved here rather than as a parameter default: $PSScriptRoot is not populated
# during parameter binding on every host, and the difference is a 67 MB package
# written to the root of C:.
$scriptRoot = Split-Path -Parent $MyInvocation.MyCommand.Path
$root = Resolve-Path (Join-Path $scriptRoot '..')

if (-not $OutputDirectory) { $OutputDirectory = Join-Path $root 'artifacts' }
$project = Join-Path $root 'src\SmartLab.App\SmartLab.App.csproj'

# Guard rather than trust: a package whose version does not match what the build
# reports would tell every installed copy it is out of date, for ever.
$declared = Select-String -Path (Join-Path $root 'src\SmartLab.App\MainViewModel.cs') `
    -Pattern 'AppVersion = "([^"]+)"' | ForEach-Object { $_.Matches[0].Groups[1].Value }

if ($declared -ne $Version) {
    throw "MainViewModel.AppVersion is '$declared' but this build was asked for '$Version'."
}

$stage = Join-Path $OutputDirectory "SmartLab-$Version-win-x64"
$zip = "$stage.zip"
$sums = Join-Path $OutputDirectory 'SHA256SUMS.txt'

if (Test-Path $stage) { Remove-Item $stage -Recurse -Force }
if (Test-Path $zip) { Remove-Item $zip -Force }

New-Item -ItemType Directory -Force $OutputDirectory | Out-Null

Write-Host "Publishing $Version..."

dotnet publish $project `
    -c Release `
    -r win-x64 `
    --self-contained true `
    -p:PublishSingleFile=false `
    -p:DebugType=none `
    -p:Version=$Version `
    -o $stage

if ($LASTEXITCODE -ne 0) { throw "publish failed with exit code $LASTEXITCODE" }

# The elevated worker ships beside the app: without it every repair that needs
# Administrator reports that it could not start one.
$worker = Join-Path $root 'src\SmartLab.Worker\SmartLab.Worker.csproj'

dotnet publish $worker `
    -c Release `
    -r win-x64 `
    --self-contained true `
    -p:PublishSingleFile=false `
    -p:DebugType=none `
    -p:Version=$Version `
    -o $stage

if ($LASTEXITCODE -ne 0) { throw "worker publish failed with exit code $LASTEXITCODE" }

Write-Host "Compressing..."
Compress-Archive -Path $stage -DestinationPath $zip -CompressionLevel Optimal

$hash = (Get-FileHash -Path $zip -Algorithm SHA256).Hash.ToLowerInvariant()
"$hash  $(Split-Path $zip -Leaf)" | Set-Content -Path $sums -Encoding ascii

Remove-Item $stage -Recurse -Force

$size = [Math]::Round((Get-Item $zip).Length / 1MB, 1)

Write-Host ""
Write-Host "  $zip  ($size MB)"
Write-Host "  $sums"
Write-Host "  sha256 $hash"
