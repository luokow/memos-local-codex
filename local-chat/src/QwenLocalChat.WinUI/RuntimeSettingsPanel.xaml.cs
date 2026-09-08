using System.Numerics;
using Microsoft.UI.Composition;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Hosting;
using Microsoft.UI.Xaml.Media;
using QwenLocalChat.Core;
using Windows.Globalization.NumberFormatting;

namespace QwenLocalChat_WinUI;

public sealed class SettingsSaveRequestedEventArgs(LocalChatSettings settings, VideoModelProfile editedVideoProfile) : EventArgs
{
    public LocalChatSettings Settings { get; } = settings;
    public VideoModelProfile EditedVideoProfile { get; } = editedVideoProfile;
}

public sealed partial class RuntimeSettingsPanel : UserControl
{
    private const double TemperatureStep = 0.1;
    private const double PenaltyStep = 0.05;
    private const double RepeatPenaltyStep = 0.01;
    private const float ClosedSlideX = 520f;
    private LocalChatSettings _original = LocalChatSettings.SafeDefaults;
    private ModelProfileCatalog? _textProfiles;
    private VideoModelProfileCatalog? _videoProfiles;
    private bool _populatingProfiles;
    private bool _isClosing;
    private bool _compositionReady;
    private CompositionScopedBatch? _closeBatch;
    private readonly Dictionary<string, TextBox> _phraseBoxes = new(StringComparer.Ordinal);

    public RuntimeSettingsPanel()
    {
        InitializeComponent();
        ConfigureTemperatureBox();
        ConfigurePenaltyBoxes();
        ConfigureIntegerBoxes();
        Loaded += (_, _) =>
        {
            EnsureCompositionReady();
            AttachNumberBoxEditDisplay();
        };
        VideoProfileParametersExpander.Expanding += (_, _) => AttachNumberBoxEditDisplay();
        EnsurePhraseEditors();
    }

    public event EventHandler<SettingsSaveRequestedEventArgs>? SaveRequested;
    public event EventHandler? CloseRequested;
    public event EventHandler? TextImportRequested;
    public event EventHandler? VideoImportRequested;
    public event EventHandler? ReleaseTextModelRequested;
    public event EventHandler? ReleaseVideoModelRequested;
    /// <summary>Raised after the panel is fully closed (animation finished or instant hide).</summary>
    public event EventHandler? Closed;

    public void Open(
        LocalChatSettings settings,
        ModelProfileCatalog textProfiles,
        VideoModelProfileCatalog videoProfiles,
        bool videoSection = false)
    {
        _isClosing = false;
        DetachCloseBatch();
        _original = settings;
        _textProfiles = textProfiles;
        _videoProfiles = videoProfiles;
        HideDiscardConfirm();
        Populate(settings);
        SettingsSectionSelector.SelectedItem = videoSection ? VideoSettingsSectionItem : TextSettingsSectionItem;
        ApplySectionVisibility(videoSection);
        IsEnabled = true;
        IsHitTestVisible = true;
        Opacity = 1;
        PanelShell.Opacity = 1;
        EnsureCompositionReady();
        PlayOpenAnimation();
        ResetSettingsScroll();
    }

    public void Close()
    {
        if (_isClosing) return;

        if (Opacity <= 0.01)
        {
            ApplyClosedVisualState(raiseClosed: true);
            return;
        }

        _isClosing = true;
        // Block hits only. Never IsEnabled=false during motion (restyles the whole form).
        IsHitTestVisible = false;
        PlayCloseAnimation();
    }

    private void EnsureCompositionReady()
    {
        if (_compositionReady) return;
        // Independent compositor translation — avoids XAML Storyboard/RenderTransform jank.
        ElementCompositionPreview.SetIsTranslationEnabled(PanelShell, true);
        var visual = ElementCompositionPreview.GetElementVisual(PanelShell);
        visual.Properties.InsertVector3("Translation", new Vector3(ClosedSlideX, 0, 0));
        _compositionReady = true;
    }

    private Visual PanelVisual => ElementCompositionPreview.GetElementVisual(PanelShell);

    private void ApplyClosedVisualState(bool raiseClosed)
    {
        DetachCloseBatch();
        _isClosing = false;
        HideDiscardConfirm();
        IsEnabled = false;
        IsHitTestVisible = false;
        Opacity = 0;
        Scrim.Opacity = 0;
        ShadowStrip.Opacity = 0;
        if (_compositionReady)
        {
            var visual = PanelVisual;
            visual.StopAnimation("Translation");
            visual.Properties.InsertVector3("Translation", new Vector3(ClosedSlideX, 0, 0));
        }

        if (raiseClosed)
            Closed?.Invoke(this, EventArgs.Empty);
    }

