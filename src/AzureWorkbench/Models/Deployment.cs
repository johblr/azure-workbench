namespace AzureWorkbench.Models;

/// <summary>One App Service / Function App deployment as recorded by Kudu (via ARM).</summary>
public record Deployment(string Id, int Status, string StatusText, string Author, string Deployer, string Message,
    DateTimeOffset? Start, DateTimeOffset? End, bool Active)
{
    public const int Pending = 0, Building = 1, Deploying = 2, Failed = 3, Success = 4;

    public static string StatusName(int status) => status switch
    {
        Pending => "Pending",
        Building => "Building",
        Deploying => "Deploying",
        Failed => "Failed",
        Success => "Success",
        _ => $"Unknown ({status})",
    };

    public bool IsFailed => Status == Failed;
    public bool IsSuccess => Status == Success;
    public bool InProgress => Status is Pending or Building or Deploying;

    /// <summary>Time from start to end; null when either is unknown or the end precedes the start.</summary>
    public TimeSpan? Duration => Start is { } s && End is { } e && e >= s ? e - s : null;

    public static string FormatDuration(TimeSpan? duration)
    {
        if (duration is not { } d) return "";
        if (d.TotalSeconds < 1) return "<1s";
        if (d.TotalMinutes < 1) return $"{(int)d.TotalSeconds}s";
        if (d.TotalHours < 1) return $"{(int)d.TotalMinutes}m {d.Seconds}s";
        return $"{(int)d.TotalHours}h {d.Minutes}m";
    }
}

/// <summary>One line of a deployment log. <see cref="DetailsId"/> is set when Kudu holds a nested log for the line.</summary>
public record DeploymentLogEntry(DateTimeOffset? Time, string Message, string? DetailsId);
