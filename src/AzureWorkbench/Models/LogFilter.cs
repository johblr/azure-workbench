using System.Text;
using System.Text.RegularExpressions;

namespace AzureWorkbench.Models;

/// <summary>Client-side display filter for log output: minimum level plus text or regex match. Never changes Azure settings.</summary>
public sealed partial class LogFilter
{
    public static readonly string[] LevelNames = ["All levels", "Information+", "Warning+", "Error+", "Critical"];

    readonly Regex? _regex;
    readonly string _text;

    public int MinLevel { get; }
    public bool IsEmpty => MinLevel == 0 && _text.Length == 0;

    /// <summary>Creates a filter. With <paramref name="regex"/>, invalid patterns throw <see cref="ArgumentException"/>.</summary>
    public LogFilter(int minLevel = 0, string? text = null, bool regex = false)
    {
        MinLevel = Math.Clamp(minLevel, 0, 4);
        _text = text ?? "";
        if (regex && _text.Length > 0)
            _regex = new Regex(_text, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(200));
    }

    public bool MatchesText(string text) =>
        _text.Length == 0 || (_regex?.IsMatch(text) ?? text.Contains(_text, StringComparison.OrdinalIgnoreCase));

    /// <summary>Application Insights severity: 0 verbose .. 4 critical. Entries without a severity are treated as Information.</summary>
    public bool Matches(LogEntry entry) =>
        (int.TryParse(entry.Severity, out var level) ? level : 1) >= MinLevel
        && MatchesText($"{entry.Timestamp} {entry.ItemType} {entry.Role} {entry.OperationId} {entry.Message}");

    /// <summary>
    /// Filters raw stream text by entry. A line with a detectable level starts an entry; following lines without one
    /// (such as stack traces) belong to it, so level and text matches keep multi-line entries together.
    /// Counts are in lines.
    /// </summary>
    public string Apply(string text, out int shown, out int total)
    {
        total = 0;
        var shownLines = 0;
        if (text.Length == 0)
        {
            shown = 0;
            return text;
        }
        var output = new StringBuilder(IsEmpty ? 0 : Math.Min(text.Length, 1 << 16));
        var entry = new StringBuilder();
        var entryLines = 0;
        int? level = null;
        void Emit()
        {
            if (entryLines == 0) return;
            var value = entry.ToString();
            if ((level ?? 1) >= MinLevel && MatchesText(value))
            {
                shownLines += entryLines;
                output.Append(value);
            }
            entry.Clear();
            entryLines = 0;
        }
        foreach (var line in text.AsSpan().EnumerateLines())
        {
            if (line.IsEmpty) continue;
            total++;
            var value = line.ToString();
            if (DetectLevel(value) is { } detected)
            {
                Emit();
                level = detected;
            }
            entry.Append(value).Append('\n');
            entryLines++;
        }
        Emit();
        shown = shownLines;
        return IsEmpty ? text : output.ToString();
    }

    /// <summary>Best-effort level detection for App Service / Functions / ASP.NET console log lines.</summary>
    public static int? DetectLevel(string line)
    {
        var head = line.Length > 120 ? line[..120] : line;
        var match = LevelToken().Match(head);
        if (!match.Success) return null;
        return match.Value.ToLowerInvariant() switch
        {
            "trace" or "trce" or "verbose" or "debug" or "dbug" => 0,
            "info" or "information" => 1,
            "warn" or "warning" => 2,
            "error" or "fail" or "err" => 3,
            _ => 4,
        };
    }

    [GeneratedRegex(@"(?<![\w.])(trace|trce|verbose|debug|dbug|information|info|warning|warn|error|fail|err|critical|crit|fatal)(?![\w.])",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex LevelToken();
}
