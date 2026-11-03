using System.Text;

namespace AzureWorkbench.Models;

public enum CompareState { OnlyLeft, OnlyRight, Different, Same }

/// <summary>One setting or connection string compared between two apps. Types are null for application settings.</summary>
public sealed record ConfigCompareRow(
    string Section, string Name, CompareState State,
    string? LeftValue, string? RightValue, bool LeftSlot, bool RightSlot, string? LeftType, string? RightType);

/// <summary>Pure comparison and export helpers for app configurations.</summary>
public static class ConfigComparison
{
    public const string SettingSection = "Setting";
    public const string ConnectionSection = "Connection string";

    /// <summary>Compares two configurations by name (case-insensitive). Rows are Same only when value, type and slot flag all match.</summary>
    public static List<ConfigCompareRow> Compare(AppConfiguration left, AppConfiguration right)
    {
        var rows = new List<ConfigCompareRow>();
        Merge(SettingSection,
            left.Settings.Select(s => (s.Name, s.Value, (string?)null, s.SlotSetting)),
            right.Settings.Select(s => (s.Name, s.Value, (string?)null, s.SlotSetting)), rows);
        Merge(ConnectionSection,
            left.ConnectionStrings.Select(c => (c.Name, c.Value, (string?)c.Type, c.SlotSetting)),
            right.ConnectionStrings.Select(c => (c.Name, c.Value, (string?)c.Type, c.SlotSetting)), rows);
        return rows;
    }

    static void Merge(string section,
        IEnumerable<(string Name, string Value, string? Type, bool Slot)> left,
        IEnumerable<(string Name, string Value, string? Type, bool Slot)> right, List<ConfigCompareRow> rows)
    {
        var l = left.GroupBy(i => i.Name, StringComparer.OrdinalIgnoreCase).ToDictionary(g => g.Key, g => g.Last(), StringComparer.OrdinalIgnoreCase);
        var r = right.GroupBy(i => i.Name, StringComparer.OrdinalIgnoreCase).ToDictionary(g => g.Key, g => g.Last(), StringComparer.OrdinalIgnoreCase);
        var names = l.Keys.Union(r.Keys, StringComparer.OrdinalIgnoreCase).OrderBy(n => n, StringComparer.OrdinalIgnoreCase);
        foreach (var key in names)
        {
            var hasL = l.TryGetValue(key, out var a);
            var hasR = r.TryGetValue(key, out var b);
            var state = !hasR ? CompareState.OnlyLeft : !hasL ? CompareState.OnlyRight
                : a.Value == b.Value && a.Type == b.Type && a.Slot == b.Slot ? CompareState.Same : CompareState.Different;
            rows.Add(new ConfigCompareRow(section, hasL ? a.Name : b.Name, state,
                hasL ? a.Value : null, hasR ? b.Value : null, hasL && a.Slot, hasR && b.Slot,
                hasL ? a.Type : null, hasR ? b.Type : null));
        }
    }

    /// <summary>Formats settings as dotenv lines (KEY=value). Values with spaces, #, quotes or line breaks are double-quoted with \ and " escaped.</summary>
    public static string ToEnv(IEnumerable<AppSetting> settings)
    {
        var sb = new StringBuilder();
        foreach (var s in settings) sb.Append(s.Name).Append('=').Append(EnvValue(s.Value)).Append('\n');
        return sb.ToString();
    }

    static string EnvValue(string value)
    {
        if (!value.Any(c => c is ' ' or '#' or '"' or '\'' or '\n' or '\r' or '\t')) return value;
        return "\"" + value.Replace("\\", "\\\\").Replace("\"", "\\\"").Replace("\r", "\\r").Replace("\n", "\\n").Replace("\t", "\\t") + "\"";
    }
}
