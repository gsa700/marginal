// Marginal — route Avalonia's own diagnostics to stdout (AlbumWall's ConsoleLogSink, copied 2026-11-01).
//
// Avalonia reports binding failures, template errors and layout complaints
// through its logger, and the default LogToTrace() sends them to
// System.Diagnostics.Trace — which, with no trace listener attached, goes
// nowhere. A broken binding then produces a control that silently renders
// nothing and says why to an audience of no one.
//
// This is how a first attempt at the expansion panel appeared to "do nothing":
// there was no error to find because the error was never printed.

using Avalonia.Logging;

namespace Marginal.App;

public sealed class ConsoleLogSink(LogEventLevel minimum) : ILogSink
{
    public bool IsEnabled(LogEventLevel level, string area) => level >= minimum;

    public void Log(LogEventLevel level, string area, object? source, string messageTemplate) =>
        Write(level, area, source, messageTemplate, []);

    public void Log(LogEventLevel level, string area, object? source,
                    string messageTemplate, params object?[] values) =>
        Write(level, area, source, messageTemplate, values);

    private void Write(LogEventLevel level, string area, object? source,
                       string template, object?[] values)
    {
        if (!IsEnabled(level, area)) return;

        // Avalonia's templates use {} placeholders positionally.
        var msg = template;
        foreach (var v in values)
        {
            var open = msg.IndexOf('{');
            if (open < 0) break;
            var close = msg.IndexOf('}', open);
            if (close < 0) break;
            msg = msg[..open] + (v?.ToString() ?? "null") + msg[(close + 1)..];
        }

        var who = source?.GetType().Name;
        Console.WriteLine($"[avalonia {level} {area}] {msg}" + (who is null ? "" : $"  <{who}>"));
    }
}
