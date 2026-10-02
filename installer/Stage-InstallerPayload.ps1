[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string]$DesktopPublishDir,

    [Parameter(Mandatory = $true)]
    [string]$BrokerPublishDir,

    [Parameter(Mandatory = $true)]
    [string]$StageRoot,

    [Parameter(Mandatory = $true)]
    [string]$SharedFragmentPath
)

# The desktop app and the broker are both self-contained .NET publishes, so
# most of their root-level runtime files are byte-identical. MSI cabinets do not
# deduplicate by content, so this script splits the publish output into three
# trees and generates a WiX fragment that stores each shared file once under
# the desktop folder and duplicates it into the broker folder at install time
# (DuplicateFile table). The installed layout stays exactly the same.

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

function Get-FullDirectoryPath([string]$Path) {
    [IO.Path]::GetFullPath($Path).TrimEnd('\') + '\'
}

function Copy-StagedFile([string]$Source, [string]$Destination) {
    $directory = [IO.Path]::GetDirectoryName($Destination)
    if (-not (Test-Path -LiteralPath $directory)) {
        New-Item -ItemType Directory -Path $directory -Force | Out-Null
    }

    # Hard links keep staging cheap; fall back to a copy across volumes.
    try {
        New-Item -ItemType HardLink -Path $Destination -Target $Source -ErrorAction Stop | Out-Null
    }
    catch {
        Copy-Item -LiteralPath $Source -Destination $Destination
    }
}

function Copy-StagedTree([string]$SourceRoot, [string]$DestinationRoot, [hashtable]$SkipRootNames) {
    foreach ($file in Get-ChildItem -LiteralPath $SourceRoot -File -Recurse -Force) {
        $relativePath = $file.FullName.Substring($SourceRoot.Length)
        $isRootFile = $relativePath.IndexOf('\') -lt 0
        if ($isRootFile -and $SkipRootNames.ContainsKey($file.Name)) {
            continue
        }

        Copy-StagedFile $file.FullName (Join-Path $DestinationRoot $relativePath)
    }
}

function Get-Sha256([string]$Path) {
    $stream = [IO.File]::OpenRead($Path)
    $sha = [Security.Cryptography.SHA256]::Create()
    try {
        [BitConverter]::ToString($sha.ComputeHash($stream))
    }
    finally {
        $sha.Dispose()
        $stream.Dispose()
    }
}

$desktopRoot = Get-FullDirectoryPath $DesktopPublishDir
$brokerRoot = Get-FullDirectoryPath $BrokerPublishDir
$stageRootPath = Get-FullDirectoryPath $StageRoot

foreach ($required in @(
        (Join-Path $desktopRoot 'PaqetFire.Desktop.exe'),
        (Join-Path $brokerRoot 'PaqetFire.Broker.exe'))) {
    if (-not (Test-Path -LiteralPath $required)) {
        throw "Publish output is missing: $required"
    }
}

# Never share the entry points: the WiX authoring attaches the shortcut,
# service, and firewall metadata to their explicit File elements.
$neverShared = @{
    'PaqetFire.Desktop.exe' = $true
    'PaqetFire.Broker.exe' = $true
}

$sharedNames = @{}
foreach ($brokerFile in Get-ChildItem -LiteralPath $brokerRoot -File -Force) {
    if ($neverShared.ContainsKey($brokerFile.Name)) {
        continue
    }

    $desktopFile = Join-Path $desktopRoot $brokerFile.Name
    if (-not (Test-Path -LiteralPath $desktopFile -PathType Leaf)) {
        continue
    }

    if ((Get-Item -LiteralPath $desktopFile).Length -ne $brokerFile.Length) {
        continue
    }

    $desktopHash = Get-Sha256 $desktopFile
    $brokerHash = Get-Sha256 $brokerFile.FullName
    if ($desktopHash -eq $brokerHash) {
        $sharedNames[$brokerFile.Name] = $true
    }
}

if (Test-Path -LiteralPath $stageRootPath) {
    Remove-Item -LiteralPath $stageRootPath -Recurse -Force
}

$desktopStage = Join-Path $stageRootPath 'desktop'
$brokerStage = Join-Path $stageRootPath 'broker'
$sharedStage = Join-Path $stageRootPath 'shared'
New-Item -ItemType Directory -Path $desktopStage, $brokerStage, $sharedStage -Force | Out-Null

Copy-StagedTree $desktopRoot $desktopStage $sharedNames
Copy-StagedTree $brokerRoot $brokerStage $sharedNames

$sha = [Security.Cryptography.SHA256]::Create()
$components = New-Object System.Text.StringBuilder
$sharedBytes = 0L
try {
    foreach ($name in ($sharedNames.Keys | Sort-Object)) {
        $source = Join-Path $desktopRoot $name
        Copy-StagedFile $source (Join-Path $sharedStage $name)
        $sharedBytes += (Get-Item -LiteralPath $source).Length

        # Identifiers are derived from the file name so they stay stable across builds.
        $nameHash = $sha.ComputeHash([Text.Encoding]::UTF8.GetBytes($name.ToLowerInvariant()))
        $suffix = -join ($nameHash[0..9] | ForEach-Object { $_.ToString('x2') })
        $escapedName = [Security.SecurityElement]::Escape($name)
        [void]$components.AppendLine("      <Component Id=`"SharedRuntime_$suffix`">")
        [void]$components.AppendLine("        <File Id=`"SharedRuntimeFile_$suffix`" Source=`"`$(var.SharedPublishDir)\$escapedName`" KeyPath=`"yes`">")
        [void]$components.AppendLine("          <CopyFile Id=`"SharedRuntimeCopy_$suffix`" DestinationDirectory=`"BrokerFolder`" />")
        [void]$components.AppendLine('        </File>')
        [void]$components.AppendLine('      </Component>')
    }
}
finally {
    $sha.Dispose()
}

$fragment = @"
<?xml version="1.0" encoding="utf-8"?>
<!-- Generated by installer\Stage-InstallerPayload.ps1. Do not edit. -->
<Wix xmlns="http://wixtoolset.org/schemas/v4/wxs">
  <Fragment>
    <ComponentGroup Id="SharedRuntimePayload" Directory="ApplicationFolder">
$($components.ToString().TrimEnd())
    </ComponentGroup>
  </Fragment>
</Wix>
"@

$fragmentDirectory = [IO.Path]::GetDirectoryName([IO.Path]::GetFullPath($SharedFragmentPath))
New-Item -ItemType Directory -Path $fragmentDirectory -Force | Out-Null
[IO.File]::WriteAllText([IO.Path]::GetFullPath($SharedFragmentPath), $fragment, (New-Object Text.UTF8Encoding($false)))

Write-Host ("Shared {0} runtime files ({1:N1} MB) between the desktop and broker payloads." -f $sharedNames.Count, ($sharedBytes / 1MB))