    private void PlayOpenAnimation()
    {
        DetachCloseBatch();
        var visual = PanelVisual;
        visual.StopAnimation("Translation");

        // Scrim/shadow snap on — no concurrent full-screen opacity storyboard.
        Scrim.Opacity = 1;
        ShadowStrip.Opacity = 1;
        visual.Properties.InsertVector3("Translation", new Vector3(ClosedSlideX, 0, 0));

        var compositor = visual.Compositor;
        var slide = compositor.CreateVector3KeyFrameAnimation();
        slide.InsertKeyFrame(1f, Vector3.Zero);
        slide.Duration = TimeSpan.FromMilliseconds(180);
        slide.StopBehavior = AnimationStopBehavior.SetToFinalValue;
        visual.StartAnimation("Translation", slide);
    }

    private void PlayCloseAnimation()
    {
        DetachCloseBatch();
        EnsureCompositionReady();
        var visual = PanelVisual;
        visual.StopAnimation("Translation");

        // Drop cheap overlays immediately so only the drawer slides on the compositor.
        Scrim.Opacity = 0;
        ShadowStrip.Opacity = 0;
        Opacity = 1;
        PanelShell.Opacity = 1;

        var compositor = visual.Compositor;
        _closeBatch = compositor.CreateScopedBatch(CompositionBatchTypes.Animation);
        _closeBatch.Completed += CloseBatch_Completed;

        var slide = compositor.CreateVector3KeyFrameAnimation();
        slide.InsertKeyFrame(1f, new Vector3(ClosedSlideX, 0, 0));
        slide.Duration = TimeSpan.FromMilliseconds(150);
        slide.StopBehavior = AnimationStopBehavior.SetToFinalValue;
        visual.StartAnimation("Translation", slide);
        _closeBatch.End();
    }

    private void CloseBatch_Completed(object sender, CompositionBatchCompletedEventArgs args)
    {
        DetachCloseBatch();
        // Composition callbacks are not guaranteed on the UI thread.
        DispatcherQueue.TryEnqueue(() =>
        {
            if (!_isClosing) return;
            ApplyClosedVisualState(raiseClosed: true);
        });
    }

    private void DetachCloseBatch()
    {
        if (_closeBatch is null) return;
        _closeBatch.Completed -= CloseBatch_Completed;
        try { _closeBatch.Dispose(); } catch { /* already ended */ }
        _closeBatch = null;
        if (_compositionReady)
            PanelVisual.StopAnimation("Translation");
    }

    private void ConfigureTemperatureBox()
    {
        // Code-side doubles + IncrementNumberRounder keep spin buttons on clean 0.1 steps.
        // Default NumberBox formatting shows float noise (0.799999999) and XAML "0.1" is unreliable.
        var rounder = new IncrementNumberRounder
        {
            Increment = TemperatureStep,
            RoundingAlgorithm = RoundingAlgorithm.RoundHalfUp,
        };
        TemperatureBox.NumberFormatter = new DecimalFormatter
        {
            IntegerDigits = 1,
            FractionDigits = 1,
            IsGrouped = false,
            NumberRounder = rounder,
        };
        TemperatureBox.SmallChange = TemperatureStep;
        TemperatureBox.LargeChange = TemperatureStep;
    }

    private void SettingsSectionSelector_SelectionChanged(SelectorBar sender, SelectorBarSelectionChangedEventArgs args)
        => ApplySectionVisibility(sender.SelectedItem == VideoSettingsSectionItem);

    private void ApplySectionVisibility(bool videoSection)
    {
        TextSettingsSection.Visibility = videoSection ? Visibility.Collapsed : Visibility.Visible;
        VideoSettingsSection.Visibility = videoSection ? Visibility.Visible : Visibility.Collapsed;
        VideoProfileParametersExpander.IsExpanded = videoSection;
        ResetSettingsScroll();
    }

