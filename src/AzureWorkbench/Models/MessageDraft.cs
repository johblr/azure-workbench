using System.Text.Json;
using Azure.Messaging.ServiceBus;

namespace AzureWorkbench.Models;

public sealed record MessageDraft(string Body, string ContentType, string Subject, string PropertiesJson)
{
    public ServiceBusMessage ToMessage()
    {
        if (string.IsNullOrWhiteSpace(Body))
            throw new ArgumentException("Enter a message body.");
        var type = ContentType.Trim();
        var mediaType = type.Split(';')[0].Trim();
        if (mediaType.Equals("application/json", StringComparison.OrdinalIgnoreCase)
            || mediaType.EndsWith("+json", StringComparison.OrdinalIgnoreCase))
        {
            using var body = JsonDocument.Parse(Body);
        }
        var message = new ServiceBusMessage(BinaryData.FromString(Body))
        {
            MessageId = Guid.NewGuid().ToString(),
            ContentType = string.IsNullOrEmpty(type) ? null : type,
            Subject = string.IsNullOrWhiteSpace(Subject) ? null : Subject,
        };
        if (string.IsNullOrWhiteSpace(PropertiesJson)) return message;
        using var properties = JsonDocument.Parse(PropertiesJson);
        if (properties.RootElement.ValueKind != JsonValueKind.Object)
            throw new ArgumentException("Custom properties must be a JSON object.");
        foreach (var property in properties.RootElement.EnumerateObject())
        {
            if (string.IsNullOrWhiteSpace(property.Name))
                throw new ArgumentException("Custom property names cannot be blank.");
            object value = property.Value.ValueKind switch
            {
                JsonValueKind.String => property.Value.GetString()!,
                JsonValueKind.True => true,
                JsonValueKind.False => false,
                JsonValueKind.Number when property.Value.TryGetInt64(out var number) => number,
                JsonValueKind.Number when property.Value.TryGetDouble(out var number) && double.IsFinite(number) => number,
                _ => throw new ArgumentException($"Custom property '{property.Name}' must be a string, boolean or finite number."),
            };
            if (!message.ApplicationProperties.TryAdd(property.Name, value))
                throw new ArgumentException($"Duplicate custom property '{property.Name}'.");
        }
        return message;
    }
}
