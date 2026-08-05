using System.ComponentModel;
using System.Runtime.CompilerServices;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;

namespace QwenLocalChat_WinUI;

public sealed class TranscriptEntry : INotifyPropertyChanged
{
    private string _text;
    private bool _isStreaming;

    public TranscriptEntry(string label, string text, Brush labelBrush, double labelFontSize, bool canCopy, bool isStreaming = false)
    {
        Label = label;
        _text = text;
        _isStreaming = isStreaming;
        LabelBrush = labelBrush;
        LabelFontSize = labelFontSize;
        CopyVisibility = canCopy ? Visibility.Visible : Visibility.Collapsed;
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
    public Brush LabelBrush { get; set; }
    public double LabelFontSize { get; set; }
    public Visibility CopyVisibility { get; }
    public bool IsStreaming => _isStreaming;
    /// <summary>Legacy plain-text stream surface — kept collapsed; live Markdown is used instead.</summary>
    public Visibility StreamingVisibility => Visibility.Collapsed;
    /// <summary>Markdown body is always shown, including while tokens stream in.</summary>
    public Visibility FormattedVisibility => Visibility.Visible;

    public event PropertyChangedEventHandler? PropertyChanged;

    public void UpdateStreamingText(string text) => Text = text;

    /// <summary>Reuse a finished Qwen bubble for assistant-prefill continuation streaming.</summary>
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
    }

    private void OnPropertyChanged([CallerMemberName] string? name = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
