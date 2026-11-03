using System.Globalization;
using System.Text.Encodings.Web;
using System.Text.Json;
using Azure.Messaging.ServiceBus;

namespace AzureWorkbench.Models;

/// <summary>One message in an export file. Bodies are archived as text.</summary>
public sealed record ArchivedMessage(
    string? MessageId, string? Subject, string? ContentType, string? CorrelationId, string Body,
    IReadOnlyDictionary<string, object> ApplicationProperties, DateTimeOffset? EnqueuedTime, long? SequenceNumber,
    string? DeadLetterReason, string? DeadLetterErrorDescription);

/// <summary>JSON (de)serialisation of Service Bus messages for export, import and copy. Pure: no UI or network.</summary>
public static class MessageArchive
{
    static readonly JsonWriterOptions WriterOptions = new() { Indented = true, Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };
    static readonly string[] SystemProperties = ["DeadLetterReason", "DeadLetterErrorDescription"];

    /// <summary>Application properties with string, boolean or number values only; the dead-letter markers are excluded.</summary>
    public static Dictionary<string, object> ScalarProperties(IEnumerable<KeyValuePair<string, object>> properties)
    {
        var result = new Dictionary<string, object>();
        foreach (var (key, value) in properties)
        {
            if (SystemProperties.Contains(key, StringComparer.Ordinal)) continue;
            if (value is string or bool or byte or sbyte or short or ushort or int or uint or long or ulong or float or double or decimal)
            {
                if (value is double d && !double.IsFinite(d)) continue;
                if (value is float f && !float.IsFinite(f)) continue;
                result[key] = value;
            }
        }
        return result;
    }

    /// <summary>Scalar application properties as an indented JSON object, or "" when there are none.</summary>
    public static string PropertiesToJson(IEnumerable<KeyValuePair<string, object>> properties)
    {
        var scalars = ScalarProperties(properties);
        if (scalars.Count == 0) return "";
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream, WriterOptions)) WriteProperties(writer, scalars);
        return System.Text.Encoding.UTF8.GetString(stream.ToArray());
    }

    static void WriteProperties(Utf8JsonWriter writer, Dictionary<string, object> properties)
    {
        writer.WriteStartObject();
        foreach (var (key, value) in properties)
        {
            switch (value)
            {
                case string s: writer.WriteString(key, s); break;
                case bool b: writer.WriteBoolean(key, b); break;
                case decimal m: writer.WriteNumber(key, m); break;
                case float f: writer.WriteNumber(key, (double)f); break;
                case double d: writer.WriteNumber(key, d); break;
                case ulong u: writer.WriteNumber(key, u); break;
                default: writer.WriteNumber(key, Convert.ToInt64(value, CultureInfo.InvariantCulture)); break;
            }
        }
        writer.WriteEndObject();
    }

    static void WriteMessage(Utf8JsonWriter writer, ServiceBusReceivedMessage m)
    {
        writer.WriteStartObject();
        writer.WriteString("messageId", m.MessageId);
        writer.WriteString("subject", m.Subject);
        writer.WriteString("contentType", m.ContentType);
        writer.WriteString("correlationId", m.CorrelationId);
        writer.WriteString("body", m.Body.ToString());
        writer.WritePropertyName("applicationProperties");
        WriteProperties(writer, ScalarProperties(m.ApplicationProperties));
        writer.WriteString("enqueuedTime", m.EnqueuedTime.ToString("O", CultureInfo.InvariantCulture));
        writer.WriteNumber("sequenceNumber", m.SequenceNumber);
        writer.WriteString("deadLetterReason", m.DeadLetterReason);
        writer.WriteString("deadLetterErrorDescription", m.DeadLetterErrorDescription);
        writer.WriteEndObject();
    }

    /// <summary>Serialises messages as a JSON array.</summary>
    public static string Serialize(IEnumerable<ServiceBusReceivedMessage> messages)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream, WriterOptions))
        {
            writer.WriteStartArray();
            foreach (var m in messages) WriteMessage(writer, m);
            writer.WriteEndArray();
        }
        return System.Text.Encoding.UTF8.GetString(stream.ToArray());
    }

    /// <summary>Serialises a single message as a JSON object.</summary>
    public static string SerializeOne(ServiceBusReceivedMessage message)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream, WriterOptions)) WriteMessage(writer, message);
        return System.Text.Encoding.UTF8.GetString(stream.ToArray());
    }

    /// <summary>Parses an export file. Throws <see cref="FormatException"/> when the content is not an array of message objects.</summary>
    public static List<ArchivedMessage> Parse(string json)
    {
        JsonDocument document;
        try { document = JsonDocument.Parse(json); }
        catch (JsonException ex) { throw new FormatException($"The file is not valid JSON: {ex.Message}", ex); }
        using (document)
        {
            if (document.RootElement.ValueKind != JsonValueKind.Array)
                throw new FormatException("Expected a JSON array of messages.");
            var result = new List<ArchivedMessage>();
            var index = 0;
            foreach (var element in document.RootElement.EnumerateArray())
            {
                index++;
                if (element.ValueKind != JsonValueKind.Object)
                    throw new FormatException($"Item {index} is not a message object.");
                if (!element.TryGetProperty("body", out var body) || body.ValueKind != JsonValueKind.String)
                    throw new FormatException($"Item {index} has no text body.");
                var properties = new Dictionary<string, object>();
                if (element.TryGetProperty("applicationProperties", out var props) && props.ValueKind == JsonValueKind.Object)
                {
                    foreach (var p in props.EnumerateObject())
                    {
                        object? value = p.Value.ValueKind switch
                        {
                            JsonValueKind.String => p.Value.GetString(),
                            JsonValueKind.True => true,
                            JsonValueKind.False => false,
                            JsonValueKind.Number when p.Value.TryGetInt64(out var n) => n,
                            JsonValueKind.Number when p.Value.TryGetDouble(out var d) && double.IsFinite(d) => d,
                            _ => null,
                        };
                        if (value != null && !string.IsNullOrWhiteSpace(p.Name)) properties[p.Name] = value;
                    }
                }
                result.Add(new ArchivedMessage(Str(element, "messageId"), Str(element, "subject"), Str(element, "contentType"),
                    Str(element, "correlationId"), body.GetString()!, properties,
                    DateTimeOffset.TryParse(Str(element, "enqueuedTime"), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var enqueued) ? enqueued : null,
                    element.TryGetProperty("sequenceNumber", out var seq) && seq.ValueKind == JsonValueKind.Number && seq.TryGetInt64(out var s) ? s : null,
                    Str(element, "deadLetterReason"), Str(element, "deadLetterErrorDescription")));
            }
            return result;
        }
    }

    static string? Str(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    /// <summary>Builds a new message to send. The file's message ID is kept (so duplicate detection can work); a new one is made when absent.</summary>
    public static ServiceBusMessage ToMessage(ArchivedMessage a)
    {
        var message = new ServiceBusMessage(BinaryData.FromString(a.Body))
        {
            MessageId = string.IsNullOrEmpty(a.MessageId) ? Guid.NewGuid().ToString() : a.MessageId,
            Subject = string.IsNullOrEmpty(a.Subject) ? null : a.Subject,
            ContentType = string.IsNullOrEmpty(a.ContentType) ? null : a.ContentType,
            CorrelationId = string.IsNullOrEmpty(a.CorrelationId) ? null : a.CorrelationId,
        };
        foreach (var (key, value) in a.ApplicationProperties) message.ApplicationProperties[key] = value;
        return message;
    }
}
