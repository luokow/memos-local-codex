[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent (Split-Path -Parent $MyInvocation.MyCommand.Path)
$baselinePath = Join-Path $root 'docs\winui-ui-baseline.json'
$appXamlPath = Join-Path $root 'src\QwenLocalChat.WinUI\App.xaml'
$mainPagePath = Join-Path $root 'src\QwenLocalChat.WinUI\MainPage.xaml'
$mainPageSourcePath = Join-Path $root 'src\QwenLocalChat.WinUI\MainPage.xaml.cs'
$markdownPresenterPath = Join-Path $root 'src\QwenLocalChat.WinUI\MarkdownPresenter.cs'
$settingsPanelPath = Join-Path $root 'src\QwenLocalChat.WinUI\RuntimeSettingsPanel.xaml'
$settingsPanelSourcePath = Join-Path $root 'src\QwenLocalChat.WinUI\RuntimeSettingsPanel.xaml.cs'
$winuiProjectPath = Join-Path $root 'src\QwenLocalChat.WinUI\QwenLocalChat.WinUI.csproj'

if (-not (Test-Path -LiteralPath $baselinePath)) {
    throw "WinUI UI baseline is missing: $baselinePath"
}

$baseline = Get-Content -Raw -Encoding UTF8 -LiteralPath $baselinePath | ConvertFrom-Json
[xml]$appXaml = Get-Content -Raw -Encoding UTF8 -LiteralPath $appXamlPath
[xml]$mainPage = Get-Content -Raw -Encoding UTF8 -LiteralPath $mainPagePath
[xml]$settingsPanel = Get-Content -Raw -Encoding UTF8 -LiteralPath $settingsPanelPath
[xml]$winuiProject = Get-Content -Raw -Encoding UTF8 -LiteralPath $winuiProjectPath
$markdownPresenterSource = Get-Content -Raw -Encoding UTF8 -LiteralPath $markdownPresenterPath
$mainPageSource = Get-Content -Raw -Encoding UTF8 -LiteralPath $mainPageSourcePath
$settingsPanelSource = Get-Content -Raw -Encoding UTF8 -LiteralPath $settingsPanelSourcePath

function New-XamlNamespaceManager([xml]$document) {
    $manager = [System.Xml.XmlNamespaceManager]::new($document.NameTable)
    $manager.AddNamespace('xaml', 'http://schemas.microsoft.com/winfx/2006/xaml/presentation')
    $manager.AddNamespace('x', 'http://schemas.microsoft.com/winfx/2006/xaml')
    $manager.AddNamespace('local', 'using:QwenLocalChat_WinUI')
    Write-Output -NoEnumerate $manager
}

function Require-Equal($expected, $actual, [string]$message) {
    if ([string]$expected -ne [string]$actual) {
        throw "$message Expected='$expected' Actual='$actual'"
    }
}

function Require-ResourceRedirect([xml]$document, [System.Xml.XmlNamespaceManager]$namespaceManager, [string]$key, [string]$expectedResourceKey) {
    $resource = $document.SelectSingleNode("//xaml:StaticResource[@x:Key='$key']", $namespaceManager)
    if ($null -eq $resource) { throw "Required resource redirect is missing: $key" }
    Require-equal $expectedResourceKey $resource.ResourceKey "Resource redirect drifted: $key."
}

function Forbid-ResourceKey([xml]$document, [System.Xml.XmlNamespaceManager]$namespaceManager, [string]$key) {
    $resource = $document.SelectSingleNode("//xaml:StaticResource[@x:Key='$key']", $namespaceManager)
    if ($null -ne $resource) {
        throw "Forbidden theme resource override is present: $key (use platform default ToggleSwitch hover visuals)."
    }
}

$appNs = New-XamlNamespaceManager $appXaml
$pageNs = New-XamlNamespaceManager $mainPage
$settingsNs = New-XamlNamespaceManager $settingsPanel

$windowsAppSdk = $winuiProject.Project.ItemGroup.PackageReference |
    Where-Object { $_.Include -eq 'Microsoft.WindowsAppSDK' } |
    Select-Object -First 1
if ($null -eq $windowsAppSdk) { throw 'Microsoft.WindowsAppSDK package reference is missing.' }
Require-Equal '1.8.260508005' $windowsAppSdk.Version 'WinUI runtime servicing baseline drifted.'

$settingsShell = $settingsPanel.SelectSingleNode('/xaml:UserControl/xaml:Grid/xaml:Border', $settingsNs)
if ($null -eq $settingsShell) { throw 'Runtime settings panel shell is missing.' }
Require-equal '500' $settingsShell.Width 'Runtime settings panel must keep the approved compact width.'

$settingsNumbers = $settingsPanel.SelectNodes('//xaml:NumberBox', $settingsNs)
if ($settingsNumbers.Count -eq 0) { throw 'Runtime settings panel must expose NumberBox controls.' }
foreach ($numberBox in $settingsNumbers) {
    Require-Equal 'Inline' $numberBox.SpinButtonPlacementMode "Settings NumberBox '$($numberBox.Name)' must not create a Compact Popup."
    Require-Equal '{StaticResource CompactSettingsNumberBoxStyle}' $numberBox.Style "Settings NumberBox '$($numberBox.Name)' must use the compact width contract."
}
$settingsTextBoxes = $settingsPanel.SelectNodes('//xaml:TextBox', $settingsNs)
foreach ($textBox in $settingsTextBoxes) {
    Require-Equal '{StaticResource CompactSettingsTextBoxStyle}' $textBox.Style "Settings TextBox '$($textBox.Name)' must use the compact width contract."
}
if ($settingsPanelSource -match '\w+Box\.Focus\s*\(') {
    throw 'Opening settings must not programmatically focus a NumberBox and create transient spin-button UI.'
}

$settingsHost = $mainPage.SelectSingleNode("//local:RuntimeSettingsPanel[@x:Name='SettingsPanel']", $pageNs)
if ($null -eq $settingsHost) { throw 'Runtime settings host is missing.' }
Require-Equal '0' $settingsHost.Opacity 'Closed settings must remain loaded and become transparent.'
Require-Equal 'False' $settingsHost.IsHitTestVisible 'Closed settings must not intercept pointer input.'
Require-Equal 'False' $settingsHost.IsEnabled 'Closed settings descendants must release keyboard focus.'
if ($settingsHost.HasAttribute('Visibility')) {
    throw 'Runtime settings must not use Visibility to detach its control tree.'
}
if ($settingsPanelSource -match 'Visibility\s*=') {
    throw 'Runtime settings code must not detach the panel with Visibility changes.'
}
if ($mainPageSource -notmatch 'SettingsButton\.Focus\s*\(') {
    throw 'Settings dismissal must transfer focus back to the main page before hiding interaction.'
}

$bodyStyle = $appXaml.SelectSingleNode("//xaml:Style[@x:Key='$($baseline.typography.transcriptBodyStyle)']", $appNs)
if ($null -eq $bodyStyle) { throw "Transcript body style is missing: $($baseline.typography.transcriptBodyStyle)" }

$fontSize = $bodyStyle.SelectSingleNode("xaml:Setter[@Property='FontSize']", $appNs).Value
$lineHeight = $bodyStyle.SelectSingleNode("xaml:Setter[@Property='LineHeight']", $appNs).Value
$fontFamily = $bodyStyle.SelectSingleNode("xaml:Setter[@Property='FontFamily']", $appNs).Value
Require-Equal $baseline.typography.transcriptBodyFontSize $fontSize 'Transcript body font size drifted.'
Require-Equal $baseline.typography.transcriptBodyLineHeight $lineHeight 'Transcript body line height drifted.'
Require-Equal $baseline.typography.transcriptBodyFontFamily $fontFamily 'Transcript body font family drifted.'

$statusStyle = $appXaml.SelectSingleNode("//xaml:Style[@x:Key='$($baseline.typography.statusTextStyle)']", $appNs)
if ($null -eq $statusStyle) { throw "Status text style is missing: $($baseline.typography.statusTextStyle)" }
$statusFontFamily = $statusStyle.SelectSingleNode("xaml:Setter[@Property='FontFamily']", $appNs).Value
Require-Equal $baseline.typography.statusFontFamily $statusFontFamily 'Status font family drifted.'

$transcript = $mainPage.SelectSingleNode("//xaml:ListView[@x:Name='TranscriptList']", $pageNs)
if ($null -eq $transcript) { throw 'TranscriptList is missing.' }

$markdownPresenter = $transcript.SelectSingleNode(".//local:MarkdownPresenter", $pageNs)
if ($null -eq $markdownPresenter) { throw 'Transcript MarkdownPresenter is missing or is not bound to message text.' }
$markdownBinding = $markdownPresenter.Attributes['Text'].Value
if ($markdownBinding -notin @('{x:Bind Text}', '{x:Bind Text, Mode=OneWay}')) {
    throw "Transcript MarkdownPresenter has an unsupported message binding: $markdownBinding"
}
if (($markdownPresenterSource | Select-String -Pattern 'new RichTextBlock' -AllMatches).Matches.Count -ne 1) {
    throw 'MarkdownPresenter must own exactly one RichTextBlock so selection can cross block boundaries.'
}
if ($markdownPresenterSource -notmatch 'IsTextSelectionEnabled\s*=\s*true') {
    throw 'MarkdownPresenter single text owner must keep selection enabled.'
}
$streamingText = $transcript.SelectSingleNode(".//xaml:TextBlock[@Visibility='{x:Bind StreamingVisibility, Mode=OneWay}']", $pageNs)
if ($null -eq $streamingText -or $streamingText.IsTextSelectionEnabled -ne 'True') {
    throw 'Streaming replies must use one selectable plain-text owner before final Markdown presentation.'
}

$copyButton = $transcript.SelectSingleNode(".//xaml:Button[@AutomationProperties.AutomationId='CopyMessageButton']", $pageNs)
if ($null -eq $copyButton) { throw 'Whole-message copy button is missing.' }
Require-Equal '{x:Bind Text}' $copyButton.Tag 'Whole-message copy button must receive the complete source message.'
Require-Equal $baseline.interaction.wholeMessageCopy.automationName $copyButton.Attributes['AutomationProperties.Name'].Value 'Whole-message copy button accessibility name drifted.'
Require-Equal "{StaticResource $($baseline.interaction.wholeMessageCopy.style)}" $copyButton.Style 'Whole-message copy button style drifted.'
Require-Equal $baseline.interaction.wholeMessageCopy.visibilityBinding $copyButton.Visibility 'Whole-message copy visibility binding drifted.'
$copyButtonParent = $copyButton.ParentNode
Require-Equal 'StackPanel' $copyButtonParent.LocalName 'Whole-message copy button must share the assistant label row.'
Require-equal 'Horizontal' $copyButtonParent.Orientation 'Whole-message copy button must sit beside the Qwen label.'

$bodyStyleTarget = $bodyStyle.Attributes['TargetType'].Value
Require-Equal 'RichTextBlock' $bodyStyleTarget 'Markdown transcript style must target RichTextBlock.'

$input = $mainPage.SelectSingleNode("//xaml:TextBox[@x:Name='InputBox']", $pageNs)
if ($null -eq $input) { throw 'InputBox is missing.' }
Require-Equal $baseline.typography.inputFontSize $input.FontSize 'Input font size drifted.'

$toggleHover = $baseline.interaction.toggleHover
Require-Equal 'platform-default' $toggleHover.mode 'ToggleSwitch hover contract must stay on platform defaults while the native crash A/B is open.'
foreach ($forbiddenKey in @($toggleHover.forbiddenResourceKeys)) {
    Forbid-ResourceKey $appXaml $appNs ([string]$forbiddenKey)
}

[pscustomobject]@{
    Result = 'PASS'
    Baseline = $baselinePath
    TranscriptBodyStyle = $baseline.typography.transcriptBodyStyle
    TranscriptBodyFontSize = [double]$fontSize
    TranscriptBodyLineHeight = [double]$lineHeight
    InputFontSize = [double]$input.FontSize
    ToggleHoverContract = 'platform-default-no-pointerover-overrides'
} | ConvertTo-Json
