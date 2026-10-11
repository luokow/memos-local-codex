[CmdletBinding()]
param(
    [string]$AppName = 'QwenLocalChat.WinUI',
    [switch]$SelfTest
)

$ErrorActionPreference = 'Stop'

$MinOverlapPx = 8
$MinSaveHeightDip = 36
$CompactNumberMinDip = 120
$CompactNumberMaxDip = 180

function Convert-ToDip([double]$Pixels, [int]$Dpi) {
    if ($Dpi -le 0) { throw "DPI must be positive. Actual=$Dpi" }
    return $Pixels * 96.0 / $Dpi
}

function Get-RectOverlap($First, $Second) {
    $x1 = [Math]::Max($First.X, $Second.X)
    $y1 = [Math]::Max($First.Y, $Second.Y)
    $x2 = [Math]::Min($First.X + $First.Width, $Second.X + $Second.Width)
    $y2 = [Math]::Min($First.Y + $First.Height, $Second.Y + $Second.Height)
    return [pscustomobject]@{ Width = ($x2 - $x1); Height = ($y2 - $y1) }
}

function Test-GalleryCombo([int]$WidthPx, [int]$WindowWidthPx, [int]$Dpi) {
    if ($WindowWidthPx -le 0) { throw "Window width must be positive. Actual=$WindowWidthPx" }
    $widthDip = Convert-ToDip $WidthPx $Dpi
    $ratio = $WidthPx / [double]$WindowWidthPx
    $ok = ($widthDip -ge 40 -and $widthDip -le 480 -and $ratio -le 0.55)
    return [pscustomobject]@{ Ok = [bool]$ok; WidthDip = [math]::Round($widthDip, 1); Ratio = [math]::Round($ratio, 2) }
}

function Test-CompactWidth([double]$WidthPx, [int]$Dpi) {
    $dip = Convert-ToDip $WidthPx $Dpi
    $ok = ($dip -ge $CompactNumberMinDip -and $dip -le $CompactNumberMaxDip)
    return [pscustomobject]@{ Ok = [bool]$ok; Dip = [math]::Round($dip, 1) }
}

function Test-SameRight([int]$Right, [int]$ReferenceRight) {
    $delta = [Math]::Abs($Right - $ReferenceRight)
    return [pscustomobject]@{ Ok = [bool]($delta -le 64); Delta = $delta }
}

function Test-NumberEditButtons($ButtonIds) {
    $ids = @($ButtonIds)
    $extra = @($ids | Where-Object { $_ -ne 'UpSpinButton' -and $_ -ne 'DownSpinButton' })
    $ok = ($extra.Count -eq 0 -and ($ids -contains 'UpSpinButton') -and ($ids -contains 'DownSpinButton'))
    return [pscustomobject]@{ Ok = [bool]$ok; Extra = (@($extra) -join ',') }
}

function Test-SaveInsideClient($Save, [int]$ClientX, [int]$ClientY, [int]$ClientHeight, [int]$Dpi) {
    $bottom = $Save.Y + $Save.Height
    $clientBottom = $ClientY + $ClientHeight
    $heightDip = Convert-ToDip $Save.Height $Dpi
    $inside = ($Save.Width -gt 0 -and $Save.Height -gt 0 -and $Save.Y -ge $ClientY -and $bottom -le ($clientBottom - 8))
    return [pscustomobject]@{ Ok = [bool]($inside -and $heightDip -ge $MinSaveHeightDip); HeightDip = [math]::Round($heightDip, 1); BottomGapPx = ($clientBottom - $bottom) }
}

