<#
.SYNOPSIS
  Publishes OpenShelf for Windows and packs the Velopack installer, portable zip and update feed.

.EXAMPLE
  .\windows\scripts\package.ps1 -Version 0.1.0

.DESCRIPTION
  Output goes to windows\.artifacts\release:
    OpenShelf-win-Setup.exe             installer (per-user, no admin)        [x64]
    OpenShelf-win-Portable.zip          portable build                        [x64]
    OpenShelfApp-<version>-full.nupkg  full update package (pack id OpenShelfApp)
    OpenShelfApp-<version>-delta.nupkg delta from the previous release, when one is in the folder
    releases.win.json, RELEASES-win     the update feed the app reads
  With -Arch arm64 or both, the arm64 build uses its own channel, so the files are named *-win-arm64-* and
  the feed is releases.win-arm64.json. An installed copy only ever follows its own channel.

  Code signing is off unless -SignParams (signtool arguments) or -AzureSignFile (Azure Trusted Signing
  metadata.json) is given, or the matching OPENSHELF_SIGN_PARAMS / OPENSHELF_AZURE_SIGN_FILE variable is set.
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [ValidatePattern('^\d+\.\d+\.\d+(-[0-9A-Za-z.-]+)?$')]
    [string]$Version,

    [ValidateSet('x64', 'arm64', 'both')]
    [string]$Arch = 'x64',

    [string]$SignParams = $env:OPENSHELF_SIGN_PARAMS,
    [string]$AzureSignFile = $env:OPENSHELF_AZURE_SIGN_FILE,

    # Markdown file shown as the release notes in the update feed.
    [string]$ReleaseNotes
)

$ErrorActionPreference = 'Stop'
$windows = Split-Path -Parent $PSScriptRoot
$project = Join-Path $windows 'OpenShelf.App\OpenShelf.App.csproj'
$icon = Join-Path $windows 'OpenShelf.App\Assets\OpenShelf.ico'
$artifacts = Join-Path $windows '.artifacts'
$buildRoot = Join-Path $artifacts 'release-build'
$out = Join-Path $artifacts 'release'
$toolDir = Join-Path $artifacts 'tools'

# vpk must match the Velopack library the app was built with.
$veloVersion = ([xml](Get-Content (Join-Path $windows 'OpenShelf.Update\OpenShelf.Update.csproj'))).Project.ItemGroup.PackageReference |
    Where-Object { $_.Include -eq 'Velopack' } | ForEach-Object { $_.Version } | Select-Object -First 1
if (-not $veloVersion) { throw 'Could not read the Velopack version from OpenShelf.Update.csproj.' }

function Get-Vpk {
    $exe = Join-Path $toolDir 'vpk.exe'
    $have = if (Test-Path $exe) { (& $exe --version 2>$null | Select-Object -First 1) } else { $null }
    if ($have -notmatch [regex]::Escape($veloVersion)) {
        Write-Host "Installing vpk $veloVersion into $toolDir"
        if (Test-Path $toolDir) { Remove-Item $toolDir -Recurse -Force }
        dotnet tool install vpk --tool-path $toolDir --version $veloVersion | Out-Host
        if ($LASTEXITCODE -ne 0) { throw 'Could not install vpk.' }
    }
    return $exe
}

function Invoke-Native([string]$Exe, [string[]]$Arguments) {
    & $Exe @Arguments
    if ($LASTEXITCODE -ne 0) { throw "$([IO.Path]::GetFileName($Exe)) failed with exit code $LASTEXITCODE" }
}

# The pack id is also the install folder (%LOCALAPPDATA%\<id>), and Velopack deletes that whole folder on uninstall.
# OpenShelf keeps logs, models and downloaded tools in %LOCALAPPDATA%\OpenShelf, so the id must differ from that.
$packId = 'OpenShelfApp'

$vpk = Get-Vpk
New-Item -ItemType Directory -Force $out | Out-Null

$targets = switch ($Arch) {
    'x64'   { @(@{ Rid = 'win-x64';   Channel = 'win' }) }
    'arm64' { @(@{ Rid = 'win-arm64'; Channel = 'win-arm64' }) }
    'both'  { @(@{ Rid = 'win-x64';   Channel = 'win' }, @{ Rid = 'win-arm64'; Channel = 'win-arm64' }) }
}

foreach ($t in $targets) {
    $publish = Join-Path $buildRoot "publish-$($t.Rid)"
    if (Test-Path $publish) { Remove-Item $publish -Recurse -Force }

    Write-Host "== Publishing $($t.Rid) $Version"
    Invoke-Native dotnet @(
        'publish', $project, '-c', 'Release', '-r', $t.Rid, '--self-contained',
        '--artifacts-path', $buildRoot, '-o', $publish,
        "-p:Version=$Version", '-p:DebugType=None', '-p:DebugSymbols=false')

    $pack = @(
        'pack',
        '--packId', $packId,
        '--packVersion', $Version,
        '--packTitle', 'OpenShelf',
        '--packAuthors', 'OpenShelf',
        '--packDir', $publish,
        '--mainExe', 'OpenShelf.exe',
        '--runtime', $t.Rid,
        '--channel', $t.Channel,
        '--icon', $icon,
        '--shortcuts', 'StartMenuRoot',
        '--outputDir', $out)
    if ($ReleaseNotes) { $pack += @('--releaseNotes', (Resolve-Path $ReleaseNotes).Path) }
    if ($SignParams) { $pack += @('--signParams', $SignParams) }
    elseif ($AzureSignFile) { $pack += @('--azureTrustedSignFile', $AzureSignFile) }
    else { Write-Host 'Not signing (no signing parameters given).' }

    Write-Host "== Packing $($t.Rid) (channel $($t.Channel))"
    Invoke-Native $vpk $pack

    # Give the installer and the portable zip the friendly name (OpenShelf-win-Setup.exe) and keep the feed's asset list in step.
    $assetsFile = Join-Path $out "assets.$($t.Channel).json"
    $assets = Get-Content $assetsFile -Raw | ConvertFrom-Json
    foreach ($a in $assets) {
        if ($a.Type -in 'Installer', 'Portable') {
            $friendly = $a.RelativeFileName -replace "^$packId-", 'OpenShelf-'
            Move-Item (Join-Path $out $a.RelativeFileName) (Join-Path $out $friendly) -Force
            $a.RelativeFileName = $friendly
        }
    }
    [IO.File]::WriteAllText($assetsFile, (ConvertTo-Json -InputObject @($assets) -Compress), (New-Object Text.UTF8Encoding $false))
}

Write-Host ''
Write-Host "Release files in ${out}:"
Get-ChildItem $out -File | Sort-Object Name | ForEach-Object { '{0,12:N0}  {1}' -f $_.Length, $_.Name }
