using System.Diagnostics;
using System.Text;

namespace JpegRepair.Core;

/// <summary>
/// Performance log: timings and counters collected while "Fix all" runs, shown afterwards. Compiled in when the build
/// defines PERFLOG, which Directory.Build.props does unless EnablePerfLog is false (set it there, or build with
/// -p:EnablePerfLog=false, for releases). When it is off, <see cref="Enabled"/> is false and every call does nothing.
/// Thread-safe.
/// </summary>
public static class PerfLog
{
    /// <summary>True if this build collects the performance log.</summary>
    public static bool Enabled { get; } =
#if PERFLOG
        true;
#else
        false;
#endif

    private static readonly object Gate = new();
    private static readonly StringBuilder Text = new();
    private static readonly Dictionary<string, (int Count, long Total, long Max)> Totals = new();
    private static readonly Dictionary<string, long> Counters = new();
    private static long _started;

    /// <summary>Starts a new log (dropping anything collected before).</summary>
    public static void Begin(string title)
    {
        if (!Enabled)
            return;
        lock (Gate)
        {
            Text.Clear();
            Totals.Clear();
            Counters.Clear();
            _started = Stopwatch.GetTimestamp();
            Text.AppendLine(title);
#if DEBUG
            Text.AppendLine("Build: Debug (timings are not representative of a release build)");
#else
            Text.AppendLine("Build: Release");
#endif
            Text.AppendLine($"Processors: {Environment.ProcessorCount}");
            Text.AppendLine();
        }
    }

    /// <summary>Adds a line, stamped with the time since <see cref="Begin"/>.</summary>
    public static void Line(string text)
    {
        if (!Enabled)
            return;
        lock (Gate)
            Text.Append($"{Stopwatch.GetElapsedTime(_started).TotalSeconds,8:0.000} s  ").AppendLine(text);
    }

    /// <summary>Adds one occurrence of <paramref name="name"/> taking <paramref name="time"/> to the totals.</summary>
    public static void Add(string name, TimeSpan time)
    {
        if (!Enabled)
            return;
        lock (Gate)
        {
            Totals.TryGetValue(name, out var t);
            Totals[name] = (t.Count + 1, t.Total + time.Ticks, Math.Max(t.Max, time.Ticks));
        }
    }

    /// <summary>Adds <paramref name="amount"/> to the counter <paramref name="name"/>.</summary>
    public static void Count(string name, long amount)
    {
        if (!Enabled)
            return;
        lock (Gate)
            Counters[name] = Counters.GetValueOrDefault(name) + amount;
    }

    /// <summary>Times the block it is used for (<c>using var _ = PerfLog.Time("…");</c>) and adds it to the totals.</summary>
    public static Scope Time(string name) => new(Enabled ? name : null);

    /// <summary>The log so far, with the totals at the end. Empty if the log is off.</summary>
    public static string Report()
    {
        if (!Enabled)
            return "";
        lock (Gate)
        {
            var report = new StringBuilder(Text.ToString());
            if (Totals.Count > 0)
            {
                report.AppendLine();
                report.AppendLine($"{"Totals",-44} {"count",7} {"total s",10} {"mean ms",10} {"max ms",10}");
                foreach (var (name, t) in Totals.OrderByDescending(e => e.Value.Total))
                {
                    var total = TimeSpan.FromTicks(t.Total);
                    report.AppendLine($"{name,-44} {t.Count,7} {total.TotalSeconds,10:0.000} " +
                                      $"{total.TotalMilliseconds / t.Count,10:0.0} {TimeSpan.FromTicks(t.Max).TotalMilliseconds,10:0.0}");
                }
            }

            if (Counters.Count > 0)
            {
                report.AppendLine();
                report.AppendLine("Counters");
                foreach (var (name, value) in Counters.OrderBy(e => e.Key, StringComparer.Ordinal))
                    report.AppendLine($"{name,-44} {value,15:N0}");
            }

            return report.ToString();
        }
    }

    /// <summary>Timer for <see cref="Time"/>; adds the elapsed time when disposed.</summary>
    public readonly struct Scope : IDisposable
    {
        private readonly string? _name;
        private readonly long _start;

        internal Scope(string? name)
        {
            _name = name;
            _start = name == null ? 0 : Stopwatch.GetTimestamp();
        }

        public void Dispose()
        {
            if (_name != null)
                Add(_name, Stopwatch.GetElapsedTime(_start));
        }
    }
}