function Assert-SettingsLayoutSelfTest {
    $stretched = Test-GalleryCombo -WidthPx 1273 -WindowWidthPx 1422 -Dpi 144
    if ($stretched.Ok) { throw "Combo check accepted a dropdown stretched across the card. WidthDip=$($stretched.WidthDip) Ratio=$($stretched.Ratio)" }

    $gallery = Test-GalleryCombo -WidthPx 200 -WindowWidthPx 1422 -Dpi 144
    if (-not $gallery.Ok) { throw "Combo check rejected a Gallery-sized dropdown. WidthDip=$($gallery.WidthDip) Ratio=$($gallery.Ratio)" }

    $slot = Test-GalleryCombo -WidthPx 935 -WindowWidthPx 1440 -Dpi 144
    if ($slot.Ok) { throw "Combo check accepted the dropdown inside the rejected 720 column. WidthDip=$($slot.WidthDip) Ratio=$($slot.Ratio)" }

    $covered = Get-RectOverlap @{ X = 900; Y = 1050; Width = 200; Height = 60 } @{ X = 155; Y = 975; Width = 935; Height = 96 }
    if ($covered.Height -le $MinOverlapPx -or $covered.Width -le $MinOverlapPx) {
        throw "Overlap check missed the save row covering the profile combo. Height=$($covered.Height) Width=$($covered.Width)"
    }

    $clear = Get-RectOverlap @{ X = 1223; Y = 1014; Width = 132; Height = 60 } @{ X = 155; Y = 900; Width = 1273; Height = 112 }
    if ($clear.Height -gt $MinOverlapPx -and $clear.Width -gt $MinOverlapPx) {
        throw "Overlap check flagged a save row that sits below the card. Height=$($clear.Height)"
    }

    $compact = Test-CompactWidth -WidthPx 204 -Dpi 144
    if (-not $compact.Ok) { throw "Compact-width check rejected the 136 DIP number box. Dip=$($compact.Dip)" }
    $stretchedNumber = Test-CompactWidth -WidthPx 1273 -Dpi 144
    if ($stretchedNumber.Ok) { throw "Compact-width check accepted a number box stretched across the card. Dip=$($stretchedNumber.Dip)" }

    $visible = Test-SaveInsideClient @{ Y = 1090; Width = 132; Height = 60 } -ClientX 87 -ClientY 76 -ClientHeight 1101 -Dpi 144
    if (-not $visible.Ok) { throw "Save-button check rejected a fully visible button. HeightDip=$($visible.HeightDip) BottomGapPx=$($visible.BottomGapPx)" }
    $clipped = Test-SaveInsideClient @{ Y = 1140; Width = 132; Height = 39 } -ClientX 87 -ClientY 76 -ClientHeight 1101 -Dpi 144
    if ($clipped.Ok) { throw "Save-button check accepted a button cut by the window edge. HeightDip=$($clipped.HeightDip) BottomGapPx=$($clipped.BottomGapPx)" }

    # 2026-10-11: the profile combo's right edge matched the text column while this expander was 679px wide.
    $narrowExpander = Test-SameRight -Right (255 + 679) -ReferenceRight (255 + 1310)
    if ($narrowExpander.Ok) { throw "Expander check accepted the half-width video expander. Delta=$($narrowExpander.Delta)" }
    $filledExpander = Test-SameRight -Right 1670 -ReferenceRight 1679
    if (-not $filledExpander.Ok) { throw "Expander check rejected the stretched video expander. Delta=$($filledExpander.Delta)" }

    $cleanEdit = Test-NumberEditButtons @('UpSpinButton', 'DownSpinButton')
    if (-not $cleanEdit.Ok) { throw 'Number edit check rejected the two spin buttons.' }
    $coveredDigits = Test-NumberEditButtons @('UpSpinButton', 'DownSpinButton', 'DeleteButton')
    if ($coveredDigits.Ok) { throw 'Number edit check accepted a clear button over the digits.' }
    $unrealized = Test-NumberEditButtons @()
    if ($unrealized.Ok) { throw 'Number edit check accepted a box that was not in the tree.' }

    Write-Output 'PASS settings-layout-self-test'
}

if ($SelfTest) {
    Assert-SettingsLayoutSelfTest
    return
}

if (-not (Get-Process -Name $AppName -ErrorAction SilentlyContinue)) {
    throw "Local AI is not running. Open the current build, then run this script."
}

Add-Type @"
using System;
using System.Runtime.InteropServices;
public static class SettingsLayoutNative {
    [DllImport("user32.dll")] public static extern uint GetDpiForWindow(IntPtr hWnd);
}
"@

