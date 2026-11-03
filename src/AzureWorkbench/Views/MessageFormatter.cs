using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Azure.Messaging.ServiceBus;
using AzureWorkbench.Models;

namespace AzureWorkbench.Views;

public static partial class MessageFormatter
{
    [GeneratedRegex("^[0-9a-f]{2}-([0-9a-f]{32})-[0-9a-f]{16}-[0-9a-f]{2}$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex TraceParent();

    /// <summary>
    /// The 32-hex W3C trace id from a traceparent or Diagnostic-Id property ("00-&lt;traceid&gt;-&lt;spanid&gt;-&lt;flags&gt;"),
    /// otherwise the correlation id, otherwise null.
    /// </summary>
    public static string? TraceId(IEnumerable<KeyValuePair<string, object>> properties, string? correlationId)
    {
        var list = properties.ToList();
        foreach (var name in new[] { "traceparent", "Diagnostic-Id" })
            foreach (var (key, value) in list)
            {
                if (!key.Equals(name, StringComparison.OrdinalIgnoreCase) || value is not string text) continue;
                var match = TraceParent().Match(text.Trim());
                if (match.Success && match.Groups[1].Value.Any(c => c != '0')) return match.Groups[1].Value.ToLowerInvariant();
            }
        return string.IsNullOrWhiteSpace(correlationId) ? null : correlationId;
    }

    public static string? TraceId(ServiceBusReceivedMessage m) => TraceId(m.ApplicationProperties, m.CorrelationId);

    /// <summary>All searchable text of a message: IDs, subject, dead-letter info, application properties and body.</summary>
    public static string SearchText(ServiceBusReceivedMessage m)
    {
        var sb = new StringBuilder();
        sb.AppendLine(m.MessageId).AppendLine(m.Subject).AppendLine(m.CorrelationId)
            .AppendLine(m.DeadLetterReason).AppendLine(m.DeadLetterErrorDescription);
        foreach (var p in m.ApplicationProperties)
            sb.Append(p.Key).Append('=').AppendLine(Convert.ToString(p.Value, System.Globalization.CultureInfo.InvariantCulture));
        return sb.Append(m.Body.ToString()).ToString();
    }

    /// <summary>True when the message matches the text/regex filter and, if given, has exactly this dead-letter reason.</summary>
    public static bool Matches(ServiceBusReceivedMessage m, LogFilter filter, string? reason = null)
    {
        if (reason != null && m.DeadLetterReason != reason) return false;
        try { return filter.MatchesText(SearchText(m)); }
        catch (RegexMatchTimeoutException) { return false; }
    }

    static readonly JsonSerializerOptions Indented = new() { WriteIndented = true };

    public static string Describe(ServiceBusReceivedMessage m)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"MessageId:      {m.MessageId}");
        sb.AppendLine($"Sequence:       {m.SequenceNumber}");
        sb.AppendLine($"Enqueued:       {m.EnqueuedTime:O}");
        sb.AppendLine($"DeliveryCount:  {m.DeliveryCount}");
        sb.AppendLine($"ContentType:    {m.ContentType}");
        sb.AppendLine($"Subject:        {m.Subject}");
        sb.AppendLine($"CorrelationId:  {m.CorrelationId}");
        sb.AppendLine($"SessionId:      {m.SessionId}");
        if (m.DeadLetterReason != null)
        {
            sb.AppendLine($"DLQ reason:     {m.DeadLetterReason}");
            sb.AppendLine($"DLQ error:      {m.DeadLetterErrorDescription}");
            sb.AppendLine($"DLQ source:     {m.DeadLetterSource}");
        }
        foreach (var p in m.ApplicationProperties) sb.AppendLine($"[prop] {p.Key} = {p.Value}");
        sb.AppendLine().AppendLine("----- body -----");
        var body = m.Body.ToString();
        try { body = JsonSerializer.Serialize(JsonDocument.Parse(body), Indented); }
        catch (JsonException) { }
        return sb.Append(body).ToString();
    }
}
