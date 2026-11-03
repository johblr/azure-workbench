using Azure.Core;

namespace AzureWorkbench.Models;

public record WebAppRef(string Id, string Name, string Kind, TokenCredential Credential, string? InsightsResourceId)
{
    public string SubscriptionId => Id.Split('/')[2];
    public bool IsLinux => Kind.Split(',').Contains("linux", StringComparer.OrdinalIgnoreCase);
    public bool IsFunction => Kind.Split(',').Contains("functionapp", StringComparer.OrdinalIgnoreCase);
}

public enum AppPageKind { Logs, Configuration, Deployments }

/// <summary>Explorer tree target: one of an app's tabs.</summary>
public record AppPage(WebAppRef App, AppPageKind Kind);

public record InsightsRef(string Id, string Name, string AppId)
{
    public override string ToString() => Name;
}

public record LogEntry(string Timestamp, string Severity, string Role, string Message, string OperationId = "", string ItemType = "");

/// <summary>Application Insights tables a recent-logs query can read.</summary>
[Flags]
public enum TelemetryTypes
{
    None = 0,
    Traces = 1,
    Exceptions = 2,
    Requests = 4,
    Dependencies = 8,
    Default = Traces | Exceptions,
    All = Traces | Exceptions | Requests | Dependencies,
}
