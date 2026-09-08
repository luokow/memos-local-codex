[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent (Split-Path -Parent $MyInvocation.MyCommand.Path)
$baselinePath = Join-Path $root 'docs\winui-ui-baseline.json'
$changeLedgerPath = Join-Path $root 'docs\ui-change-ledger.json'
$appXamlPath = Join-Path $root 'src\QwenLocalChat.WinUI\App.xaml'
$mainPagePath = Join-Path $root 'src\QwenLocalChat.WinUI\MainPage.xaml'
$mainPageSourcePath = Join-Path $root 'src\QwenLocalChat.WinUI\MainPage.xaml.cs'
$mainWindowSourcePath = Join-Path $root 'src\QwenLocalChat.WinUI\MainWindow.xaml.cs'
$videoPanelPath = Join-Path $root 'src\QwenLocalChat.WinUI\VideoGenerationPanel.xaml'
$videoPanelSourcePath = Join-Path $root 'src\QwenLocalChat.WinUI\VideoGenerationPanel.xaml.cs'
$hanhuaPanelPath = Join-Path $root 'src\QwenLocalChat.WinUI\HanhuaPanel.xaml'
$hanhuaPanelSourcePath = Join-Path $root 'src\QwenLocalChat.WinUI\HanhuaPanel.xaml.cs'
$markdownPresenterPath = Join-Path $root 'src\QwenLocalChat.WinUI\MarkdownPresenter.cs'
$settingsPanelPath = Join-Path $root 'src\QwenLocalChat.WinUI\RuntimeSettingsPanel.xaml'
$settingsPanelSourcePath = Join-Path $root 'src\QwenLocalChat.WinUI\RuntimeSettingsPanel.xaml.cs'
$winuiProjectPath = Join-Path $root 'src\QwenLocalChat.WinUI\QwenLocalChat.WinUI.csproj'

if (-not (Test-Path -LiteralPath $baselinePath)) {
    throw "WinUI UI baseline is missing: $baselinePath"
}
if (-not (Test-Path -LiteralPath $changeLedgerPath)) {
    throw "Existing-product change ledger is missing: $changeLedgerPath"
}

$baseline = Get-Content -Raw -Encoding UTF8 -LiteralPath $baselinePath | ConvertFrom-Json
$changeLedger = Get-Content -Raw -Encoding UTF8 -LiteralPath $changeLedgerPath | ConvertFrom-Json
[xml]$appXaml = Get-Content -Raw -Encoding UTF8 -LiteralPath $appXamlPath
[xml]$mainPage = Get-Content -Raw -Encoding UTF8 -LiteralPath $mainPagePath
[xml]$videoPanel = Get-Content -Raw -Encoding UTF8 -LiteralPath $videoPanelPath
[xml]$hanhuaPanel = Get-Content -Raw -Encoding UTF8 -LiteralPath $hanhuaPanelPath
[xml]$settingsPanel = Get-Content -Raw -Encoding UTF8 -LiteralPath $settingsPanelPath
[xml]$winuiProject = Get-Content -Raw -Encoding UTF8 -LiteralPath $winuiProjectPath
$markdownPresenterSource = Get-Content -Raw -Encoding UTF8 -LiteralPath $markdownPresenterPath
$mainPageSource = Get-Content -Raw -Encoding UTF8 -LiteralPath $mainPageSourcePath
$mainWindowSource = Get-Content -Raw -Encoding UTF8 -LiteralPath $mainWindowSourcePath
$videoPanelSource = Get-Content -Raw -Encoding UTF8 -LiteralPath $videoPanelSourcePath
$hanhuaPanelSource = Get-Content -Raw -Encoding UTF8 -LiteralPath $hanhuaPanelSourcePath
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
$videoNs = New-XamlNamespaceManager $videoPanel
$hanhuaNs = New-XamlNamespaceManager $hanhuaPanel
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
if ($settingsPanelSource -match '(?m)^\s*Visibility\s*=') {
    throw 'Runtime settings panel itself must not detach with Visibility changes.'
}

function Require-StringList($value, [string]$message) {
    $items = @($value)
    if ($items.Count -eq 0 -or @($items | Where-Object { [string]::IsNullOrWhiteSpace([string]$_) }).Count -gt 0) {
        throw $message
    }
}

Require-Equal '1' $changeLedger.schema_version 'Existing-product change ledger schema drifted.'
Require-StringList $changeLedger.baseline_paths 'Change ledger must name the product baseline.'
Require-StringList $changeLedger.affected_surfaces 'Change ledger must list affected surfaces.'
Require-StringList $changeLedger.acceptance 'Change ledger must map claims to evidence.'

$requiredSurfaceIds = @('shared-shell', 'chat-composer', 'video-composer', 'video-preview', 'settings-drawer', 'exit-overlay', 'model-profiles', 'video-workflow')
$surfaceIds = @($changeLedger.affected_surfaces | ForEach-Object { [string]$_.id })
foreach ($surfaceId in $requiredSurfaceIds) {
    if ($surfaceId -notin $surfaceIds) { throw "Change ledger is missing affected surface: $surfaceId" }
}
foreach ($surface in @($changeLedger.affected_surfaces)) {
    Require-StringList $surface.preserve "Change ledger surface '$($surface.id)' must declare preserved relations."
    Require-StringList $surface.change_kinds "Change ledger surface '$($surface.id)' must classify the change."
}

$requiredEvidenceKinds = @('static', 'functional', 'real-window', 'temporal', 'runtime', 'artifact-identity')
$evidenceKinds = @($changeLedger.acceptance | ForEach-Object { [string]$_.kind })
foreach ($evidenceKind in $requiredEvidenceKinds) {
    if ($evidenceKind -notin $evidenceKinds) { throw "Change ledger is missing acceptance evidence: $evidenceKind" }
}

$saveVideoContract = @($changeLedger.runtime_contracts | Where-Object { $_.integration -eq 'ComfyUI SaveVideo' }) | Select-Object -First 1
if ($null -eq $saveVideoContract) { throw 'Change ledger must retain the ComfyUI SaveVideo runtime contract.' }
foreach ($requiredInput in @('format', 'codec')) {
    if ($requiredInput -notin @($saveVideoContract.required_inputs)) {
        throw "ComfyUI SaveVideo contract is missing required input: $requiredInput"
    }
}
if ($changeLedger.baseline_update.changed -eq $true -and [string]::IsNullOrWhiteSpace([string]$changeLedger.baseline_update.approval_reference)) {
    throw 'A design baseline change requires an explicit user approval reference.'
}
$compactNumberStyle = $appXaml.SelectSingleNode("//xaml:Style[@x:Key='CompactSettingsNumberBoxStyle']", $appNs)
if ($null -eq $compactNumberStyle) { throw 'CompactSettingsNumberBoxStyle must live in application resources so dynamically loaded settings content can resolve it.' }
Require-Equal 'Top' $compactNumberStyle.SelectSingleNode("xaml:Setter[@Property='VerticalAlignment']", $appNs).Value 'Compact settings numbers must top-align inside two-column Auto rows.'
$compactTextStyle = $appXaml.SelectSingleNode("//xaml:Style[@x:Key='CompactSettingsTextBoxStyle']", $appNs)
if ($null -eq $compactTextStyle) { throw 'CompactSettingsTextBoxStyle must live in application resources so dynamically loaded settings content can resolve it.' }
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
Require-equal 'Horizontal' $copyButtonParent.Orientation 'Whole-message copy button must sit beside the assistant label.'

$bodyStyleTarget = $bodyStyle.Attributes['TargetType'].Value
Require-Equal 'RichTextBlock' $bodyStyleTarget 'Markdown transcript style must target RichTextBlock.'

$input = $mainPage.SelectSingleNode("//xaml:TextBox[@x:Name='InputBox']", $pageNs)
if ($null -eq $input) { throw 'InputBox is missing.' }
$composerInputStyle = $appXaml.SelectSingleNode("//xaml:Style[@x:Key='ComposerInputTextBoxStyle']", $appNs)
if ($null -eq $composerInputStyle) { throw 'Shared composer input typography is missing.' }
Require-Equal $baseline.typography.inputFontSize $composerInputStyle.SelectSingleNode("xaml:Setter[@Property='FontSize']", $appNs).Value 'Input font size drifted.'
Require-Equal '{StaticResource ComposerInputTextBoxStyle}' $input.Style 'Chat input must use the shared composer typography.'
Require-Equal '78' $input.MinHeight 'Chat input must keep the shared three-line minimum.'
Require-Equal '188' $input.MaxHeight 'Chat input must keep the shared eight-line hard cap.'

$modeSelector = $mainPage.SelectSingleNode("//xaml:SelectorBar[@x:Name='ModeSelector']", $pageNs)
if ($null -eq $modeSelector) { throw 'Local AI mode selector is missing.' }
$modeItems = @($modeSelector.SelectNodes('xaml:SelectorBarItem', $pageNs))
if ($modeItems.Count -ne 3) { throw 'Mode selector must have chat, video, and hanhua items.' }
if ($null -eq $mainPage.SelectSingleNode("//xaml:SelectorBarItem[@x:Name='ChatModeItem']", $pageNs)) {
    throw 'Chat mode item is missing.'
}
if ($null -eq $mainPage.SelectSingleNode("//xaml:SelectorBarItem[@x:Name='VideoModeItem']", $pageNs)) {
    throw 'Video mode item is missing.'
}
if ($null -eq $mainPage.SelectSingleNode("//xaml:SelectorBarItem[@x:Name='HanhuaModeItem']", $pageNs)) {
    throw 'Hanhua mode item is missing.'
}
$chatLabel = $mainPage.SelectSingleNode("//xaml:SelectorBarItem[@x:Name='ChatModeItem']", $pageNs).Attributes['Text'].Value
$videoLabel = $mainPage.SelectSingleNode("//xaml:SelectorBarItem[@x:Name='VideoModeItem']", $pageNs).Attributes['Text'].Value
$hanhuaLabel = $mainPage.SelectSingleNode("//xaml:SelectorBarItem[@x:Name='HanhuaModeItem']", $pageNs).Attributes['Text'].Value
if ([string]::IsNullOrWhiteSpace($chatLabel) -or [string]::IsNullOrWhiteSpace($videoLabel) -or [string]::IsNullOrWhiteSpace($hanhuaLabel)) {
    throw 'Mode selector items must keep visible labels.'
}
if ($hanhuaLabel.Length -ne 2) {
    throw 'Hanhua mode label must stay the two-character 汉化 caption.'
}
$statusStyle = $appXaml.SelectSingleNode("//xaml:Style[@x:Key='StatusTextStyle']", $appNs)
if ($null -eq $statusStyle) { throw 'StatusTextStyle is missing.' }
$statusFontSize = $statusStyle.SelectSingleNode("xaml:Setter[@Property='FontSize']", $appNs)
Require-Equal '12' $statusFontSize.Value 'The three runtime status chips must retain their original 12pt type scale.'
foreach ($statusName in @('QwenStatusText', 'VideoModelStatusText', 'MemosStatusText', 'LogStatusText')) {
    $statusText = $mainPage.SelectSingleNode("//xaml:TextBlock[@x:Name='$statusName']", $pageNs)
    if ($null -eq $statusText) { throw "Runtime status text is missing: $statusName" }
    Require-Equal '{StaticResource StatusTextStyle}' $statusText.Style "Runtime status text '$statusName' must retain the original StatusTextStyle."
}
$modeToolbar = $mainPage.SelectSingleNode("//xaml:Border[@x:Name='ModeToolbar']", $pageNs)
if ($null -eq $modeToolbar) { throw 'Chat and video switching must live in one integrated mode toolbar.' }
Require-Equal 'Transparent' $modeToolbar.Background 'Mode toolbar must use a quiet transparent surface instead of a heavy full-width card.'
Require-Equal '0,0,0,1' $modeToolbar.BorderThickness 'Mode toolbar must read as a lightweight section divider.'
Require-Equal '0' $modeToolbar.CornerRadius 'Mode toolbar divider must not add another rounded container.'
$modeToolbarGrid = $modeToolbar.SelectSingleNode('xaml:Grid', $pageNs)
if ($null -eq $modeToolbarGrid -or $modeToolbarGrid.SelectNodes('xaml:Grid.ColumnDefinitions/xaml:ColumnDefinition', $pageNs).Count -ne 2) {
    throw 'The integrated mode toolbar must have mode and contextual-tool columns.'
}
if ($modeSelector.ParentNode -ne $modeToolbarGrid) { throw 'Mode selector must be part of the integrated mode toolbar, not the brand/status row.' }
Require-Equal '0' $modeSelector.Attributes['Grid.Column'].Value 'Mode selector must lead the integrated toolbar.'
$chatTools = $mainPage.SelectSingleNode("//xaml:StackPanel[@x:Name='ChatToolsHost']", $pageNs)
$videoTools = $mainPage.SelectSingleNode("//xaml:StackPanel[@x:Name='VideoToolsHost']", $pageNs)
if ($null -eq $chatTools -or $null -eq $videoTools) { throw 'Chat and video tools must use same-position hosts.' }
if ($chatTools.ParentNode -ne $videoTools.ParentNode -or $chatTools.ParentNode.ParentNode -ne $modeToolbarGrid) {
    throw 'Chat and video contextual tools must share the second integrated-toolbar column.'
}
$pageRows = $mainPage.SelectNodes('/xaml:Page/xaml:Grid/xaml:Grid/xaml:Grid.RowDefinitions/xaml:RowDefinition', $pageNs)
if ($pageRows.Count -lt 4) { throw 'Shared chat/video shell rows are missing.' }
Require-Equal 'Auto' $pageRows[2].Height 'Composer row must follow the bounded adaptive editor height.'
Require-Equal '64' $pageRows[3].Height 'Footer budget must stay 64 high across modes.'
$chatComposer = $mainPage.SelectSingleNode("//xaml:Border[@x:Name='ChatComposerHost']", $pageNs)
$chatComposerRows = $chatComposer.SelectNodes('xaml:Grid/xaml:Grid.RowDefinitions/xaml:RowDefinition', $pageNs)
if ($chatComposerRows.Count -ne 3) { throw 'Chat composer must use title/meta, editor, and optional-hint rows.' }
$chatPromptLabel = $chatComposer.SelectSingleNode(".//xaml:TextBlock[@x:Name='ChatPromptLabel']", $pageNs)
Require-Equal '{StaticResource ComposerTitleTextStyle}' $chatPromptLabel.Style 'Chat prompt label must share the video prompt label typography.'
$chatMeta = $chatComposer.SelectSingleNode(".//xaml:TextBlock[@x:Name='ChatComposerMetaText']", $pageNs)
Require-Equal 'Right' $chatMeta.HorizontalAlignment 'Chat model metadata must mirror the video preset alignment.'
Require-Equal '{StaticResource ComposerMetaTextStyle}' $chatMeta.Style 'Chat metadata must use the shared composer summary typography.'
$chatExpand = $chatComposer.SelectSingleNode(".//*[@AutomationProperties.AutomationId='ExpandChatInputButton']", $pageNs)
if ($null -eq $chatExpand) { throw 'Chat composer must expose the shared expand-editor action.' }
if ($null -ne $chatExpand.SelectSingleNode('xaml:Button.Flyout', $pageNs)) { throw 'Chat expanded editor must not use an edge-constrained Flyout.' }
$expandedEditor = $mainPage.SelectSingleNode("//xaml:Grid[@x:Name='ExpandedEditorOverlay']", $pageNs)
if ($null -eq $expandedEditor) { throw 'Chat and video must share an application-owned expanded editor overlay.' }
$expandedEditorBorder = $expandedEditor.SelectSingleNode('xaml:Border', $pageNs)
Require-Equal 'Center' $expandedEditorBorder.HorizontalAlignment 'Expanded editor must stay horizontally centered.'
Require-Equal 'Center' $expandedEditorBorder.VerticalAlignment 'Expanded editor must stay vertically centered.'
$expandedText = $expandedEditor.SelectSingleNode(".//xaml:TextBox[@x:Name='ExpandedEditorText']", $pageNs)
Require-Equal 'Disabled' $expandedText.Attributes['ScrollViewer.HorizontalScrollBarVisibility'].Value 'Expanded editor must never require a bottom horizontal scrollbar.'
$videoHost = $mainPage.SelectSingleNode("//xaml:Grid[@x:Name='VideoModeHost']/local:VideoGenerationPanel[@x:Name='VideoPanel']", $pageNs)
if ($null -eq $videoHost) { throw 'Video mode must remain inside the shared page shell so the mode selector stays reachable.' }
$hanhuaHost = $mainPage.SelectSingleNode("//xaml:Grid[@x:Name='HanhuaModeHost']/local:HanhuaPanel[@x:Name='HanhuaPanel']", $pageNs)
if ($null -eq $hanhuaHost) { throw 'Hanhua mode must remain inside the shared page shell so the mode selector stays reachable.' }
if ($mainPageSource -notmatch 'sender\.SelectedItem == ChatModeItem' -and $mainPageSource -notmatch 'sender.SelectedItem == ChatModeItem') {
    throw 'Chat visibility must key off the chat mode item, not merely not-video.'
}
if ($mainPageSource -notmatch 'HanhuaPanel\.IsBusy' -or $mainPageSource -notmatch 'PrepareForHanhuaGpuAsync') {
    throw 'Main page must arbitrate chat/video against an in-flight hanhua job.'
}

foreach ($automationId in @('VideoPromptInput', 'VideoPresetText', 'VideoDurationSummaryText', 'VideoJobStatus', 'VideoJobProgress', 'StartVideoButton', 'CancelVideoButton', 'OpenVideoOutputButton', 'VideoPreview')) {
    $node = $videoPanel.SelectSingleNode("//*[@AutomationProperties.AutomationId='$automationId']", $videoNs)
    if ($null -eq $node) { throw "Required video control is missing: $automationId" }
}
$videoExpand = $videoPanel.SelectSingleNode("//*[@AutomationProperties.AutomationId='ExpandVideoInputButton']", $videoNs)
if ($null -eq $videoExpand) { throw 'Video composer must expose the shared expand-editor action.' }
if ($null -ne $videoExpand.SelectSingleNode('xaml:Button.Flyout', $videoNs)) { throw 'Video expanded editor must not use an edge-constrained Flyout.' }
$videoStatus = $videoPanel.SelectSingleNode("//*[@AutomationProperties.AutomationId='VideoJobStatus']", $videoNs)
Require-Equal 'TextBox' $videoStatus.LocalName 'Video status must remain selectable and copyable.'
Require-Equal 'True' $videoStatus.IsReadOnly 'Video status must not be editable.'
Require-Equal 'NoWrap' $videoStatus.TextWrapping 'Video status stays a single centered line next to the progress bar.'
if ($null -eq $videoPanel.SelectSingleNode("//*[@AutomationProperties.AutomationId='VideoErrorDetailsButton']", $videoNs)) {
    throw 'Video failures must expose an application-owned full error detail surface.'
}
if ($null -eq $videoPanel.SelectSingleNode("//*[@AutomationProperties.AutomationId='VideoJobPercentText']", $videoNs)) {
    throw 'Video progress must show a percent next to the bar, not duplicated in the status text.'
}
if ($videoPanelSource -notmatch 'VideoErrorPresentation\.Summarize') {
    throw 'Video status must summarize CUDA OOM instead of dumping the backend traceback.'
}
$videoRows = $videoPanel.SelectNodes('/xaml:UserControl/xaml:Grid/xaml:Grid.RowDefinitions/xaml:RowDefinition', $videoNs)
if ($videoRows.Count -ne 3) { throw 'Video mode must share the three-row content/input/footer geometry.' }
Require-Equal '*' $videoRows[0].Height 'Video preview must occupy the shared content row.'
Require-Equal 'Auto' $videoRows[1].Height 'Video prompt card must remain compact when collapsed and grow only for this-job parameters.'
Require-Equal '64' $videoRows[2].Height 'Video actions must occupy the shared 64-high footer row.'
$videoRoot = $videoPanel.SelectSingleNode('/xaml:UserControl/xaml:Grid', $videoNs)
if ($null -ne $videoRoot.Attributes['RowSpacing']) { throw 'Video mode must not add row spacing outside the shared shell budget.' }
$videoPreviewRegion = $videoPanel.SelectSingleNode("//xaml:Grid[@x:Name='VideoPreviewRegion']", $videoNs)
if ($null -eq $videoPreviewRegion) { throw 'Video preview must expose a named available-size region for aspect-fit layout.' }
$videoPreviewFrame = $videoPanel.SelectSingleNode("//xaml:Border[@x:Name='VideoPreviewFrame']", $videoNs)
if ($null -eq $videoPreviewFrame) { throw 'Video preview must expose a named dynamic aspect-fit frame.' }
$videoPreview = $videoPanel.SelectSingleNode("//xaml:MediaPlayerElement[@x:Name='VideoPreview']", $videoNs)
if ($null -eq $videoPreview) { throw 'VideoPreview media player is missing.' }
Require-Equal 'False' $videoPreview.AreTransportControlsEnabled 'VideoPreview transport controls must be disabled until pointer hover or keyboard focus.'
if ($null -eq $videoPreview.SelectSingleNode('xaml:MediaPlayerElement.TransportControls/xaml:MediaTransportControls[@IsCompact="True"]', $videoNs)) {
    throw 'VideoPreview must use compact native media transport controls.'
}
foreach ($eventName in @('PointerEntered', 'PointerExited', 'GotFocus', 'LostFocus')) {
    if (-not $videoPreview.HasAttribute($eventName)) { throw "VideoPreview must handle $eventName to control native transport visibility." }
}
if ($videoPanelSource -notmatch 'VideoPreviewInteraction\.ShouldShowControls') {
    throw 'VideoPreview hover and keyboard focus must share one combined visibility rule.'
}
$videoComposer = $videoPanel.SelectSingleNode('/xaml:UserControl/xaml:Grid/xaml:Border[@Grid.Row="1"]', $videoNs)
Require-Equal '0,10,0,10' $videoComposer.Margin 'Video prompt card must use the same inset as the chat composer.'
$videoPromptLabel = $videoComposer.SelectSingleNode(".//xaml:TextBlock[@x:Name='VideoPromptLabel']", $videoNs)
if ($null -eq $videoPromptLabel) { throw 'Video composer must use a compact prompt label instead of a hero page title.' }
Require-Equal '{StaticResource ComposerTitleTextStyle}' $videoPromptLabel.Style 'Video prompt label must follow the shared compact role-label hierarchy.'
if ($null -ne $videoComposer.SelectSingleNode(".//xaml:TextBlock[@Style='{StaticResource TitleTextStyle}']", $videoNs)) {
    throw 'Video composer must not use page-title typography inside the compact input card.'
}
$videoPreset = $videoPanel.SelectSingleNode("//xaml:TextBlock[@x:Name='VideoPresetText']", $videoNs)
Require-Equal 'Right' $videoPreset.HorizontalAlignment 'Video preset summary must remain secondary and right-aligned.'
Require-Equal '{StaticResource ComposerMetaTextStyle}' $videoPreset.Style 'Video preset summary must share the chat metadata typography.'
$videoDurationSummary = $videoPanel.SelectSingleNode("//xaml:TextBlock[@x:Name='VideoDurationSummaryText']", $videoNs)
Require-Equal '{StaticResource ComposerMetaTextStyle}' $videoDurationSummary.Style 'Video duration summary must share the compact metadata typography.'
$videoPromptInput = $videoPanel.SelectSingleNode("//xaml:TextBox[@x:Name='VideoPromptInput']", $videoNs)
Require-Equal '{StaticResource ComposerInputTextBoxStyle}' $videoPromptInput.Style 'Video input must use the shared composer typography.'
Require-Equal '78' $videoPromptInput.MinHeight 'Video input must keep the shared three-line minimum.'
Require-Equal '188' $videoPromptInput.MaxHeight 'Video input must keep the shared eight-line hard cap.'
$videoEmptyState = $videoPanel.SelectSingleNode("//xaml:TextBlock[@x:Name='VideoEmptyState']", $videoNs)
Require-Equal '{StaticResource StatusTextStyle}' $videoEmptyState.Style 'Chinese empty-state copy must not use the machine-readable mono style.'
$videoComposerRows = $videoComposer.SelectNodes('xaml:Grid/xaml:Grid.RowDefinitions/xaml:RowDefinition', $videoNs)
if ($videoComposerRows.Count -ne 5) {
    throw 'Video composer must use title+mode, editor, optional media strip, collapsible parameters, and hint rows (chat-aligned rhythm).'
}
if ($null -eq $videoComposer.SelectSingleNode(".//xaml:Border[@x:Name='VideoMediaCard']", $videoNs)) {
    throw 'Video composer must expose a media strip for first-frame / reference modes.'
}
if ($null -eq $videoPanel.SelectSingleNode("//*[@AutomationProperties.AutomationId='VideoConditioningMode']", $videoNs)) {
    throw 'Video composer must expose a selectable conditioning mode control on the title row.'
}
if ($null -eq $videoPanel.SelectSingleNode("//*[@AutomationProperties.AutomationId='VideoPromptTagHint']", $videoNs)) {
    throw 'Video composer must expose a prompt-tag hint for image / reference modes.'
}
if ($videoPanelSource -notmatch 'PromptTagHint' -or $videoPanelSource -notmatch 'PromptPlaceholder') {
    throw 'Video panel must bind mode-specific prompt tag hints and placeholders from Core.'
}
$modeBox = $videoPanel.SelectSingleNode("//xaml:ComboBox[@x:Name='VideoConditioningModeBox']", $videoNs)
if ($null -eq $modeBox) {
    throw 'Conditioning mode ComboBox is missing.'
}
$modeInTitleStack = $videoComposer.SelectSingleNode(".//xaml:StackPanel[@Grid.Column='1']//xaml:ComboBox[@x:Name='VideoConditioningModeBox']", $videoNs)
if ($null -eq $modeInTitleStack) {
    throw 'Conditioning mode must sit on the composer title row like chat metadata.'
}
$videoJobParametersCard = $videoComposer.SelectSingleNode(".//xaml:Border[@x:Name='VideoJobParametersCard']", $videoNs)
if ($null -eq $videoJobParametersCard) { throw 'Video composer this-job parameters must keep a dedicated disclosure region.' }
Require-Equal 'Transparent' $videoJobParametersCard.Background 'Collapsed parameter disclosure must not render a second filled card inside the composer.'
Require-Equal '0,1,0,0' $videoJobParametersCard.BorderThickness 'Collapsed parameter disclosure must use one quiet divider instead of a nested outline.'
$videoJobParametersToggle = $videoJobParametersCard.SelectSingleNode("xaml:Grid/xaml:Button[@x:Name='VideoJobParametersToggle']", $videoNs)
if ($null -eq $videoJobParametersToggle) { throw 'Video composer must use a compact semantic button for the parameter disclosure row.' }
Require-Equal 'Transparent' $videoJobParametersToggle.Background 'Parameter disclosure button must not add a default filled header slab.'
Require-Equal 'Stretch' $videoJobParametersToggle.HorizontalContentAlignment 'Parameter disclosure summary must fill the available row.'
if (-not $videoJobParametersToggle.HasAttribute('Click')) { throw 'Parameter disclosure button must toggle its downward details region.' }
$videoJobParametersDetails = $videoJobParametersCard.SelectSingleNode("xaml:Grid/xaml:Border[@x:Name='VideoJobParametersDetails']", $videoNs)
if ($null -eq $videoJobParametersDetails) { throw 'Video composer this-job parameter details surface is missing.' }
Require-Equal 'Collapsed' $videoJobParametersDetails.Visibility 'This-job parameter details must remain collapsed by default.'
Require-Equal '10' $videoJobParametersDetails.CornerRadius 'Expanded details must retain the shared rounded surface language.'
$videoJobParameters = $videoJobParametersDetails.SelectSingleNode("xaml:Grid", $videoNs)
if ($null -eq $videoJobParameters) { throw 'Video composer this-job parameter grid is missing.' }
Require-Equal '3' $videoJobParameters.SelectNodes('xaml:Grid.ColumnDefinitions/xaml:ColumnDefinition', $videoNs).Count 'This-job parameters must use three columns so the footer remains visible at the baseline window height.'
Require-Equal '3' $videoJobParameters.SelectNodes('xaml:Grid.RowDefinitions/xaml:RowDefinition', $videoNs).Count 'This-job parameters must use two control rows plus one reset-action row.'
foreach ($parameterBox in $videoJobParameters.SelectNodes('xaml:NumberBox', $videoNs)) {
    Require-Equal '{StaticResource ComposerParameterNumberBoxStyle}' $parameterBox.Style "This-job NumberBox '$($parameterBox.Name)' must stretch inside the composer grid instead of using the fixed settings-drawer width."
}
if ($videoPanelSource.Contains([string][char]0x00B7)) { throw 'Video summary and completion status must not use middle-dot separators.' }
if ($mainPageSource.Contains([string][char]0x00B7) -or $settingsPanelSource.Contains([string][char]0x00B7)) {
    throw 'Runtime UI strings must not use middle-dot separators.'
}
if ($videoPanelSource -match 'VideoPromptInput_PreviewKeyDown' -eq $false -or
    $videoPanelSource -notmatch 'ChatInteractionPolicy\.ResolveEnter' -or
    $videoPanelSource -notmatch 'InputHistoryNavigator') {
    throw 'Video prompt input must reuse chat Enter, Shift+Enter, and history behavior.'
}
if ($mainPageSource -notmatch 'ComposerTextBoxLayout\.Apply\(InputBox, ActualHeight\)' -or
    $videoPanelSource -notmatch 'ComposerTextBoxLayout\.Apply') {
    throw 'Chat and video inputs must share the bounded adaptive height implementation.'
}
if ($mainPageSource -notmatch 'NoticeText\.Visibility\s*=\s*chat\s*\?\s*Visibility\.Visible' -and
    $mainPageSource -notmatch 'NoticeText\.Visibility\s*=\s*video\s*\?\s*Visibility\.Collapsed') {
    throw 'Chat notice text must be hidden while the video or hanhua page is active.'
}
$rebuildThenAnchor = [regex]::Match($mainPageSource, 'RebuildVisibleTranscriptFromSession\(gen\.Session, liveGen:\s*null\);[\s\S]{0,200}RestoreQuestionAnchorAfterRebuild').Success -and
    [regex]::Match($mainPageSource, 'RestoreQuestionAnchorAfterRebuild[\s\S]{0,1000}ScrollTranscriptToQuestion').Success
if (-not $rebuildThenAnchor) { throw 'Reply completion must scroll to the rebuilt question entry after canonical transcript rebuild.' }
foreach ($automationId in @('TextImportButton', 'ReleaseTextModelButton', 'VideoImportButton', 'ReleaseVideoModelButton', 'StartupModelSetting')) {
    $node = $settingsPanel.SelectSingleNode("//*[@AutomationProperties.AutomationId='$automationId']", $settingsNs)
    if ($null -eq $node) { throw "Required model-card control is missing: $automationId" }
}
foreach ($automationId in @('VideoOutputFormatSetting', 'VideoCodecSetting')) {
    if ($null -eq $settingsPanel.SelectSingleNode("//*[@AutomationProperties.AutomationId='$automationId']", $settingsNs)) {
        throw "Video SaveVideo output setting is missing: $automationId"
    }
}
foreach ($removedId in @('StartTextModelButton', 'StartVideoModelButton')) {
    if ($null -ne $settingsPanel.SelectSingleNode("//*[@AutomationProperties.AutomationId='$removedId']", $settingsNs)) {
        throw "High-frequency model start action must not remain hidden in settings: $removedId"
    }
}
foreach ($frontId in @('StartTextModelPageButton', 'StartVideoModelPageButton')) {
    if ($null -eq $mainPage.SelectSingleNode("//*[@AutomationProperties.AutomationId='$frontId']", $pageNs)) {
        throw "Each main mode must expose its current model start action: $frontId"
    }
}
if ($mainPageSource -notmatch 'Microsoft\.Windows\.Storage\.Pickers\.FolderPicker|FolderPicker\s*=\s*Microsoft\.Windows\.Storage\.Pickers\.FolderPicker') {
    throw 'Model directory selection must use Microsoft.Windows.Storage.Pickers.FolderPicker.'
}
if ($mainWindowSource -notmatch 'public\s+WindowId\s+AppWindowId\s*=>\s*_appWindow\.Id' -or
    $mainPageSource -notmatch 'new FolderPicker\(window\.AppWindowId\)' -or
    $mainPageSource -match 'SettingsPanel\.XamlRoot\.ContentIslandEnvironment\.AppWindowId') {
    throw 'Model directory selection must use the top-level MainWindow AppWindowId and surface picker failures.'
}
if ($mainPageSource -notmatch 'ApplyStartupModelSelectionAsync' -or
    $mainPageSource -notmatch 'StartupModelSelection\.Video' -or
    $mainPageSource -notmatch 'StartupModelSelection\.None') {
    throw 'Client startup must route the persisted text/video/none selection.'
}
foreach ($summaryName in @('TextModelSummaryText', 'VideoModelSummaryText')) {
    $summary = $mainPage.SelectSingleNode("//xaml:TextBlock[@x:Name='$summaryName']", $pageNs)
    if ($null -eq $summary) { throw "Mode toolbar must expose the selected model identity: $summaryName" }
    Require-Equal 'Right' $summary.HorizontalAlignment "Selected model identity '$summaryName' must remain visually secondary."
}
$videoProgress = $videoPanel.SelectSingleNode("//xaml:ProgressBar[@x:Name='VideoJobProgress']", $videoNs)
Require-Equal 'Collapsed' $videoProgress.Visibility 'Idle video progress must not occupy a visible line.'
$videoCommandSurface = $videoPanel.SelectSingleNode("//xaml:Border[@x:Name='VideoCommandSurface']", $videoNs)
if ($null -eq $videoCommandSurface) { throw 'Video footer must present status and actions as one intentional command surface.' }
Require-Equal '2' $videoCommandSurface.Attributes['Grid.Row'].Value 'Video command surface must occupy the shared footer row.'
Require-Equal '{StaticResource RaisedBrush}' $videoCommandSurface.Background 'Video command surface must separate itself from the empty canvas.'
Require-Equal '1' $videoCommandSurface.BorderThickness 'Video command surface must retain one quiet boundary.'
Require-Equal '12' $videoCommandSurface.CornerRadius 'Video command surface corner radius drifted.'
Require-Equal '12,8' $videoCommandSurface.Padding 'Video command surface padding must follow the compact 4px-grid rhythm.'
if ($videoProgress.SelectSingleNode("ancestor::xaml:Border[@x:Name='VideoCommandSurface']", $videoNs) -ne $videoCommandSurface) {
    throw 'Busy progress must remain inside the unified video command surface.'
}
$cancelVideo = $videoPanel.SelectSingleNode("//xaml:Button[@x:Name='CancelVideoButton']", $videoNs)
Require-Equal 'Collapsed' $cancelVideo.Visibility 'Cancel action must only appear while a video job is active.'
foreach ($buttonName in @('VideoSettingsButton', 'OpenVideoOutputButton', 'CancelVideoButton')) {
    $button = $videoPanel.SelectSingleNode("//xaml:Button[@x:Name='$buttonName']", $videoNs)
    Require-Equal '{StaticResource ToolbarButtonStyle}' $button.Style "Video action '$buttonName' must use the shared compact toolbar sizing."
}
$videoSettings = $videoPanel.SelectSingleNode("//xaml:Button[@x:Name='VideoSettingsButton']", $videoNs)
$chatSettings = $mainPage.SelectSingleNode("//xaml:Button[@x:Name='SettingsButton']", $pageNs)
Require-Equal $chatSettings.Content $videoSettings.Content 'Video settings must use the same concise label as chat settings.'
if ($videoSettings.SelectSingleNode("ancestor::xaml:Border[@x:Name='VideoCommandSurface']", $videoNs) -ne $videoCommandSurface) {
    throw 'Video settings must align with chat settings in the bottom-right action surface.'
}
$startVideo = $videoPanel.SelectSingleNode("//xaml:Button[@x:Name='StartVideoButton']", $videoNs)
Require-Equal '{StaticResource VideoPrimaryButtonStyle}' $startVideo.Style 'Generate action must use the compact primary video style.'
foreach ($actionName in @('VideoSettingsButton', 'OpenVideoOutputButton', 'CancelVideoButton', 'StartVideoButton')) {
    $action = $videoPanel.SelectSingleNode("//xaml:Button[@x:Name='$actionName']", $videoNs)
    if ($action.SelectSingleNode("ancestor::xaml:Border[@x:Name='VideoCommandSurface']", $videoNs) -ne $videoCommandSurface) {
        throw "Video action '$actionName' must belong to the unified command surface."
    }
}
if ($videoPanelSource -notmatch 'VideoJobProgress\.Visibility\s*=\s*generating\s*\?\s*Visibility\.Visible\s*:\s*Visibility\.Collapsed' -or
    $videoPanelSource -notmatch 'CancelVideoButton\.Visibility\s*=\s*generating\s*\?\s*Visibility\.Visible\s*:\s*Visibility\.Collapsed') {
    throw 'Busy-only video progress and cancel visibility must be synchronized with generation state.'
}
if ($null -eq $videoPanel.SelectSingleNode("//*[@AutomationProperties.AutomationId='VideoSettingsButton']", $videoNs)) {
    throw 'Video footer must expose a reachable video settings button.'
}
if ($videoPanelSource -notmatch 'EnsureAvailableAsync' -or $videoPanelSource -notmatch 'CancelAsync') {
    throw 'Video panel must start the verified local service and support exact job cancellation.'
}
if ($videoPanelSource -notmatch 'StopServiceAsync') {
    throw 'Exit-and-stop must stop the configured video service, including a safely identified reused listener.'
}
if ($mainPageSource -notmatch 'PrepareForVideoAsync' -or $mainPageSource -notmatch 'VideoPanel\.ReleaseModelAsync') {
    throw 'Main page must retain explicit chat/video GPU arbitration.'
}
$hanhuaRows = $hanhuaPanel.SelectNodes('/xaml:UserControl/xaml:Grid/xaml:Grid.RowDefinitions/xaml:RowDefinition', $hanhuaNs)
if ($hanhuaRows.Count -ne 3) { throw 'Hanhua page must reuse the preview / composer / 64-high command rhythm.' }
Require-Equal '64' $hanhuaRows[2].Height 'Hanhua command surface must stay 64 high.'
if ($null -eq $hanhuaPanel.SelectSingleNode("//xaml:ComboBox[@x:Name='EngineBox']", $hanhuaNs)) {
    throw 'Hanhua composer must expose the local/cloud engine dropdown.'
}
if ($null -eq $hanhuaPanel.SelectSingleNode("//xaml:Button[@x:Name='ComposerStartButton']", $hanhuaNs)) {
    throw 'Hanhua composer must keep the single start button next to the folder picker.'
}
if ($null -ne $hanhuaPanel.SelectSingleNode("//xaml:Button[@x:Name='StartHanhuaButton']", $hanhuaNs)) {
    throw 'Hanhua footer must not repeat the start button already on the composer.'
}
if ($null -eq $hanhuaPanel.SelectSingleNode("//xaml:Border[@x:Name='HanhuaCommandSurface']", $hanhuaNs)) {
    throw 'Hanhua footer must use a unified command surface.'
}
if ($null -eq $hanhuaPanel.SelectSingleNode("//xaml:StackPanel[@x:Name='HanhuaEmptyState']", $hanhuaNs)) {
    throw 'Hanhua main pane must explain that it is the task log when idle.'
}
if ($hanhuaPanelSource -notmatch 'HanhuaProgressStatus' -or $hanhuaPanelSource -notmatch 'StartElapsedTicker') {
    throw 'Hanhua footer must reuse video-style elapsed/ETA progress with a one-second clock.'
}
if ($null -eq $hanhuaPanel.SelectSingleNode("//xaml:TextBlock[@x:Name='HanhuaComposerHint']", $hanhuaNs)) {
    throw 'Hanhua composer must explain the automatic pipeline and that the toolbar Start button is not required.'
}
if ($hanhuaPanelSource -notmatch 'HanhuaErrorPresentation') {
    throw 'Hanhua failures must use a short Chinese summary like video errors.'
}
if ($null -eq $settingsPanel.SelectSingleNode("//xaml:TextBox[@x:Name='HanhuaPackRootBox']", $settingsNs)) {
    throw 'Hanhua pack path must live in the text-model local-service card.'
}
if ([regex]::Matches($mainPageSource, 'EnforceTextVideoModelExclusivity').Count -lt 3) {
    throw 'The shared model-residency switch must govern chat start, video start, and request-time release paths.'
}
$closeMethod = [regex]::Match($mainPageSource, 'public\s+Task<AppCloseDecision>\s+RequestCloseDecisionAsync\(\)[\s\S]*?private void CloseStopModelButton_Click').Value
if ([string]::IsNullOrWhiteSpace($closeMethod) -or $closeMethod -match 'await|IsHealthyAsync|IsServiceRunningAsync') {
    throw 'The exit prompt must render immediately without probing model-service health.'
}
$closeStop = $mainPage.SelectSingleNode("//xaml:Button[@x:Name='CloseStopModelButton']", $pageNs)
$closeKeep = $mainPage.SelectSingleNode("//xaml:Button[@x:Name='CloseKeepModelButton']", $pageNs)
$closeCancel = $mainPage.SelectSingleNode("//xaml:Button[@x:Name='CloseCancelButton']", $pageNs)
if ($null -eq $closeStop -or $null -eq $closeKeep -or $null -eq $closeCancel -or $closeStop.HasAttribute('Visibility')) {
    throw 'The inline exit prompt must always expose stop-services, keep-services, and cancel actions.'
}
Require-Equal $baseline.interaction.exitPrompt.primaryAction $closeStop.Content 'The primary exit action must explicitly close model services.'
Require-Equal $baseline.interaction.exitPrompt.secondaryAction $closeKeep.Content 'The secondary exit action must keep model services running.'
Require-Equal $baseline.interaction.exitPrompt.cancelAction $closeCancel.Content 'The exit prompt must retain cancel.'
# Prefer select-when-file-exists via CreateExplorerRevealStartInfo; never open the default player.
$revealSource = Get-Content -Raw -LiteralPath (Join-Path $root 'src\QwenLocalChat.Core\WindowsFileReveal.cs')
if ($videoPanelSource -notmatch 'WindowsFileReveal\.CreateExplorerRevealStartInfo' -or $videoPanelSource -match 'Launcher\.LaunchFileAsync') {
    throw 'Video output must reveal via Explorer (select file or open output folder) instead of opening the player.'
}
if ($revealSource -notmatch 'CreateExplorerSelectStartInfo' -or $revealSource -notmatch '/select,') {
    throw 'Explorer reveal helper must still support selecting an existing output file with /select.'
}
$openVideoOutput = $videoPanel.SelectSingleNode("//xaml:Button[@x:Name='OpenVideoOutputButton']", $videoNs)
Require-Equal $baseline.interaction.videoOutputReveal.actionLabel $openVideoOutput.Content 'The video output action label must describe file location.'
if ($null -eq $settingsPanel.SelectSingleNode("//*[@AutomationProperties.AutomationId='ModelExclusivitySetting']", $settingsNs)) {
    throw 'Settings must expose the shared text-video model exclusivity switch.'
}
if ($settingsPanelSource -notmatch 'EnforceTextVideoModelExclusivity\s*=\s*ModelExclusivityToggle\.IsOn') {
    throw 'The shared model exclusivity switch must participate in settings save and dirty checks.'
}
foreach ($automationId in @('VideoWidthSetting', 'VideoHeightSetting', 'VideoDurationSetting', 'VideoStepsSetting', 'VideoSeedSetting', 'VideoRandomSeedSetting', 'VideoPromptPhrasesExpander', 'ResetVideoPromptPhrasesButton')) {
    $node = $settingsPanel.SelectSingleNode("//*[@AutomationProperties.AutomationId='$automationId']", $settingsNs)
    if ($null -eq $node) { throw "Required video settings control is missing: $automationId" }
}
foreach ($automationId in @('VideoJobParametersExpander', 'VideoJobWidthSetting', 'VideoJobHeightSetting', 'VideoJobDurationSetting', 'VideoJobStepsSetting', 'VideoJobSeedSetting', 'VideoJobRandomSeedSetting', 'ResetVideoJobParametersButton')) {
    if ($null -eq $videoPanel.SelectSingleNode("//*[@AutomationProperties.AutomationId='$automationId']", $videoNs)) {
        throw "This-job video parameter control is missing: $automationId"
    }
}
if ($videoPanelSource -notmatch 'VideoGenerationOverrides' -or $videoPanelSource -notmatch '\.ApplyTo\(CurrentSettings\)') {
    throw 'Video composer must resolve this-job overrides in memory without writing global settings.'
}
if ($videoPanelSource -match 'SettingsStore' -or $videoPanelSource -match '_settingsStore') {
    throw 'Video composer must not own the persistent settings store; this-job parameters are request-local.'
}
foreach ($automationId in @('VideoProfileDisplayNameSetting', 'VideoProfileFpsSetting', 'VideoProfileAlignmentMultipleSetting', 'VideoProfileAlignmentOffsetSetting', 'VideoProfileMinimumDimensionSetting', 'VideoProfileMaximumDimensionSetting', 'VideoProfileDimensionStepSetting', 'VideoProfileMaximumPixelsSetting', 'VideoProfileMinimumDurationSetting', 'VideoProfileMaximumDurationSetting', 'VideoProfileMinimumStepsSetting', 'VideoProfileMaximumStepsSetting', 'VideoProfileMaxReferenceImagesSetting', 'VideoProfileRefImageSizeSetting', 'VideoProfileMaxReferenceVideosSetting', 'VideoProfileMaxReferenceAudiosSetting', 'VideoProfileTextToVideoSetting', 'VideoProfileFirstFrameSetting', 'VideoProfileFirstLastFrameSetting', 'VideoProfileDefaultModeSetting', 'VideoProfileCfgSetting', 'VideoProfileDenoiseSetting', 'VideoProfileSamplerSetting', 'VideoProfileSchedulerSetting', 'VideoProfileShiftVideoSetting', 'VideoProfileShiftAudioSetting', 'VideoProfileNegativePromptSetting')) {
    if ($null -eq $settingsPanel.SelectSingleNode("//*[@AutomationProperties.AutomationId='$automationId']", $settingsNs)) {
        throw "Editable video profile field is missing: $automationId"
    }
}
if ($settingsPanelSource -notmatch 'EditedVideoProfile' -or $mainPageSource -notmatch '_videoProfileStore\.Save') {
    throw 'Saving settings must include the edited video profile through the catalog store.'
}
$settingsContentScroll = $settingsPanel.SelectSingleNode("//xaml:ScrollViewer[@x:Name='SettingsContentScrollViewer']", $settingsNs)
if ($null -eq $settingsContentScroll) { throw 'Settings content must expose a named scroll owner so each section can reopen at the top.' }
$videoProfileExpander = $settingsPanel.SelectSingleNode("//xaml:Expander[@x:Name='VideoProfileParametersExpander']", $settingsNs)
Require-Equal 'Down' $videoProfileExpander.ExpandDirection 'Video profile parameters must expand downward inside the settings drawer.'
if ($settingsPanelSource -notmatch 'SettingsContentScrollViewer\.ChangeView' -or $settingsPanelSource -notmatch 'VideoProfileParametersExpander\.IsExpanded\s*=\s*videoSection') {
    throw 'Opening or switching settings must return to the top and reveal video profile capability editing.'
}
foreach ($automationId in @('TextModelProfileSetting', 'VideoModelProfileSetting')) {
    $node = $settingsPanel.SelectSingleNode("//*[@AutomationProperties.AutomationId='$automationId']", $settingsNs)
    if ($null -eq $node) { throw "Each settings mode must expose a native model profile selector: $automationId" }
    Require-Equal 'ComboBox' $node.LocalName "Model profile selector '$automationId' must keep native keyboard and accessibility behavior."
}
if ($null -eq $settingsPanel.SelectSingleNode("//xaml:SelectorBar[@x:Name='SettingsSectionSelector']", $settingsNs)) {
    throw 'Settings drawer must expose text and video model sections.'
}
if ($null -eq $settingsPanel.SelectSingleNode("//xaml:StackPanel[@x:Name='TextSettingsSection']", $settingsNs)) {
    throw 'Settings drawer must isolate text settings in a selectable section.'
}
if ($settingsPanelSource -notmatch 'SettingsSectionSelector_SelectionChanged' -or $settingsPanelSource -notmatch 'VideoSettingsSection\.Visibility') {
    throw 'Settings selector must actually toggle text and video section visibility.'
}
if ($settingsPanelSource -notmatch 'VideoGeneration\s*=\s*new VideoGenerationSettings') {
    throw 'Video settings fields must participate in save and dirty checks.'
}
if ($settingsPanelSource -notmatch 'VideoGeneration\s*=\s*BuildDraft\(\)\.VideoGeneration') {
    throw 'Resetting text settings must preserve the current video draft.'
}
if ($settingsPanelSource -notmatch 'SelectedTextProfileId' -or $settingsPanelSource -notmatch 'SelectedVideoProfileId') {
    throw 'Both profile selections must participate in settings save and dirty checks.'
}
if ($videoPanelSource -notmatch 'ComfyUiWorkflowVideoClient' -or $videoPanelSource -match 'MiniMax H3') {
    throw 'Video UI must use the selected workflow profile and must not hard-code the currently deployed video model name.'
}
if ($videoPanelSource -notmatch 'RestoreActiveSessionPreviewAsync' -or $videoPanelSource -notmatch 'VideoPreviewHistory\.ResolveSessionPreview') {
    throw 'Video UI must restore the bound clip of the active video window, not the newest file in the output folder.'
}
if ($mainPageSource -notmatch 'VideoSessionPicker' -or $mainPageSource -notmatch 'NewVideoSessionButton' -or $videoPanelSource -notmatch 'ClearActiveSessionAsync') {
    throw 'Video mode must reuse chat session chrome: picker, new window, and clear current window.'
}
if ($mainPage.OuterXml -match 'MINIMAX H3') {
    throw 'The mode toolbar must render its video model name from the selected profile.'
}
if ($mainPageSource -notmatch 'TranscriptPresentationPolicy\.AssistantLabel' -or $mainPageSource -match 'AppendTurn\("Qwen"') {
    throw 'New assistant turns must use a generic role label instead of a hard-coded text model name.'
}

$toggleHover = $baseline.interaction.toggleHover
Require-Equal 'platform-default' $toggleHover.mode 'ToggleSwitch hover contract must stay on platform defaults while the native crash A/B is open.'
foreach ($forbiddenKey in @($toggleHover.forbiddenResourceKeys)) {
    Forbid-ResourceKey $appXaml $appNs ([string]$forbiddenKey)
}

[pscustomobject]@{
    Result = 'PASS'
    Baseline = $baselinePath
    ChangeLedger = $changeLedgerPath
    TranscriptBodyStyle = $baseline.typography.transcriptBodyStyle
    TranscriptBodyFontSize = [double]$fontSize
    TranscriptBodyLineHeight = [double]$lineHeight
    InputFontSize = [double]$composerInputStyle.SelectSingleNode("xaml:Setter[@Property='FontSize']", $appNs).Value
    ToggleHoverContract = 'platform-default-no-pointerover-overrides'
} | ConvertTo-Json
