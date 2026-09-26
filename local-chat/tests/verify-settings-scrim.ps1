[CmdletBinding()]
param(
    [string]$AppName = 'QwenLocalChat.WinUI'
)

$ErrorActionPreference = 'Stop'

if (-not (Get-Process -Name $AppName -ErrorAction SilentlyContinue)) {
    throw "Local AI is not running. Open the current build, then run this script."
}

$windows = & winapp ui list-windows -a $AppName --json
if ($LASTEXITCODE -ne 0) { throw "winapp ui list-windows failed ($LASTEXITCODE): $windows" }
$main = @(($windows | ConvertFrom-Json) | Where-Object {
    $_.title -eq 'Local AI' -and $_.className -eq 'WinUIDesktopWin32WindowClass'
}) | Select-Object -First 1
if (-not $main) { throw 'Main Local AI window was not found.' }
$hwnd = [string]$main.hwnd

function Invoke-WinAppJson {
    param([Parameter(Mandatory = $true)][string[]]$Arguments)
    $output = & winapp @Arguments
    $parsed = $output | ConvertFrom-Json
    if ($LASTEXITCODE -ne 0 -and $parsed.matchCount -ne 0) {
        throw "winapp $($Arguments -join ' ') failed ($LASTEXITCODE): $output"
    }
    return $parsed
}

$launcher = $null
foreach ($id in @('SettingsButton', 'VideoSettingsButton', 'HanhuaSettingsButton')) {
    $found = Invoke-WinAppJson -Arguments @('ui', 'search', $id, '-w', $hwnd, '--json')
    $match = @($found.matches | Where-Object {
        $_.automationId -eq $id -and $_.isEnabled -and -not $_.isOffscreen
    }) | Select-Object -First 1
    if ($match) {
        $launcher = [string]$match.selector
        break
    }
}
if (-not $launcher) { throw 'No visible settings button on the main window.' }

Invoke-WinAppJson -Arguments @('ui', 'invoke', $launcher, '-w', $hwnd, '--json') | Out-Null
Invoke-WinAppJson -Arguments @(
    'ui', 'wait-for', 'CloseSettingsButton', '-w', $hwnd,
    '--property', 'IsEnabled', '--value', 'True', '--timeout', '5000', '--json'
) | Out-Null
# The icon sits on the left page. With the drawer open, the mouse click lands on the scrim.
Invoke-WinAppJson -Arguments @('ui', 'click', 'AppIconMark', '-w', $hwnd, '--json') | Out-Null
Invoke-WinAppJson -Arguments @(
    'ui', 'wait-for', 'CloseSettingsButton', '-w', $hwnd,
    '--property', 'IsEnabled', '--value', 'False', '--timeout', '4000', '--json'
) | Out-Null
Write-Output 'PASS settings-scrim-click-closes-drawer'