function Invoke-WinAppJson {
    param([Parameter(Mandatory = $true)][string[]]$Arguments)
    $output = & winapp @Arguments
    if ($LASTEXITCODE -ne 0) { throw "winapp $($Arguments -join ' ') failed ($LASTEXITCODE): $output" }
    return ($output | ConvertFrom-Json)
}

$windows = Invoke-WinAppJson -Arguments @('ui', 'list-windows', '-a', $AppName, '--json')
$main = @($windows | Where-Object { $_.title -eq 'Local AI' -and $_.className -eq 'WinUIDesktopWin32WindowClass' }) | Select-Object -First 1
if (-not $main) { throw 'Main Local AI window was not found.' }
$hwnd = [string]$main.hwnd
$hwndPtr = [IntPtr]::new([int64]$main.hwnd)

$closeState = Invoke-WinAppJson -Arguments @('ui', 'get-property', 'CloseSettingsButton', '-w', $hwnd, '--property', 'IsEnabled', '--json')
if ([string]$closeState.properties.IsEnabled -ne 'True') {
    $launcher = $null
    foreach ($id in @('SettingsButton', 'VideoSettingsButton', 'HanhuaSettingsButton')) {
        $found = Invoke-WinAppJson -Arguments @('ui', 'search', $id, '-w', $hwnd, '--json')
        $match = @($found.matches | Where-Object { $_.automationId -eq $id -and $_.isEnabled -and -not $_.isOffscreen }) | Select-Object -First 1
        if ($match) { $launcher = [string]$match.selector; break }
    }
    if (-not $launcher) { throw 'No visible settings button on the main window.' }
    Invoke-WinAppJson -Arguments @('ui', 'invoke', $launcher, '-w', $hwnd, '--json') | Out-Null
    Invoke-WinAppJson -Arguments @('ui', 'wait-for', 'CloseSettingsButton', '-w', $hwnd, '--property', 'IsEnabled', '--value', 'True', '--timeout', '5000', '--json') | Out-Null
}

$dpi = [int][SettingsLayoutNative]::GetDpiForWindow($hwndPtr)
if ($dpi -le 0) { throw "GetDpiForWindow returned $dpi." }

function Get-FlatElements($Nodes) {
    $flat = New-Object System.Collections.Generic.List[object]
    $pending = New-Object System.Collections.Generic.Stack[object]
    foreach ($node in @($Nodes)) { $pending.Push($node) }
    while ($pending.Count -gt 0) {
        $node = $pending.Pop()
        $flat.Add($node)
        foreach ($child in @($node.children)) { if ($null -ne $child) { $pending.Push($child) } }
    }
    return $flat
}

function Get-SettingsElements {
    $inspected = Invoke-WinAppJson -Arguments @('ui', 'inspect', '-w', $hwnd, '-d', '20', '--json')
    $window = @($inspected.windows | Where-Object { [string]$_.hwnd -eq $hwnd }) | Select-Object -First 1
    if (-not $window) { throw 'Settings inspect did not return the main window.' }
    return @(Get-FlatElements $window.elements)
}

function Find-Element($Elements, [string]$AutomationId) {
    $found = @($Elements | Where-Object { $_.automationId -eq $AutomationId }) | Select-Object -First 1
    if (-not $found) { throw "Settings element was not in the window tree: $AutomationId" }
    return $found
}

$openElements = Get-SettingsElements
$hostWindow = @($openElements | Where-Object { $_.type -eq 'Window' }) | Select-Object -First 1
if (-not $hostWindow) { throw 'Inspect tree has no Window element.' }
foreach ($comboId in @('StartupPageSetting', 'StartupModelSetting')) {
    $combo = Find-Element $openElements $comboId
    $comboWidth = Test-GalleryCombo -WidthPx ([int]$combo.width) -WindowWidthPx ([int]$hostWindow.width) -Dpi $dpi
    if (-not $comboWidth.Ok) {
        throw "$comboId is stretched across the row. WidthDip=$($comboWidth.WidthDip) Ratio=$($comboWidth.Ratio) Dpi=$dpi Window=$($hostWindow.width)x$($hostWindow.height)"
    }
    Write-Output "$comboId WidthDip=$($comboWidth.WidthDip) Ratio=$($comboWidth.Ratio)"
}

