using System.Text;
using System.Text.RegularExpressions;

namespace Port.Content;

/// <summary>
/// Process-wide diagnostic ring buffer for sprite/texture/connect failures.
/// Safe to call from UI, net, and GL threads.
/// Identical consecutive messages are stacked as <c>… xN</c>.
/// </summary>
public static class DiagLog
{
    const int MaxLines = 400;
    static readonly object Gate = new();
    static readonly List<string> Lines = new(MaxLines);
    static readonly Regex TimestampPrefix = new(
        @"^\d{2}:\d{2}:\d{2}\.\d{3}\s+",
        RegexOptions.Compiled);

    public static void Info(string msg) => Add("I", msg);
    public static void Warn(string msg) => Add("W", msg);
    public static void Error(string msg) => Add("E", msg);

    public static void Add(string level, string msg)
    {
        if (string.IsNullOrWhiteSpace(msg))
            return;
        var body = $"[{level}] {msg.TrimEnd()}";
        var stamp = DateTime.Now.ToString("HH:mm:ss.fff");
        lock (Gate)
        {
            if (Lines.Count > 0
                && TrySplitStacked(Lines[^1], out _, out var prevBody, out var prevCount)
                && string.Equals(prevBody, body, StringComparison.Ordinal))
            {
                Lines[^1] = $"{stamp} {body} x{prevCount + 1}";
                return;
            }

            Lines.Add($"{stamp} {body}");
            if (Lines.Count > MaxLines)
                Lines.RemoveRange(0, Lines.Count - MaxLines + 50);
        }
    }

    public static IReadOnlyList<string> Snapshot(int max = 200)
    {
        lock (Gate)
        {
            if (Lines.Count == 0)
                return Array.Empty<string>();
            var take = Math.Min(max, Lines.Count);
            return Lines.Skip(Lines.Count - take).ToList();
        }
    }

    public static string Format(int max = 200)
    {
        var snap = Snapshot(max);
        return snap.Count == 0 ? "(diag empty)" : CollapseRepeated(snap);
    }

    static readonly Regex TrailingCount = new(@"\s+x(\d+)$", RegexOptions.Compiled | RegexOptions.IgnoreCase);
    static readonly Regex VolatileDigits = new(@"#\d+", RegexOptions.Compiled);
    static readonly Regex VolatilePayload = new(
        @"payload=[\d,.]+B|storeXf=\d+|raw=\d+B|z=\d+",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    /// <summary>
    /// Collapse consecutive identical lines (ignoring leading timestamps and <c>#123</c>
    /// counters) into <c>firstLine xN</c>.
    /// </summary>
    public static string CollapseRepeated(IEnumerable<string> lines)
    {
        string? runStamp = null;
        string? runBody = null;
        string? runKey = null;
        var runCount = 0;
        var sb = new StringBuilder();

        void Flush()
        {
            if (runBody is null) return;
            if (sb.Length > 0) sb.Append('\n');
            if (runStamp is not null)
                sb.Append(runStamp).Append(' ');
            sb.Append(runBody);
            if (runCount > 1)
                sb.Append(" x").Append(runCount);
            runStamp = null;
            runBody = null;
            runKey = null;
            runCount = 0;
        }

        foreach (var raw in lines)
        {
            if (string.IsNullOrEmpty(raw))
            {
                Flush();
                if (sb.Length > 0) sb.Append('\n');
                continue;
            }

            TrySplitStacked(raw, out var stamp, out var body, out var count);
            var key = VolatilePayload.Replace(VolatileDigits.Replace(body, "#"), "*");
            if (runKey is not null && string.Equals(runKey, key, StringComparison.Ordinal))
            {
                runCount += count;
                continue;
            }

            Flush();
            runStamp = stamp;
            runBody = body;
            runKey = key;
            runCount = count;
        }

        Flush();
        return sb.ToString();
    }

    static bool TrySplitStacked(string line, out string? stamp, out string body, out int count)
    {
        stamp = null;
        count = 1;
        var rest = line;
        var m = TimestampPrefix.Match(line);
        if (m.Success)
        {
            stamp = line[..m.Length].TrimEnd();
            rest = line[m.Length..];
        }

        var cm = TrailingCount.Match(rest);
        if (cm.Success && int.TryParse(cm.Groups[1].Value, out var n) && n > 1)
        {
            body = rest[..cm.Index];
            count = n;
            return true;
        }

        body = rest;
        return true;
    }

    public static void Clear()
    {
        lock (Gate) Lines.Clear();
    }
}
