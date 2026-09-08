$ErrorActionPreference = 'Stop'

$installerDirectory = Split-Path -Parent $MyInvocation.MyCommand.Path
$packagePath = Join-Path $installerDirectory 'PaqetFire.Installer\Package.wxs'
$projectPath = Join-Path $installerDirectory 'PaqetFire.Installer\PaqetFire.Installer.wixproj'

[xml]$package = Get-Content -Raw $packagePath
[xml]$project = Get-Content -Raw $projectPath

$wixNamespace = @{ wix = 'http://wixtoolset.org/schemas/v4/wxs' }

$checks = [ordered]@{
    DesktopDirectory = (Select-Xml -Xml $package -Namespace $wixNamespace -XPath "//wix:StandardDirectory[@Id='DesktopFolder']").Count -eq 1
    DesktopShortcut = (Select-Xml -Xml $package -Namespace $wixNamespace -XPath "//wix:File[@Id='PaqetFireDesktopExe']/wix:Shortcut[@Directory='DesktopFolder']").Count -eq 1
    CompletionDialog = (Select-Xml -Xml $package -Namespace $wixNamespace -XPath "//wix:UI/wix:Publish[@Dialog='ExitDialog' and @Control='Finish' and @Event='EndDialog' and @Value='Return']").Count -eq 1
    UiExtension = (Select-Xml -Xml $project -XPath "//PackageReference[@Include='WixToolset.UI.wixext']").Count -eq 1
}

$failedChecks = @($checks.GetEnumerator() | Where-Object { -not $_.Value } | ForEach-Object Key)

if ($failedChecks.Count -gt 0) {
    Write-Error "Installer UX checks failed: $($failedChecks -join ', ')"
}

Write-Output 'PASS installer declares a Desktop shortcut and a completion dialog.'
