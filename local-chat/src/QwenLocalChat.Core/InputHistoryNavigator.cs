namespace QwenLocalChat.Core;

public sealed class InputHistoryNavigator
{
    private readonly List<string> _entries = [];
    private int _index;
    private string _draft = string.Empty;

    public InputHistoryNavigator()
    {
        _index = _entries.Count;
    }

    public string Previous(string currentInput)
    {
        if (_entries.Count == 0) return currentInput;
        if (_index == _entries.Count) _draft = currentInput;
        if (_index > 0) _index--;
        return _entries[_index];
    }

    public string Next(string currentInput)
    {
        if (_index >= _entries.Count) return currentInput;
        _index++;
        return _index == _entries.Count ? _draft : _entries[_index];
    }

    public void Record(string input)
    {
        if (string.IsNullOrWhiteSpace(input)) return;
        _entries.Add(input);
        ResetNavigation();
    }

    public void Clear()
    {
        _entries.Clear();
        ResetNavigation();
    }

    private void ResetNavigation()
    {
        _index = _entries.Count;
        _draft = string.Empty;
    }
}
