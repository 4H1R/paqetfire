[CmdletBinding()]
param(
    [string]$DestinationRoot,
    [string]$WorkRoot
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$repositoryRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$manifestPath = Join-Path $repositoryRoot 'payload\payload-manifest.json'
$releaseAssetsPath = Join-Path $repositoryRoot 'payload\release-assets.json'

if ([string]::IsNullOrWhiteSpace($DestinationRoot)) {
    $DestinationRoot = Join-Path $repositoryRoot 'payload\engines'
}

if ([string]::IsNullOrWhiteSpace($WorkRoot)) {
    $WorkRoot = Join-Path ([IO.Path]::GetTempPath()) (
        'PaqetFire-payload-' + [guid]::NewGuid().ToString('N'))
}

$destinationRootPath = [IO.Path]::GetFullPath($DestinationRoot)
$workRootPath = [IO.Path]::GetFullPath($WorkRoot)
$temporaryRoot = [IO.Path]::GetFullPath([IO.Path]::GetTempPath())

if (-not $workRootPath.StartsWith($temporaryRoot, [StringComparison]::OrdinalIgnoreCase)) {
    throw "WorkRoot must stay beneath the system temporary directory: $temporaryRoot"
}

if (Test-Path -LiteralPath $destinationRootPath) {
    $existing = Get-ChildItem -LiteralPath $destinationRootPath -Force -ErrorAction Stop |
        Select-Object -First 1
    if ($null -ne $existing) {
        throw "DestinationRoot must be empty: $destinationRootPath"
    }
}

$manifest = Get-Content -LiteralPath $manifestPath -Raw | ConvertFrom-Json
$releaseAssets = Get-Content -LiteralPath $releaseAssetsPath -Raw | ConvertFrom-Json

if ($manifest.schemaVersion -ne '1' -or $releaseAssets.schemaVersion -ne '1') {
    throw 'Unsupported payload manifest or release-assets schema version.'
}

$downloadsRoot = Join-Path $workRootPath 'downloads'
$extractionRoot = Join-Path $workRootPath 'extracted'
$stagingRoot = Join-Path $workRootPath 'staged-engines'

try {
    New-Item -ItemType Directory -Path $downloadsRoot, $extractionRoot, $stagingRoot -Force |
        Out-Null

    foreach ($asset in $releaseAssets.assets) {
        $engine = @($manifest.engines | Where-Object engine -eq $asset.engine)
        if ($engine.Count -ne 1) {
            throw "Release asset '$($asset.engine)' does not map to exactly one manifest engine."
        }

        $engine = $engine[0]
        if ($engine.version -ne $asset.version) {
            throw "Version mismatch for $($asset.engine): release lock has '$($asset.version)' but manifest has '$($engine.version)'."
        }

        $assetUri = [uri]$asset.url
        if ($assetUri.Scheme -ne 'https' -or $assetUri.Host -ne 'github.com') {
            throw "Release asset URL must use HTTPS on github.com: $($asset.url)"
        }

        $archiveName = [IO.Path]::GetFileName($assetUri.AbsolutePath)
        if (-not $archiveName.EndsWith('.zip', [StringComparison]::OrdinalIgnoreCase)) {
            throw "Only ZIP release assets are supported: $archiveName"
        }

        $archivePath = Join-Path $downloadsRoot $archiveName
        Write-Host "Downloading $($asset.engine) $($asset.version)..."
        Invoke-WebRequest -Uri $assetUri -OutFile $archivePath

        $archiveHash = (Get-FileHash -LiteralPath $archivePath -Algorithm SHA256).Hash
        if ($archiveHash -ne $asset.sha256) {
            throw "Archive SHA-256 mismatch for $archiveName."
        }

        $engineExtractionRoot = Join-Path $extractionRoot $asset.engine
        Expand-Archive -LiteralPath $archivePath -DestinationPath $engineExtractionRoot

        foreach ($file in $engine.files) {
            $relativePath = $file.path -replace '^engines[/\\]', ''
            $leafName = [IO.Path]::GetFileName($relativePath)
            $matches = @(Get-ChildItem -LiteralPath $engineExtractionRoot -File -Recurse |
                Where-Object Name -eq $leafName)

            if ($matches.Count -ne 1) {
                throw "Expected exactly one '$leafName' in $archiveName; found $($matches.Count)."
            }

            $stagedPath = Join-Path $stagingRoot $relativePath
            New-Item -ItemType Directory -Path ([IO.Path]::GetDirectoryName($stagedPath)) -Force |
                Out-Null
            Copy-Item -LiteralPath $matches[0].FullName -Destination $stagedPath

            $stagedFile = Get-Item -LiteralPath $stagedPath
            if ($stagedFile.Length -ne [long]$file.size) {
                throw "Size mismatch for $($file.path)."
            }

            $stagedHash = (Get-FileHash -LiteralPath $stagedPath -Algorithm SHA256).Hash
            if ($stagedHash -ne $file.sha256) {
                throw "SHA-256 mismatch for $($file.path)."
            }
        }
    }

    New-Item -ItemType Directory -Path $destinationRootPath -Force | Out-Null
    Copy-Item -Path (Join-Path $stagingRoot '*') -Destination $destinationRootPath -Recurse
    Write-Host "Verified payload staged at $destinationRootPath"
}
finally {
    if (Test-Path -LiteralPath $workRootPath) {
        Remove-Item -LiteralPath $workRootPath -Recurse -Force
    }
}