    private void ResetSettingsScroll()
    {
        SettingsContentScrollViewer.UpdateLayout();
        SettingsContentScrollViewer.ChangeView(null, 0, null, true);
        DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, () =>
        {
            SettingsContentScrollViewer.UpdateLayout();
            SettingsContentScrollViewer.ChangeView(null, 0, null, true);
        });
    }

    private void ConfigurePenaltyBoxes()
    {
        ConfigureDecimalBox(FrequencyPenaltyBox, PenaltyStep, fractionDigits: 2);
        ConfigureDecimalBox(PresencePenaltyBox, PenaltyStep, fractionDigits: 2);
        ConfigureDecimalBox(RepeatPenaltyBox, RepeatPenaltyStep, fractionDigits: 2);
        ConfigureDecimalBox(DryMultiplierBox, PenaltyStep, fractionDigits: 2);
    }

    private void AttachNumberBoxEditDisplay()
    {
        foreach (var box in EnumerateDescendants<NumberBox>(this))
            NumberBoxEditDisplay.Attach(box);
    }

    private void ConfigureIntegerBoxes()
    {
        foreach (var box in new[]
        {
            MaxOutputTokensBox, RequestTimeoutBox, RepeatLastNBox, ContextSizeBox,
            MaxHistoryRoundsBox, MemosTopKBox, GpuLayersBox, ParallelSlotsBox,
            StartupTimeoutBox, PortBox, ChatAttachMaxFilesBox, ChatAttachMaxKbBox,
            VideoProfileFpsBox, VideoProfileAlignmentMultipleBox, VideoProfileAlignmentOffsetBox,
            VideoProfileDimensionStepBox, VideoProfileMinimumDimensionBox, VideoProfileMaximumDimensionBox,
            VideoProfileMaximumPixelsBox, VideoProfileMinimumDurationBox, VideoProfileMaximumDurationBox,
            VideoProfileMinimumStepsBox, VideoProfileMaximumStepsBox,
            VideoProfileMaxReferenceImagesBox, VideoProfileMaxReferenceVideosBox, VideoProfileMaxReferenceAudiosBox,
            VideoWidthBox, VideoHeightBox, VideoDurationBox, VideoStepsBox, VideoSeedBox,
        })
        {
            box.NumberFormatter = new DecimalFormatter
            {
                IntegerDigits = 1,
                FractionDigits = 0,
                IsGrouped = false,
            };
        }
    }

    private static IEnumerable<T> EnumerateDescendants<T>(DependencyObject root) where T : DependencyObject
    {
        var count = VisualTreeHelper.GetChildrenCount(root);
        for (var i = 0; i < count; i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is T match)
                yield return match;
            foreach (var nested in EnumerateDescendants<T>(child))
                yield return nested;
        }
    }

    private static void ConfigureDecimalBox(NumberBox box, double step, int fractionDigits)
    {
        var rounder = new IncrementNumberRounder
        {
            Increment = step,
            RoundingAlgorithm = RoundingAlgorithm.RoundHalfUp,
        };
        box.NumberFormatter = new DecimalFormatter
        {
            IntegerDigits = 1,
            FractionDigits = fractionDigits,
            IsGrouped = false,
            NumberRounder = rounder,
        };
        box.SmallChange = step;
        box.LargeChange = step;
    }

    private void Populate(LocalChatSettings settings)
    {
        _populatingProfiles = true;
        TextModelProfileBox.ItemsSource = _textProfiles?.Profiles;
        VideoModelProfileBox.ItemsSource = _videoProfiles?.Profiles;
        if (_textProfiles is not null) TextModelProfileBox.SelectedItem = _textProfiles.Resolve(settings.SelectedTextProfileId);
        if (_videoProfiles is not null) VideoModelProfileBox.SelectedItem = _videoProfiles.Resolve(settings.SelectedVideoProfileId);
        MaxOutputTokensBox.Value = settings.MaxOutputTokens;
        TemperatureBox.Value = SettingsNumberInput.Temperature(settings.Temperature);
        FrequencyPenaltyBox.Value = SettingsNumberInput.OpenAiPenalty(settings.FrequencyPenalty);
        PresencePenaltyBox.Value = SettingsNumberInput.OpenAiPenalty(settings.PresencePenalty);
        RepeatPenaltyBox.Value = SettingsNumberInput.RepeatPenalty(settings.RepeatPenalty);
        DryMultiplierBox.Value = SettingsNumberInput.DryMultiplier(settings.DryMultiplier);
        RepeatLastNBox.Value = settings.RepeatLastN;
        StreamResponsesToggle.IsOn = settings.StreamResponses;
        ModelExclusivityToggle.IsOn = settings.EnforceTextVideoModelExclusivity;
        SelectStartupModel(settings.StartupModel);
        AutoTightenOutputToggle.IsOn = settings.AutoTightenOutputTokens;
        SegmentedLongFormToggle.IsOn = settings.SegmentedLongForm;
        ClientRepetitionGuardToggle.IsOn = settings.ClientRepetitionGuard;
        RequestTimeoutBox.Value = settings.RequestTimeoutSeconds;
        ContextSizeBox.Value = settings.ContextSize;
        MaxHistoryRoundsBox.Value = settings.MaxHistoryRounds;
        MemosTopKBox.Value = settings.MemosTopK;
        GpuLayersBox.Value = settings.GpuLayers;
        ParallelSlotsBox.Value = settings.ParallelSlots;
        StartupTimeoutBox.Value = settings.StartupTimeoutSeconds;
        ReasoningToggle.IsOn = settings.ReasoningEnabled;
        JinjaToggle.IsOn = settings.UseJinja;
        ModelAliasBox.Text = settings.ModelAlias;
        ModelPathBox.Text = settings.ModelPath;
        PortBox.Value = settings.Port;
        var video = settings.VideoGeneration ?? VideoGenerationSettings.SafeDefaults;
        VideoWidthBox.Value = video.Width;
        VideoHeightBox.Value = video.Height;
        VideoDurationBox.Value = video.DurationSeconds;
        VideoStepsBox.Value = video.Steps;
        VideoSeedBox.Value = video.Seed;
        VideoRandomSeedToggle.IsOn = video.RandomSeed;
        SelectTaggedItem(VideoOutputFormatBox, video.OutputFormat);
        SelectTaggedItem(VideoCodecBox, video.VideoCodec);
        AutoStartOnDemandToggle.IsOn = settings.AutoStartOnDemand;
        HanhuaPackRootBox.Text = LocalChatSettings.CoalesceHanhuaPath(settings.HanhuaPackRoot, LocalChatSettings.DefaultHanhuaPackRoot);
        HanhuaPythonExeBox.Text = LocalChatSettings.CoalesceHanhuaPath(settings.HanhuaPythonExe, LocalChatSettings.DefaultHanhuaPythonExe);
        HanhuaMitRootBox.Text = LocalChatSettings.CoalesceHanhuaPath(settings.HanhuaMitRoot, LocalChatSettings.DefaultHanhuaMitRoot);
        SelectSessionSortMode(settings.SessionSortMode);
        var attachments = settings.ChatAttachments ?? ChatAttachmentPolicy.SafeDefaults;
        ChatAttachTextToggle.IsOn = attachments.AllowText;
        ChatAttachImageToggle.IsOn = attachments.AllowImage;
        ChatAttachAudioToggle.IsOn = attachments.AllowAudio;
        ChatAttachVideoToggle.IsOn = attachments.AllowVideo;
        ChatAttachMaxFilesBox.Value = attachments.MaxFiles;
        ChatAttachMaxKbBox.Value = Math.Max(4, attachments.MaxBytesPerFile / 1024);
        ChatAttachExtraExtensionsBox.Text = attachments.ExtraExtensions ?? "";
        WritePhraseEditors(settings.VideoPromptPhrases);
        MemosStatusReadonlyText.Text = settings.UseMemos ? "MemOS 长期记忆：开启" : "MemOS 长期记忆：关闭";
        LogStatusReadonlyText.Text = settings.SaveChatLogs ? "聊天日志：开启" : "聊天日志：关闭";
        ValidationInfo.IsOpen = false;
        HideDiscardConfirm();
        UpdateTextProfileDetails();
        UpdateVideoProfileDetails();
        _populatingProfiles = false;
        AttachNumberBoxEditDisplay();
    }

    private TextModelProfile? SelectedTextProfile => TextModelProfileBox.SelectedItem as TextModelProfile;
    private VideoModelProfile? SelectedVideoProfile => VideoModelProfileBox.SelectedItem as VideoModelProfile;

    public TextModelProfile? CurrentTextProfile => SelectedTextProfile;
    public VideoModelProfile? CurrentVideoProfile => SelectedVideoProfile;

    public void SetModelOperationStatus(bool video, string message, bool busy)
    {
        (video ? VideoModelOperationStatus : TextModelOperationStatus).Text = message;
        TextImportButton.IsEnabled = !busy;
        ReleaseTextModelButton.IsEnabled = !busy;
        VideoImportButton.IsEnabled = !busy;
        ReleaseVideoModelButton.IsEnabled = !busy;
    }

    public void RefreshTextProfiles(ModelProfileCatalog profiles, string selectedId)
    {
        _textProfiles = profiles;
        _populatingProfiles = true;
        TextModelProfileBox.ItemsSource = profiles.Profiles;
        TextModelProfileBox.SelectedItem = profiles.Resolve(selectedId);
        _populatingProfiles = false;
        UpdateTextProfileDetails();
    }

    public void RefreshVideoProfiles(VideoModelProfileCatalog profiles, string selectedId)
    {
        _videoProfiles = profiles;
        _populatingProfiles = true;
        VideoModelProfileBox.ItemsSource = profiles.Profiles;
        VideoModelProfileBox.SelectedItem = profiles.Resolve(selectedId);
        _populatingProfiles = false;
        ApplyVideoCapabilities();
        UpdateVideoProfileDetails();
    }

    private void TextImportButton_Click(object sender, RoutedEventArgs e) => TextImportRequested?.Invoke(this, EventArgs.Empty);
    private void VideoImportButton_Click(object sender, RoutedEventArgs e) => VideoImportRequested?.Invoke(this, EventArgs.Empty);
    private void ReleaseTextModelButton_Click(object sender, RoutedEventArgs e) => ReleaseTextModelRequested?.Invoke(this, EventArgs.Empty);
    private void ReleaseVideoModelButton_Click(object sender, RoutedEventArgs e) => ReleaseVideoModelRequested?.Invoke(this, EventArgs.Empty);

    private void SelectStartupModel(string? startupModel)
    {
        var normalized = StartupModelSelection.Normalize(startupModel);
        foreach (var item in StartupModelBox.Items.OfType<ComboBoxItem>())
        {
            if (string.Equals(item.Tag as string, normalized, StringComparison.Ordinal))
            {
                StartupModelBox.SelectedItem = item;
                return;
            }
        }
        StartupModelBox.SelectedIndex = 0;
    }

    private string ReadStartupModel()
        => StartupModelBox.SelectedItem is ComboBoxItem { Tag: string tag }
            ? StartupModelSelection.Normalize(tag)
            : StartupModelSelection.Text;

    private void TextModelProfileBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_populatingProfiles || SelectedTextProfile is not { } profile) return;
        var service = profile.Service;
        ContextSizeBox.Value = service.ContextSize;
        GpuLayersBox.Value = service.GpuLayers;
        ParallelSlotsBox.Value = service.ParallelSlots;
        StartupTimeoutBox.Value = service.StartupTimeoutSeconds;
        ReasoningToggle.IsOn = service.ReasoningEnabled;
        JinjaToggle.IsOn = service.UseJinja;
        ModelAliasBox.Text = service.ModelAlias;
        ModelPathBox.Text = service.ModelPath;
        PortBox.Value = service.Port;
        AutoStartOnDemandToggle.IsOn = service.AutoStartOnDemand;
        UpdateTextProfileDetails();
    }

    private void VideoModelProfileBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_populatingProfiles || SelectedVideoProfile is null) return;
        ApplyVideoCapabilities();
        UpdateVideoProfileDetails();
    }

    private void UpdateTextProfileDetails()
    {
        if (SelectedTextProfile is not { } profile) return;
        TextModelProfileDetails.Text = $"{profile.Adapter}  /  {profile.Service.BindHost}:{profile.Service.Port}  /  API model: {profile.Service.ModelAlias}";
    }

    private void UpdateVideoProfileDetails()
    {
        if (SelectedVideoProfile is not { } profile) return;
        ApplyVideoCapabilities();
        var capabilities = profile.Capabilities;
        VideoModelProfileDetails.Text = $"{profile.Adapter}  /  {profile.Service.BindHost}:{profile.Service.Port}  /  {capabilities.FramesPerSecond} fps";
        VideoCapabilityHint.Text = $"宽高 {capabilities.MinimumDimension}–{capabilities.MaximumDimension}，步进 {capabilities.DimensionStep}；时长 {capabilities.MinimumDurationSeconds}–{capabilities.MaximumDurationSeconds} 秒；步数 {capabilities.MinimumSteps}–{capabilities.MaximumSteps}。";
        VideoProfileDisplayNameBox.Text = profile.DisplayName;
        VideoProfileFpsBox.Value = capabilities.FramesPerSecond;
        VideoProfileAlignmentMultipleBox.Value = capabilities.AlignmentMultiple;
        VideoProfileAlignmentOffsetBox.Value = capabilities.AlignmentOffset;
        VideoProfileMinimumDimensionBox.Value = capabilities.MinimumDimension;
        VideoProfileMaximumDimensionBox.Value = capabilities.MaximumDimension;
        VideoProfileDimensionStepBox.Value = capabilities.DimensionStep;
        VideoProfileMaximumPixelsBox.Value = capabilities.MaximumPixels;
        VideoProfileMinimumDurationBox.Value = capabilities.MinimumDurationSeconds;
        VideoProfileMaximumDurationBox.Value = capabilities.MaximumDurationSeconds;
        VideoProfileMinimumStepsBox.Value = capabilities.MinimumSteps;
        VideoProfileMaximumStepsBox.Value = capabilities.MaximumSteps;
        var media = profile.EffectiveMedia;
        VideoProfileMaxReferenceImagesBox.Minimum = 0;
        VideoProfileMaxReferenceImagesBox.Maximum = Math.Max(0, media.EffectiveNodeMaxReferenceImages);
        VideoProfileMaxReferenceImagesBox.Value = media.MaxReferenceImages;
        VideoProfileMaxReferenceVideosBox.Minimum = 0;
        VideoProfileMaxReferenceVideosBox.Maximum = Math.Max(0, media.EffectiveNodeMaxReferenceVideos);
        VideoProfileMaxReferenceVideosBox.Value = media.MaxReferenceVideos;
        VideoProfileMaxReferenceAudiosBox.Minimum = 0;
        VideoProfileMaxReferenceAudiosBox.Maximum = Math.Max(0, media.EffectiveNodeMaxReferenceAudios);
        VideoProfileMaxReferenceAudiosBox.Value = media.MaxReferenceAudios;
        SelectTaggedItem(VideoProfileRefImageSizeBox, media.DefaultRefImageSize);
        SelectTaggedItem(VideoProfileDefaultModeBox, media.DefaultMode);
        VideoProfileTextToVideoToggle.IsOn = media.TextToVideo;
        VideoProfileFirstFrameToggle.IsOn = media.FirstFrame;
        VideoProfileFirstLastFrameToggle.IsOn = media.FirstLastFrame;
        var workflow = profile.EffectiveWorkflow;
        VideoProfileCfgBox.Value = workflow.Cfg ?? 1;
        VideoProfileDenoiseBox.Value = workflow.Denoise ?? 1;
        VideoProfileShiftVideoBox.Value = workflow.ShiftVideo ?? 12;
        VideoProfileShiftAudioBox.Value = workflow.ShiftAudio ?? 3;
        EnsureTaggedItem(VideoProfileSamplerBox, workflow.SamplerName ?? "euler");
        EnsureTaggedItem(VideoProfileSchedulerBox, workflow.Scheduler ?? "simple");
        VideoProfileNegativePromptBox.Text = workflow.NegativePrompt ?? "";
    }

    private void ApplyVideoCapabilities()
    {
        if (SelectedVideoProfile is not { } profile) return;
        var capabilities = profile.Capabilities;
        VideoWidthBox.Minimum = capabilities.MinimumDimension;
        VideoWidthBox.Maximum = capabilities.MaximumDimension;
        VideoWidthBox.SmallChange = capabilities.DimensionStep;
        VideoHeightBox.Minimum = capabilities.MinimumDimension;
        VideoHeightBox.Maximum = capabilities.MaximumDimension;
        VideoHeightBox.SmallChange = capabilities.DimensionStep;
        VideoDurationBox.Minimum = capabilities.MinimumDurationSeconds;
        VideoDurationBox.Maximum = capabilities.MaximumDurationSeconds;
        VideoStepsBox.Minimum = capabilities.MinimumSteps;
        VideoStepsBox.Maximum = capabilities.MaximumSteps;
    }

    private void SelectSessionSortMode(string? mode)
    {
        var normalized = SessionSortModes.Normalize(mode);
        for (var i = 0; i < SessionSortModeBox.Items.Count; i++)
        {
            if (SessionSortModeBox.Items[i] is ComboBoxItem item
                && string.Equals(item.Tag as string, normalized, StringComparison.OrdinalIgnoreCase))
            {
                SessionSortModeBox.SelectedIndex = i;
                return;
            }
        }

        SessionSortModeBox.SelectedIndex = 0;
    }

    private string ReadSessionSortMode()
    {
        if (SessionSortModeBox.SelectedItem is ComboBoxItem item && item.Tag is string tag)
            return SessionSortModes.Normalize(tag);
        return SessionSortModes.Created;
    }

    private static void SelectTaggedItem(ComboBox box, string value)
    {
        foreach (var item in box.Items.OfType<ComboBoxItem>())
        {
            if (string.Equals(item.Tag as string, value, StringComparison.OrdinalIgnoreCase))
            {
                box.SelectedItem = item;
                return;
            }
        }
        box.SelectedIndex = 0;
    }

    private static string ReadTaggedItem(ComboBox box, string fallback)
        => box.SelectedItem is ComboBoxItem { Tag: string tag } ? tag : fallback;

    private LocalChatSettings BuildDraft()
        => _original with
        {
            MaxOutputTokens = Whole(MaxOutputTokensBox),
            Temperature = SettingsNumberInput.Temperature(TemperatureBox.Text, TemperatureBox.Value),
            FrequencyPenalty = SettingsNumberInput.OpenAiPenalty(FrequencyPenaltyBox.Text, FrequencyPenaltyBox.Value),
            PresencePenalty = SettingsNumberInput.OpenAiPenalty(PresencePenaltyBox.Text, PresencePenaltyBox.Value),
            RepeatPenalty = SettingsNumberInput.RepeatPenalty(RepeatPenaltyBox.Text, RepeatPenaltyBox.Value),
            DryMultiplier = SettingsNumberInput.DryMultiplier(DryMultiplierBox.Text, DryMultiplierBox.Value),
            RepeatLastN = Whole(RepeatLastNBox),
            StreamResponses = StreamResponsesToggle.IsOn,
            EnforceTextVideoModelExclusivity = ModelExclusivityToggle.IsOn,
            StartupModel = ReadStartupModel(),
            AutoTightenOutputTokens = AutoTightenOutputToggle.IsOn,
            SegmentedLongForm = SegmentedLongFormToggle.IsOn,
            ClientRepetitionGuard = ClientRepetitionGuardToggle.IsOn,
            RequestTimeoutSeconds = Whole(RequestTimeoutBox),
            ContextSize = Whole(ContextSizeBox),
            MaxHistoryRounds = Whole(MaxHistoryRoundsBox),
            MemosTopK = Whole(MemosTopKBox),
            GpuLayers = Whole(GpuLayersBox),
            ParallelSlots = Whole(ParallelSlotsBox),
            StartupTimeoutSeconds = Whole(StartupTimeoutBox),
            ReasoningEnabled = ReasoningToggle.IsOn,
            UseJinja = JinjaToggle.IsOn,
            ModelAlias = ModelAliasBox.Text.Trim(),
            ModelPath = ModelPathBox.Text.Trim(),
            Port = Whole(PortBox),
            AutoStartOnDemand = AutoStartOnDemandToggle.IsOn,
            HanhuaPackRoot = LocalChatSettings.CoalesceHanhuaPath(HanhuaPackRootBox.Text, LocalChatSettings.DefaultHanhuaPackRoot),
            HanhuaPythonExe = LocalChatSettings.CoalesceHanhuaPath(HanhuaPythonExeBox.Text, LocalChatSettings.DefaultHanhuaPythonExe),
            HanhuaMitRoot = LocalChatSettings.CoalesceHanhuaPath(HanhuaMitRootBox.Text, LocalChatSettings.DefaultHanhuaMitRoot),
            HanhuaEngine = HanhuaEngineCodec.ToJson(HanhuaEngineCodec.Parse(_original.HanhuaEngine)),
            SelectedTextProfileId = SelectedTextProfile?.Id,
            SelectedVideoProfileId = SelectedVideoProfile?.Id,
            SessionSortMode = ReadSessionSortMode(),
            ChatAttachments = new ChatAttachmentPolicy
            {
                AllowText = ChatAttachTextToggle.IsOn,
                AllowImage = ChatAttachImageToggle.IsOn,
                AllowAudio = ChatAttachAudioToggle.IsOn,
                AllowVideo = ChatAttachVideoToggle.IsOn,
                MaxFiles = Whole(ChatAttachMaxFilesBox),
                MaxBytesPerFile = Whole(ChatAttachMaxKbBox) * 1024,
                ExtraExtensions = ChatAttachExtraExtensionsBox.Text ?? "",
            },
            VideoGeneration = new VideoGenerationSettings
            {
                Width = Whole(VideoWidthBox),
                Height = Whole(VideoHeightBox),
                DurationSeconds = Whole(VideoDurationBox),
                Steps = Whole(VideoStepsBox),
                Seed = Whole(VideoSeedBox),
                RandomSeed = VideoRandomSeedToggle.IsOn,
                OutputFormat = ReadTaggedItem(VideoOutputFormatBox, "mp4"),
                VideoCodec = ReadTaggedItem(VideoCodecBox, "auto"),
            },
            VideoPromptPhrases = ReadPhraseEditors(),
        };

    private VideoModelProfile BuildEditedVideoProfile()
    {
        var selected = SelectedVideoProfile ?? throw new InvalidOperationException("请先选择视频模型档案。");
        var media = selected.EffectiveMedia;
        return selected with
        {
            DisplayName = VideoProfileDisplayNameBox.Text.Trim(),
            Capabilities = new VideoGenerationCapabilities(
                Whole(VideoProfileFpsBox),
                Whole(VideoProfileAlignmentMultipleBox),
                Whole(VideoProfileAlignmentOffsetBox),
                Whole(VideoProfileMinimumDimensionBox),
                Whole(VideoProfileMaximumDimensionBox),
                Whole(VideoProfileDimensionStepBox),
                Whole(VideoProfileMaximumPixelsBox),
                Whole(VideoProfileMinimumDurationBox),
                Whole(VideoProfileMaximumDurationBox),
                Whole(VideoProfileMinimumStepsBox),
                Whole(VideoProfileMaximumStepsBox)),
            Media = media with
            {
                TextToVideo = VideoProfileTextToVideoToggle.IsOn,
                FirstFrame = VideoProfileFirstFrameToggle.IsOn,
                FirstLastFrame = VideoProfileFirstLastFrameToggle.IsOn,
                MaxReferenceImages = Whole(VideoProfileMaxReferenceImagesBox),
                MaxReferenceVideos = Whole(VideoProfileMaxReferenceVideosBox),
                MaxReferenceAudios = Whole(VideoProfileMaxReferenceAudiosBox),
                DefaultRefImageSize = ReadTaggedItem(VideoProfileRefImageSizeBox, "match"),
                DefaultMode = ReadTaggedItem(VideoProfileDefaultModeBox, "text"),
            },
            Workflow = new VideoWorkflowParameters
            {
                Cfg = Decimal(VideoProfileCfgBox),
                SamplerName = ReadTaggedItem(VideoProfileSamplerBox, "euler"),
                Scheduler = ReadTaggedItem(VideoProfileSchedulerBox, "simple"),
                Denoise = Decimal(VideoProfileDenoiseBox),
                ShiftVideo = Decimal(VideoProfileShiftVideoBox),
                ShiftAudio = Decimal(VideoProfileShiftAudioBox),
                NegativePrompt = VideoProfileNegativePromptBox.Text ?? string.Empty,
            },
        };
    }

    internal bool IsDirty()
        => !BuildDraft().IsSameAs(_original) || BuildEditedVideoProfile() != SelectedVideoProfile;

    private void EnsurePhraseEditors()
    {
        if (_phraseBoxes.Count > 0 || VideoPromptPhraseHost is null) return;
        foreach (var editor in VideoPromptTemplatePhrases.Editors)
        {
            var prefix = editor.Key.StartsWith("extra", StringComparison.Ordinal)
                && editor.Key.EndsWith("Prefix", StringComparison.Ordinal);
            var box = new TextBox
            {
                Header = editor.Label,
                AcceptsReturn = !prefix,
                TextWrapping = TextWrapping.Wrap,
                MinHeight = prefix ? 36 : 52,
                HorizontalAlignment = HorizontalAlignment.Stretch,
            };
            AutomationProperties.SetAutomationId(box, editor.AutomationId);
            AutomationProperties.SetName(box, editor.Label);
            _phraseBoxes[editor.Key] = box;
            var host = prefix && VideoPromptPrefixHost is not null
                ? VideoPromptPrefixHost
                : VideoPromptPhraseHost;
            host.Children.Add(box);
        }
    }

    private void WritePhraseEditors(VideoPromptTemplatePhrases? phrases)
    {
        EnsurePhraseEditors();
        var filled = (phrases ?? VideoPromptTemplatePhrases.OfficialDefaults).WithDefaults();
        foreach (var editor in VideoPromptTemplatePhrases.Editors)
        {
            if (_phraseBoxes.TryGetValue(editor.Key, out var box))
                box.Text = editor.Read(filled);
        }
    }

    private VideoPromptTemplatePhrases ReadPhraseEditors()
    {
        EnsurePhraseEditors();
        var current = VideoPromptTemplatePhrases.OfficialDefaults;
        foreach (var editor in VideoPromptTemplatePhrases.Editors)
        {
            if (_phraseBoxes.TryGetValue(editor.Key, out var box))
                current = editor.Write(current, box.Text ?? "");
        }
        return current.WithDefaults();
    }

    private void ResetVideoPromptPhrasesButton_Click(object sender, RoutedEventArgs e)
        => WritePhraseEditors(VideoPromptTemplatePhrases.OfficialDefaults);

    private void ResetButton_Click(object sender, RoutedEventArgs e)
    {
        HideDiscardConfirm();
        if (SettingsSectionSelector.SelectedItem == VideoSettingsSectionItem)
        {
            Populate(BuildDraft() with
            {
                VideoGeneration = VideoGenerationSettings.SafeDefaults,
                VideoPromptPhrases = VideoPromptTemplatePhrases.OfficialDefaults,
            });
            return;
        }
        Populate(LocalChatSettings.ResetToDefaults() with
        {
            UseMemos = _original.UseMemos,
            SaveChatLogs = _original.SaveChatLogs,
            ContextSize = _original.ContextSize,
            GpuLayers = _original.GpuLayers,
            ParallelSlots = _original.ParallelSlots,
            ReasoningEnabled = _original.ReasoningEnabled,
            UseJinja = _original.UseJinja,
            ModelAlias = _original.ModelAlias,
            ModelPath = _original.ModelPath,
            Port = _original.Port,
            StartupTimeoutSeconds = _original.StartupTimeoutSeconds,
            AutoStartOnDemand = _original.AutoStartOnDemand,
            SelectedTextProfileId = _original.SelectedTextProfileId,
            StartupModel = _original.StartupModel,
            SelectedVideoProfileId = BuildDraft().SelectedVideoProfileId,
            VideoGeneration = BuildDraft().VideoGeneration,
            VideoPromptPhrases = BuildDraft().VideoPromptPhrases,
        });
    }

    private void CloseButton_Click(object sender, RoutedEventArgs e)
    {
        if (!IsDirty())
        {
            CloseRequested?.Invoke(this, EventArgs.Empty);
            return;
        }

        // Avoid ContentDialog (known Microsoft.UI.Xaml native crash path). Use inline confirm.
        ShowDiscardConfirm();
        ValidationInfo.IsOpen = false;
    }

    private void KeepEditingButton_Click(object sender, RoutedEventArgs e)
        => HideDiscardConfirm();

    private void DiscardAndCloseButton_Click(object sender, RoutedEventArgs e)
    {
        HideDiscardConfirm();
        CloseRequested?.Invoke(this, EventArgs.Empty);
    }

    private void ShowDiscardConfirm()
    {
        FooterActionsBar.Opacity = 0;
        FooterActionsBar.IsHitTestVisible = false;
        DiscardConfirmBar.Height = double.NaN;
        DiscardConfirmBar.Opacity = 1;
        DiscardConfirmBar.IsHitTestVisible = true;
        KeepEditingButton.IsEnabled = true;
        DiscardAndCloseButton.IsEnabled = true;
    }

    private void HideDiscardConfirm()
    {
        KeepEditingButton.IsEnabled = false;
        DiscardAndCloseButton.IsEnabled = false;
        DiscardConfirmBar.IsHitTestVisible = false;
        DiscardConfirmBar.Opacity = 0;
        DiscardConfirmBar.Height = 0;
        FooterActionsBar.Opacity = 1;
        FooterActionsBar.IsHitTestVisible = true;
    }

    private void SaveButton_Click(object sender, RoutedEventArgs e)
    {
        HideDiscardConfirm();
        var candidate = BuildDraft();
        var editedVideoProfile = BuildEditedVideoProfile();
        var errors = candidate.Validate().ToList();
        if (string.IsNullOrWhiteSpace(editedVideoProfile.DisplayName)) errors.Add("视频档案显示名称不能为空");
        errors.AddRange(editedVideoProfile.Capabilities.ValidateDefinition());
        errors.AddRange(editedVideoProfile.EffectiveMedia.ValidateDefinition());
        errors.AddRange(editedVideoProfile.EffectiveWorkflow.Validate());
        errors.AddRange(editedVideoProfile.Capabilities.Validate(candidate.VideoGeneration));
        if (errors.Count > 0)
        {
            ValidationInfo.Message = string.Join("；", errors);
            ValidationInfo.IsOpen = true;
            return;
        }
        SaveRequested?.Invoke(this, new SettingsSaveRequestedEventArgs(candidate, editedVideoProfile));
    }

    private static int Whole(NumberBox box) => SettingsNumberInput.Whole(box.Text, box.Value);

    private static double Decimal(NumberBox box) => SettingsNumberInput.Decimal(box.Text, box.Value);

    private static void EnsureTaggedItem(ComboBox box, string value)
    {
        if (box.Items.OfType<ComboBoxItem>().All(item =>
                !string.Equals(item.Tag as string, value, StringComparison.OrdinalIgnoreCase)))
        {
            box.Items.Add(new ComboBoxItem { Content = value, Tag = value });
        }
        SelectTaggedItem(box, value);
    }
}
