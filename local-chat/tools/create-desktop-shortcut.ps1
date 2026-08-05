[CmdletBinding()]
param(
    [string]$Aumid = '7D1F07DC-0090-4570-868B-B0D5D40CFB40_1z32rh13vfry6!App',
    [string]$ShortcutName = 'Qwen Local'
)

$ErrorActionPreference = 'Stop'

$root = Split-Path -Parent $PSScriptRoot
$iconPath = Join-Path $root 'src\QwenLocalChat.WinUI\Assets\AppIcon.ico'
$desktop = [Environment]::GetFolderPath('Desktop')
$shortcutPath = Join-Path $desktop ($ShortcutName + '.lnk')
$explorerPath = Join-Path $env:SystemRoot 'explorer.exe'

if (-not (Test-Path -LiteralPath $iconPath -PathType Leaf)) {
    throw "Canonical icon was not found: $iconPath"
}
if (-not (Test-Path -LiteralPath $explorerPath -PathType Leaf)) {
    throw "Explorer launcher was not found: $explorerPath"
}

$startApp = @(Get-StartApps | Where-Object { $_.AppID -eq $Aumid })
if ($startApp.Count -ne 1) {
    throw "Registered packaged app was not found for AUMID '$Aumid'. Build and register the WinUI package first."
}

$shell = New-Object -ComObject WScript.Shell
$shortcut = $shell.CreateShortcut($shortcutPath)
$shortcut.TargetPath = $explorerPath
$shortcut.Arguments = "shell:AppsFolder\$Aumid"
$shortcut.WorkingDirectory = $root
$shortcut.IconLocation = "$iconPath,0"
$shortcut.Description = 'Open the local Qwen chat window'
$shortcut.Save()

$check = $shell.CreateShortcut($shortcutPath)
if ($check.TargetPath -ne $explorerPath -or $check.Arguments -ne "shell:AppsFolder\$Aumid") {
    throw "Desktop shortcut verification failed: $shortcutPath"
}

[ordered]@{
    shortcut = $shortcutPath
    target = $check.TargetPath
    arguments = $check.Arguments
    icon = $check.IconLocation
    aumid = $Aumid
} | ConvertTo-Json -Compress
