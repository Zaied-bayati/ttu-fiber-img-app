namespace Ttu.FiberImgApp.Core.Abstractions;

/// <summary>
/// In-app activity / serial-style monitor for operator diagnostics.
/// </summary>
public interface IActivityLog
{
    event EventHandler? Changed;

    IReadOnlyList<string> Lines { get; }

    string Text { get; }

    void Write(string message);

    void Write(string category, string message);

    void Clear();
}