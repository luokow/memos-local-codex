using System.ComponentModel;
using System.Runtime.CompilerServices;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;
using QwenLocalChat.Core;

namespace QwenLocalChat_WinUI;

public sealed class TranscriptEntry : INotifyPropertyChanged
{
    private string _text;
    private string _editText;
    private bool _isStreaming;
    private bool _isEditing;

    public TranscriptEntry(string label, string text, Brush labelBrush, double labelFontSize, bool canCopy, bool isStreaming = false)
    {
        Label = label;
        _text = text;
        _editText = text;
        _isStreaming = isStreaming;
        LabelBrush = labelBrush;
        LabelFontSize = labelFontSize;
        CopyVisibility = canCopy ? Visibility.Visible : Visibility.Collapsed;
        CanEdit = TranscriptPresentationPolicy.CanEdit(label);
    }

    public string Label { get; set; }
    public string Text
    {
        get => _text;
        private set
        {
            if (_text == value) return;
            _text = value;
            OnPropertyChanged();
        }
    }
    public string EditText
    {
        get => _editText;
        set
        {
            var next = value ?? "";
            if (_editText == next) return;
            _editText = next;
            OnPropertyChanged();
        }
    }
    public Brush LabelBrush { get; set; }
    public double LabelFontSize { get; set; }
    public Visibility CopyVisibility { get; }
    public bool CanEdit { get; }
    public bool IsEditing => _isEditing;
    public Visibility EditVisibility
        => CanEdit && !_isStreaming && !_isEditing ? Visibility.Visible : Visibility.Collapsed;
    public Visibility EditingVisibility
        => _isEditing ? Visibility.Visible : Visibility.Collapsed;
    public bool IsStreaming => _isStreaming;
    /// <summary>Legacy plain-text stream surface — kept collapsed; live Markdown is used instead.</summary>
    public Visibility StreamingVisibility => Visibility.Collapsed;
    /// <summary>Markdown body is shown unless this user turn is being edited.</summary>
    public Visibility FormattedVisibility => _isEditing ? Visibility.Collapsed : Visibility.Visible;

    public event PropertyChangedEventHandler? PropertyChanged;

    public void UpdateStreamingText(string text) => Text = text;

    /// <summary>Reuse a finished assistant bubble for continuation streaming.</summary>
    public void ResumeStreaming(string text)
    {
        Text = text;
        if (_isStreaming) return;
        _isStreaming = true;
        OnPropertyChanged(nameof(IsStreaming));
        OnPropertyChanged(nameof(StreamingVisibility));
        OnPropertyChanged(nameof(FormattedVisibility));
    }

    public void Complete(string text)
    {
        Text = text;
        if (!_isStreaming) return;
        _isStreaming = false;
        OnPropertyChanged(nameof(IsStreaming));
        // Visibility props stay constant (always Markdown); still notify for any binding that watches them.
        OnPropertyChanged(nameof(StreamingVisibility));
        OnPropertyChanged(nameof(FormattedVisibility));
        OnPropertyChanged(nameof(EditVisibility));
    }

    public void BeginEdit()
    {
        if (!CanEdit || _isStreaming) return;
        EditText = Text;
        if (_isEditing) return;
        _isEditing = true;
        NotifyEditState();
    }

    public void CancelEdit()
    {
        EditText = Text;
        if (!_isEditing) return;
        _isEditing = false;
        NotifyEditState();
    }

    private void NotifyEditState()
    {
        OnPropertyChanged(nameof(IsEditing));
        OnPropertyChanged(nameof(EditVisibility));
        OnPropertyChanged(nameof(EditingVisibility));
        OnPropertyChanged(nameof(FormattedVisibility));
    }

    private void OnPropertyChanged([CallerMemberName] string? name = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
