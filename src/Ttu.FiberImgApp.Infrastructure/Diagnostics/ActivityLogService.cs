using Ttu.FiberImgApp.Core.Abstractions;

namespace Ttu.FiberImgApp.Infrastructure.Diagnostics;

public sealed class ActivityLogService : IActivityLog
{
    private const int MaxLines = 2000;
    private readonly object _gate = new();
    private readonly List<string> _lines = new();

    public event EventHandler? Changed;

    public IReadOnlyList<string> Lines
    {
        get { lock (_gate) return _lines.ToList(); }
    }

    public string Text
    {
        get
        {
            lock (_gate)
            {
                return string.Join(Environment.NewLine, _lines);
            }
        }
    }

    public void Write(string message) => Write("app", message);

    public void Write(string category, string message)
    {
        var line = $"{DateTime.Now:HH:mm:ss.fff}  [{category}]  {message}";
        lock (_gate)
        {
            _lines.Add(line);
            if (_lines.Count > MaxLines)
            {
                _lines.RemoveRange(0, _lines.Count - MaxLines);
            }
        }

        Changed?.Invoke(this, EventArgs.Empty);
    }

    public void Clear()
    {
        lock (_gate) _lines.Clear();
        Changed?.Invoke(this, EventArgs.Empty);
    }
}