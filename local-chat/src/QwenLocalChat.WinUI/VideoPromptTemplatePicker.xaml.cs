using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using QwenLocalChat.Core;

namespace QwenLocalChat_WinUI;

public sealed partial class VideoPromptTemplatePicker : UserControl
{
    private VideoConditioningMode _mode;
    private IReadOnlyList<VideoPromptMediaChoice> _media = [];
    private VideoPromptTemplatePhrases _phrases = VideoPromptTemplatePhrases.OfficialDefaults;
    private readonly Dictionary<string, ComboBox> _slotBoxes = new(StringComparer.Ordinal);
    private readonly Dictionary<int, ComboBox> _dutyBoxes = new();
    private ComboBox? _sizeBox;
    private VideoPromptTemplateSpec? _selected;

    public event EventHandler<VideoPromptTemplateInsert>? InsertRequested;
    public event EventHandler? Cancelled;

    public VideoPromptTemplatePicker()
    {
        InitializeComponent();
    }

    public void Refresh(
        VideoConditioningMode mode,
        IReadOnlyList<VideoPromptMediaChoice> media,
        VideoPromptTemplatePhrases? phrases = null)
    {
        _mode = mode;
        _media = media ?? [];
        _phrases = (phrases ?? VideoPromptTemplatePhrases.OfficialDefaults).WithDefaults();
        TemplateErrorText.Visibility = Visibility.Collapsed;
        var specs = VideoPromptTemplates.AvailableFor(
            mode,
            CountOf("Picture"),
            CountOf("Video"),
            CountOf("Audio"));
        TemplateKindBox.ItemsSource = specs;
        TemplateKindBox.DisplayMemberPath = nameof(VideoPromptTemplateSpec.Title);
        if (specs.Count > 0)
            TemplateKindBox.SelectedIndex = 0;
        else
        {
            TemplateKindBox.SelectedIndex = -1;
            RebuildSlots(null);
        }
    }

    private void TemplateKindBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
        => RebuildSlots(TemplateKindBox.SelectedItem as VideoPromptTemplateSpec);

    private void RebuildSlots(VideoPromptTemplateSpec? spec)
    {
        TemplateSlotsHost.Children.Clear();
        _slotBoxes.Clear();
        _dutyBoxes.Clear();
        _sizeBox = null;
        _selected = spec;
        TemplateDescriptionText.Text = spec?.Description ?? "当前模式没有可插入的模板。";
        if (spec is null) return;

        if (spec.Kind == VideoPromptTemplateKind.CustomPictureDuties)
        {
            var pictures = Pictures();
            foreach (var picture in pictures)
            {
                var box = new ComboBox
                {
                    Header = picture.Display,
                    HorizontalAlignment = HorizontalAlignment.Stretch,
                    ItemsSource = VideoPromptTemplates.PictureDuties,
                    DisplayMemberPath = nameof(VideoPromptDutyChoice.Label),
                    SelectedIndex = DutyIndex(VideoPromptTemplates.DefaultDuty(picture.Index, pictures.Length)),
                    Tag = picture.Index,
                };
                _dutyBoxes[picture.Index] = box;
                TemplateSlotsHost.Children.Add(box);
            }
            return;
        }

        if (VideoPromptTemplates.HasAdjustableSize(spec))
        {
            var sizes = Enumerable.Range(spec.MinSize, spec.MaxSize - spec.MinSize + 1).ToArray();
            var uploaded = spec.Kind switch
            {
                VideoPromptTemplateKind.Characters => Pictures().Length,
                VideoPromptTemplateKind.Videos => CountOf("Video"),
                VideoPromptTemplateKind.Audios => CountOf("Audio"),
                _ => spec.MinSize,
            };
            var selectedSize = Math.Clamp(Math.Max(uploaded, spec.MinSize), spec.MinSize, spec.MaxSize);
            _sizeBox = new ComboBox
            {
                Header = spec.Kind == VideoPromptTemplateKind.Characters ? "人物数量" : "数量",
                HorizontalAlignment = HorizontalAlignment.Stretch,
                ItemsSource = sizes,
                SelectedItem = selectedSize,
            };
            _sizeBox.SelectionChanged += (_, _) => RebuildRoleBoxes(ActiveSpec());
            TemplateSlotsHost.Children.Add(_sizeBox);
        }

        RebuildRoleBoxes(ActiveSpec());
    }