$startupCombo = Find-Element $openElements 'StartupPageSetting'
$columnRight = [int]$startupCombo.x + [int]$startupCombo.width

function Get-ControlRight([string]$Id) {
    Invoke-WinAppJson -Arguments @('ui', 'scroll-into-view', $Id, '-w', $hwnd, '--json') | Out-Null
    $found = Invoke-WinAppJson -Arguments @('ui', 'search', $Id, '-w', $hwnd, '--json')
    $match = @($found.matches | Where-Object { $_.automationId -eq $Id -and $_.width -gt 0 }) | Select-Object -First 1
    if (-not $match) { throw "Settings element was not measurable: $Id" }
    return ([int]$match.x + [int]$match.width)
}

function Assert-SameSettingsColumn([string]$SectionItem, [string]$Id) {
    Invoke-WinAppJson -Arguments @('ui', 'invoke', $SectionItem, '-w', $hwnd, '--json') | Out-Null
    Start-Sleep -Milliseconds 300
    $right = Get-ControlRight $Id
    $delta = [Math]::Abs($right - $columnRight)
    if ($delta -gt 64) {
        throw "$Id is not in the text settings column. Right=$right TextRight=$columnRight Delta=$delta. Video and hanhua must use the same column width."
    }
    Write-Output "$Id columnDelta=$delta"
}

Assert-SameSettingsColumn 'VideoSettingsSectionItem' 'VideoModelProfileSetting'

function Get-ControlBounds([string]$Id) {
    Invoke-WinAppJson -Arguments @('ui', 'scroll-into-view', $Id, '-w', $hwnd, '--json') | Out-Null
    $found = Invoke-WinAppJson -Arguments @('ui', 'search', $Id, '-w', $hwnd, '--json')
    $match = @($found.matches | Where-Object { $_.automationId -eq $Id -and $_.width -gt 0 }) | Select-Object -First 1
    if (-not $match) { throw "Settings element was not measurable: $Id" }
    return $match
}

$closeRight = Get-ControlRight 'CloseSettingsButton'
foreach ($expanderId in @('VideoProfileParametersExpander', 'VideoPromptPhrasesExpander')) {
    $expander = Get-ControlBounds $expanderId
    $expanderRight = [int]$expander.x + [int]$expander.width
    $column = Test-SameRight -Right $expanderRight -ReferenceRight $closeRight
    if (-not $column.Ok) {
        throw "$expanderId is narrower than the settings column. Right=$expanderRight CloseRight=$closeRight Delta=$($column.Delta). A combo on the same page can still line up."
    }
    Write-Output "$expanderId columnDelta=$($column.Delta)"
}

function Assert-NumberEditShowsDigits([string]$Id) {
    Invoke-WinAppJson -Arguments @('ui', 'scroll-into-view', $Id, '-w', $hwnd, '--json') | Out-Null
    Invoke-WinAppJson -Arguments @('ui', 'focus', $Id, '-w', $hwnd, '--json') | Out-Null
    Start-Sleep -Milliseconds 400
    $elements = Get-SettingsElements
    $box = Find-Element $elements $Id
    if ([int]$box.width -le 0 -or [string]$box.isOffscreen -eq 'True') {
        throw "$Id is not on screen, so a clipped value would not be visible."
    }
    $buttons = @(
        $elements | Where-Object {
            $_.type -eq 'Button' -and [int]$_.width -gt 0 -and [int]$_.height -gt 0 -and
            [int]$_.x -ge ([int]$box.x - 2) -and ([int]$_.x + [int]$_.width) -le ([int]$box.x + [int]$box.width + 2) -and
            [int]$_.y -ge ([int]$box.y - 2) -and ([int]$_.y + [int]$_.height) -le ([int]$box.y + [int]$box.height + 2)
        }
    )
    $ids = @($buttons | ForEach-Object { [string]$_.automationId })
    $edit = Test-NumberEditButtons $ids
    if (-not $edit.Ok) {
        throw "$Id hides its leading digits. Buttons=$($ids -join ',') Extra=$($edit.Extra). Focus the box; do not click it."
    }
    Write-Output "$Id editButtons=$($ids -join ',')"
}

