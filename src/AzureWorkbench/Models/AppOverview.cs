namespace AzureWorkbench.Models;

/// <summary>One independently loaded part of the overview: either a value or the error that prevented loading it.</summary>
public record Section<T>(T? Value, string? Error = null)
{
    public bool Ok => Error == null;
}

public record SiteInfo(string State, string UsageState, string AvailabilityState, string DefaultHostName, List<string> HostNames,
    bool? HttpsOnly, bool? ClientAffinityEnabled, string Location, string Kind, string ServerFarmId, DateTimeOffset? LastModifiedUtc,
    List<string> OutboundIpAddresses)
{
    public bool IsRunning => State.Equals("Running", StringComparison.OrdinalIgnoreCase);
    public bool IsStopped => State.Equals("Stopped", StringComparison.OrdinalIgnoreCase);
}

public record SiteConfigInfo(string LinuxFxVersion, string NetFrameworkVersion, string Runtime, bool? AlwaysOn, string FtpsState,
    string MinTlsVersion, bool? Http20Enabled, string HealthCheckPath, int? NumberOfWorkers, bool? Use32BitWorkerProcess);

public record PlanInfo(string Name, string SkuName, string Tier, int? Capacity, string Kind, int? NumberOfSites, int? MaximumElasticWorkerCount);

public record HealthInfo(string AvailabilityState, string Summary, string ReasonType, string OccurredTime, string DetailedStatus);

public record InstanceInfo(string Name, string State);

public record SlotInfo(string Name, string State, string DefaultHostName);

public record FunctionInfo(string Name, bool IsDisabled, string Trigger, string InvokeUrl);

/// <summary>A metric over the last 24 hours. <see cref="Summary"/> is the sum for Total and the mean of non-null points for Average.</summary>
public record MetricSeries(string Name, string DisplayName, string Unit, string Aggregation, List<(DateTimeOffset Time, double? Value)> Points,
    double? Summary);

public record AppOverview(
    Section<SiteInfo> Site,
    Section<SiteConfigInfo> Config,
    Section<PlanInfo> Plan,
    Section<HealthInfo> Health,
    Section<List<InstanceInfo>> Instances,
    Section<List<SlotInfo>> Slots,
    Section<List<FunctionInfo>>? Functions,
    Section<List<MetricSeries>> Metrics);