    private VideoPromptTemplateSpec? ActiveSpec()
    {
        if (_selected is null) return null;
        if (_sizeBox?.SelectedItem is int size)
            return VideoPromptTemplates.WithSize(_selected, size);
        return _selected;
    }

    private void RebuildRoleBoxes(VideoPromptTemplateSpec? spec)
    {
        foreach (var child in TemplateSlotsHost.Children.OfType<ComboBox>().Where(box => !ReferenceEquals(box, _sizeBox)).ToArray())
            TemplateSlotsHost.Children.Remove(child);
        _slotBoxes.Clear();
        if (spec is null) return;

        foreach (var role in spec.Roles)
        {
            var choices = _media.Where(item => item.Kind == role.MediaKind).ToArray();
            var box = new ComboBox
            {
                Header = role.Label,
                HorizontalAlignment = HorizontalAlignment.Stretch,
                ItemsSource = choices,
                DisplayMemberPath = nameof(VideoPromptMediaChoice.Display),
                SelectedIndex = DefaultIndex(role, spec, choices),
            };
            _slotBoxes[role.Key] = box;
            TemplateSlotsHost.Children.Add(box);
        }
    }

    private VideoPromptMediaChoice[] Pictures()
        => _media.Where(item => item.Kind == "Picture").ToArray();

    private static int DutyIndex(VideoPromptPictureDuty duty)
    {
        for (var i = 0; i < VideoPromptTemplates.PictureDuties.Count; i++)
        {
            if (VideoPromptTemplates.PictureDuties[i].Duty == duty) return i;
        }
        return 0;
    }

    private static int DefaultIndex(
        VideoPromptTemplateRole role,
        VideoPromptTemplateSpec spec,
        IReadOnlyList<VideoPromptMediaChoice> choices)
    {
        if (choices.Count == 0) return -1;
        var roleIndex = 0;
        for (var i = 0; i < spec.Roles.Count; i++)
        {
            if (spec.Roles[i].Key == role.Key) { roleIndex = i; break; }
        }
        return roleIndex < choices.Count ? roleIndex : 0;
    }

    private void InsertButton_Click(object sender, RoutedEventArgs e)
    {
        var spec = ActiveSpec();
        if (spec is null)
        {
            ShowError("请先选择模板。");
            return;
        }

        if (spec.Kind == VideoPromptTemplateKind.CustomPictureDuties)
        {
            var duties = new List<(int Index, VideoPromptPictureDuty Duty)>();
            foreach (var (index, box) in _dutyBoxes)
            {
                if (box.SelectedItem is not VideoPromptDutyChoice duty)
                {
                    ShowError("请给每张图选择职责。");
                    return;
                }
                duties.Add((index, duty.Duty));
            }
            if (duties.All(item => item.Duty == VideoPromptPictureDuty.Unused))
            {
                ShowError("至少给一张图选择职责。");
                return;
            }
            TemplateErrorText.Visibility = Visibility.Collapsed;
            var videos = _media.Where(item => item.Kind == "Video").Select(item => item.Index).Distinct().ToArray();
            var audios = _media.Where(item => item.Kind == "Audio").Select(item => item.Index).Distinct().ToArray();
            InsertRequested?.Invoke(this, new VideoPromptTemplateInsert(
                spec.Title,
                VideoPromptTemplates.RenderCustom(duties, _phrases, videos, audios)));
            return;
        }

        var assignments = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var role in spec.Roles)
        {
            if (!_slotBoxes.TryGetValue(role.Key, out var box)
                || box.SelectedItem is not VideoPromptMediaChoice choice)
            {
                ShowError($"请选择「{role.Label}」对应的素材。需要 {spec.Roles.Count} 份。");
                return;
            }
            assignments[role.Key] = choice.Index;
        }

        if (!VideoPromptTemplates.TryValidate(spec, assignments, out var error))
        {
            ShowError(error ?? "模板角色分配无效。");
            return;
        }

        TemplateErrorText.Visibility = Visibility.Collapsed;
        InsertRequested?.Invoke(this, new VideoPromptTemplateInsert(
            spec.Title,
            VideoPromptTemplates.Render(spec.Kind, assignments, _phrases)));
    }

    private void CancelButton_Click(object sender, RoutedEventArgs e)
        => Cancelled?.Invoke(this, EventArgs.Empty);

    private void ShowError(string text)
    {
        TemplateErrorText.Text = text;
        TemplateErrorText.Visibility = Visibility.Visible;
    }

    private int CountOf(string kind)
        => _media.Count(item => item.Kind == kind);
}