Assert-NumberEditShowsDigits 'VideoWidthSetting'
Assert-NumberEditShowsDigits 'VideoProfileFpsSetting'
Assert-SameSettingsColumn 'HanhuaSettingsSectionItem' 'HanhuaPackRootPickButton'
Invoke-WinAppJson -Arguments @('ui', 'invoke', 'TextSettingsSectionItem', '-w', $hwnd, '--json') | Out-Null
Assert-NumberEditShowsDigits 'TemperatureSetting'
Assert-NumberEditShowsDigits 'PortSetting'

Invoke-WinAppJson -Arguments @('ui', 'scroll-into-view', 'ChatAttachMaxFilesSetting', '-w', $hwnd, '--json') | Out-Null
$numberProperty = Invoke-WinAppJson -Arguments @('ui', 'get-property', 'ChatAttachMaxFilesSetting', '-w', $hwnd, '--property', 'BoundingRectangle', '--json')
$numberParts = [string]$numberProperty.properties.BoundingRectangle -split ','
if ($numberParts.Count -ne 4) { throw "ChatAttachMaxFilesSetting bounds were not readable: $($numberProperty.properties.BoundingRectangle)" }
$compact = Test-CompactWidth -WidthPx ([double]$numberParts[2]) -Dpi $dpi
if (-not $compact.Ok) {
    throw "ChatAttachMaxFilesSetting left the compact width. Dip=$($compact.Dip) Allowed=$CompactNumberMinDip..$CompactNumberMaxDip Bounds=$($numberProperty.properties.BoundingRectangle)"
}
Write-Output "ChatAttachMaxFilesSetting Dip=$($compact.Dip)"

# ScrollItemPattern leaves this button at 0,0 until the settings viewer itself moves to the end.
Invoke-WinAppJson -Arguments @('ui', 'scroll', 'SettingsContentScrollViewer', '-w', $hwnd, '--to', 'bottom', '--json') | Out-Null
Start-Sleep -Milliseconds 400
$save = Get-ControlBounds 'SaveSettingsButton'
$scrolled = Get-SettingsElements
$saveRect = @{ X = [int]$save.x; Y = [int]$save.y; Width = [int]$save.width; Height = [int]$save.height }
$visible = Test-SaveInsideClient $saveRect -ClientX ([int]$hostWindow.x) -ClientY ([int]$hostWindow.y) -ClientHeight ([int]$hostWindow.height) -Dpi $dpi
if (-not $visible.Ok) {
    throw "SaveSettingsButton is clipped or off the page. HeightDip=$($visible.HeightDip) BottomGapPx=$($visible.BottomGapPx) Bounds=$($save.x),$($save.y),$($save.width),$($save.height)"
}

foreach ($other in @($scrolled | Where-Object { $_.automationId -like '*Setting' -and $_.width -gt 0 -and $_.height -gt 0 })) {
    $overlap = Get-RectOverlap $saveRect @{ X = [int]$other.x; Y = [int]$other.y; Width = [int]$other.width; Height = [int]$other.height }
    if ($overlap.Width -gt $MinOverlapPx -and $overlap.Height -gt $MinOverlapPx) {
        throw "SaveSettingsButton covers $($other.automationId) by $($overlap.Width)x$($overlap.Height) px."
    }
}
Write-Output "SaveSettingsButton visible HeightDip=$($visible.HeightDip) BottomGapPx=$($visible.BottomGapPx)"

Invoke-WinAppJson -Arguments @('ui', 'invoke', 'CloseSettingsButton', '-w', $hwnd, '--json') | Out-Null
Invoke-WinAppJson -Arguments @('ui', 'wait-for', 'CloseSettingsButton', '-w', $hwnd, '--property', 'IsEnabled', '--value', 'False', '--timeout', '4000', '--json') | Out-Null
Write-Output 'PASS settings-page-layout'
