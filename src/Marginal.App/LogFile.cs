// Marginal — a copy of the console in a file.
//
// COPIED FROM AlbumWall (src/AlbumWall.App/LogFile.cs), 2026-11-01, unchanged
// below this comment except for the settings path's name: a fix in one goes to
// the others. Why it exists: a GUI program started from the Finder, the Start
// Menu or a desktop launcher has no terminal, so whatever it printed went
// nowhere, and the one run that misbehaved was always the one nobody was
// recording. Now it goes to marginal.log beside the settings, and the previous run
// is kept as marginal.log.1.
using System.Text;

namespace Marginal.App;

public static class LogFile
{
    public static string Path { get; } = System.IO.Path.Combine(
        System.IO.Path.GetDirectoryName(Settings.FilePath)!, "marginal.log");

    /// Best effort. A log that cannot be opened must never stop the app
    /// starting; it just goes back to being console-only.
    public static void Start()
    {
        try
        {
            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(Path)!);
            if (File.Exists(Path)) File.Move(Path, Path + ".1", overwrite: true);

            var file = new StreamWriter(new FileStream(Path, FileMode.Create, FileAccess.Write,
                                                       FileShare.ReadWrite | FileShare.Delete))
            { AutoFlush = true };

            // Errors too (2026-11-01): Console.Error went only to the terminal,
            // so the line that said what failed was the one line not kept.
            Console.SetOut(new Tee(Console.Out, file));
            Console.SetError(new Tee(Console.Error, file));
            Console.WriteLine($"[log] {DateTime.Now:yyyy-MM-dd HH:mm:ss}  pid {Environment.ProcessId}  "
                            + $"{typeof(LogFile).Assembly.GetName().Version}");
        }
        catch { /* console only */ }
    }

    /// Lines are stamped with the time since start, because nearly every
    /// question asked of this log is "in what order, and how far apart?".
    /// Output and Error each have one, writing to the same file, so they share
    /// the clock and the lock.
    private sealed class Tee(TextWriter console, TextWriter file) : TextWriter
    {
        private static readonly System.Diagnostics.Stopwatch _since = System.Diagnostics.Stopwatch.StartNew();
        private static readonly object _gate = new();

        public override Encoding Encoding => Encoding.UTF8;

        public override void WriteLine(string? value)
        {
            lock (_gate)
            {
                try { console.WriteLine(value); } catch { /* no console: fine */ }
                try { file.WriteLine($"{_since.Elapsed.TotalSeconds,8:0.000}  {value}"); } catch { }
            }
        }

        public override void Write(char value)
        {
            lock (_gate)
            {
                try { console.Write(value); } catch { }
                try { file.Write(value); } catch { }
            }
        }
    }
}
