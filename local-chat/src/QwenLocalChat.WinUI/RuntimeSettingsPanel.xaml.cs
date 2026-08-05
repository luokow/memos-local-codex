using System.Numerics;
using Microsoft.UI.Composition;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Hosting;
using QwenLocalChat.Core;
using Windows.Globalization.NumberFormatting;

namespace QwenLocalChat_WinUI;

public sealed class SettingsSaveRequestedEventArgs(LocalChatSettings settings) : EventArgs
{
    public LocalChatSettings Settings { get; } = settings;
}

public sealed partial class RuntimeSettingsPanel : UserControl
{
    private const double TemperatureStep = 0.1;
    private const double PenaltyStep = 0.05;
    private const double RepeatPenaltyStep = 0.01;
    private const float ClosedSlideX = 520f;
    private LocalChatSettings _original = LocalChatSettings.SafeDefaults;
    private bool _isClosing;
    private bool _compositionReady;
    private CompositionScopedBatch? _closeBatch;

    public RuntimeSettingsPanel()
    {
        InitializeComponent();
        ConfigureTemperatureBox();
        ConfigurePenaltyBoxes();
        Loaded += (_, _) => EnsureCompositionReady();
    }

    public event EventHandler<SettingsSaveRequestedEventArgs>? SaveRequested;
    public event EventHandler? CloseRequested;
    /// <summary>Raised after the panel is fully closed (animation finished or instant hide).</summary>
    public event EventHandler? Closed;

    public void Open(LocalChatSettings settings)
    {
        _isClosing = false;
        DetachCloseBatch();
        _original = settings;
        HideDiscardConfirm();
        Populate(settings);
        IsEnabled = true;
        IsHitTestVisible = true;
        Opacity = 1;
        PanelShell.Opacity = 1;
        EnsureCompositionReady();
        PlayOpenAnimation();
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

    private void ConfigurePenaltyBoxes()
    {
        ConfigureDecimalBox(FrequencyPenaltyBox, PenaltyStep, fractionDigits: 2);
        ConfigureDecimalBox(PresencePenaltyBox, PenaltyStep, fractionDigits: 2);
        ConfigureDecimalBox(RepeatPenaltyBox, RepeatPenaltyStep, fractionDigits: 2);
        ConfigureDecimalBox(DryMultiplierBox, PenaltyStep, fractionDigits: 2);
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
        MaxOutputTokensBox.Value = settings.MaxOutputTokens;
        TemperatureBox.Value = SettingsNumberInput.Temperature(settings.Temperature);
        FrequencyPenaltyBox.Value = SettingsNumberInput.OpenAiPenalty(settings.FrequencyPenalty);
        PresencePenaltyBox.Value = SettingsNumberInput.OpenAiPenalty(settings.PresencePenalty);
        RepeatPenaltyBox.Value = SettingsNumberInput.RepeatPenalty(settings.RepeatPenalty);
        DryMultiplierBox.Value = SettingsNumberInput.DryMultiplier(settings.DryMultiplier);
        RepeatLastNBox.Value = settings.RepeatLastN;
        StreamResponsesToggle.IsOn = settings.StreamResponses;
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
        SelectSessionSortMode(settings.SessionSortMode);
        MemosStatusReadonlyText.Text = settings.UseMemos ? "MemOS 长期记忆：开启" : "MemOS 长期记忆：关闭";
        LogStatusReadonlyText.Text = settings.SaveChatLogs ? "聊天日志：开启" : "聊天日志：关闭";
        ValidationInfo.IsOpen = false;
        HideDiscardConfirm();
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
            SessionSortMode = ReadSessionSortMode(),
        };

    internal bool IsDirty()
        => !BuildDraft().IsSameAs(_original);

    private void ResetButton_Click(object sender, RoutedEventArgs e)
    {
        HideDiscardConfirm();
        Populate(LocalChatSettings.ResetToDefaults() with
        {
            UseMemos = _original.UseMemos,
            SaveChatLogs = _original.SaveChatLogs,
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
        var errors = candidate.Validate();
        if (errors.Count > 0)
        {
            ValidationInfo.Message = string.Join("；", errors);
            ValidationInfo.IsOpen = true;
            return;
        }
        SaveRequested?.Invoke(this, new SettingsSaveRequestedEventArgs(candidate));
    }

    private static int Whole(NumberBox box) => SettingsNumberInput.Whole(box.Text, box.Value);
}
