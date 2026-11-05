using System.Diagnostics;

namespace Gelato.Services;

// Time spent per named step of one request, for a single log line.
public sealed class StepTimer
{
    private readonly Stopwatch _total = Stopwatch.StartNew();
    private readonly List<(string Name, long Ms)> _steps = [];
    private long _mark;

    public long TotalMs => _total.ElapsedMilliseconds;

    // Ends the step running since the last mark.
    public void Mark(string name)
    {
        var now = _total.ElapsedMilliseconds;
        Add(name, now - _mark);
        _mark = now;
    }

    public void Add(string name, long ms)
    {
        lock (_steps)
        {
            var i = _steps.FindIndex(s => s.Name == name);
            if (i >= 0)
                _steps[i] = (name, _steps[i].Ms + ms);
            else
                _steps.Add((name, ms));
        }
    }

    public override string ToString()
    {
        lock (_steps)
            return string.Join(", ", _steps.Select(s => $"{s.Name} {s.Ms} ms"));
    }
}
