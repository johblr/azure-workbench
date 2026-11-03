using System.Text;

namespace AzureWorkbench.Models;

public sealed class LogBuffer(int capacity = 200_000)
{
    readonly StringBuilder _text = new();
    public bool Truncated { get; private set; }

    public void Append(string text)
    {
        if (capacity < 1) throw new ArgumentOutOfRangeException(nameof(capacity));
        if (text.Length >= capacity)
        {
            _text.Clear();
            _text.Append(text.AsSpan(text.Length - capacity));
            Truncated = true;
        }
        else
        {
            var excess = _text.Length + text.Length - capacity;
            if (excess > 0)
            {
                _text.Remove(0, excess);
                Truncated = true;
            }
            _text.Append(text);
        }
    }

    public void Clear()
    {
        _text.Clear();
        Truncated = false;
    }

    public override string ToString() => _text.ToString();
}
