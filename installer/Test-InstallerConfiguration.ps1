$ErrorActionPreference = 'Stop'

$installerDirectory = Split-Path -Parent $MyInvocation.MyCommand.Path
$packagePath = Join-Path $installerDirectory 'PaqetFire.Installer\Package.wxs'
$projectPath = Join-Path $installerDirectory 'PaqetFire.Installer\PaqetFire.Installer.wixproj'

[xml]$package = Get-Content -Raw $packagePath
[xml]$project = Get-Content -Raw $projectPath

$wixNamespace = @{
    wix = 'http://wixtoolset.org/schemas/v4/wxs'
    util = 'http://wixtoolset.org/schemas/v4/wxs/util'
}

$checks = [ordered]@{
    DesktopDirectory = (Select-Xml -Xml $package -Namespace $wixNamespace -XPath "//wix:StandardDirectory[@Id='DesktopFolder']").Count -eq 1
    DesktopShortcut = (Select-Xml -Xml $package -Namespace $wixNamespace -XPath "//wix:File[@Id='PaqetFireDesktopExe']/wix:Shortcut[@Directory='DesktopFolder']").Count -eq 1
    CompletionDialog = (Select-Xml -Xml $package -Namespace $wixNamespace -XPath "//wix:UI/wix:Publish[@Dialog='ExitDialog' and @Control='Finish' and @Event='EndDialog' and @Value='Return']").Count -eq 1
    UiExtension = (Select-Xml -Xml $project -XPath "//PackageReference[@Include='WixToolset.UI.wixext']").Count -eq 1
    DesktopClosedForUpgrade = (Select-Xml -Xml $package -Namespace $wixNamespace -XPath "//util:CloseApplication[@Target='PaqetFire.Desktop.exe' and @CloseMessage='yes' and @ElevatedCloseMessage='yes' and @RebootPrompt='no' and @TerminateProcess='0' and contains(@Condition, 'WIX_UPGRADE_DETECTED')]").Count -eq 1
    DesktopClosedForUninstall = (Select-Xml -Xml $package -Namespace $wixNamespace -XPath "//util:CloseApplication[@Target='PaqetFire.Desktop.exe' and contains(@Condition, 'REMOVE~=') and contains(@Condition, 'ALL')]").Count -eq 1
    InUseProcessesAreForcedClosed = (Select-Xml -Xml $package -Namespace $wixNamespace -XPath "//wix:Property[@Id='MSIRMSHUTDOWN' and @Value='1']").Count -eq 1
    InUseProcessesAreNotRestarted = (Select-Xml -Xml $package -Namespace $wixNamespace -XPath "//wix:Property[@Id='MSIDISABLERMRESTART' and @Value='1']").Count -eq 1
    PaqetRestartResource = (Select-Xml -Xml $package -Namespace $wixNamespace -XPath "//util:RestartResource[@Path='[BrokerFolder]payload\engines\paqet\x64\paqet_windows_amd64.exe']").Count -eq 1
    XrayRestartResource = (Select-Xml -Xml $package -Namespace $wixNamespace -XPath "//util:RestartResource[@Path='[#PaqetFireXrayExe]']").Count -eq 1
    ProxiFyreRestartResource = (Select-Xml -Xml $package -Namespace $wixNamespace -XPath "//util:RestartResource[@Path='[#PaqetFireProxiFyreExe]']").Count -eq 1
    UtilExtension = (Select-Xml -Xml $project -XPath "//PackageReference[@Include='WixToolset.Util.wixext']").Count -eq 1
}

$failedChecks = @($checks.GetEnumerator() | Where-Object { -not $_.Value } | ForEach-Object Key)

if ($failedChecks.Count -gt 0) {
    Write-Error "Installer UX checks failed: $($failedChecks -join ', ')"
}

Write-Output 'PASS installer declares its UX and running-application lifecycle behavior.'
