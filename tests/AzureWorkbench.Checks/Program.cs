using System.Net;
using System.Text;
using System.Text.Json;
using Azure.Core;
using AzureWorkbench.Models;
using AzureWorkbench.Services;

var checks = 0;
void Check(bool condition, string name)
{
    if (!condition) throw new InvalidOperationException($"FAIL: {name}");
    checks++;
    Console.WriteLine($"PASS: {name}");
}

async Task ThrowsAsync<T>(Func<Task> action, string name) where T : Exception
{
    try { await action(); }
    catch (T) { Check(true, name); return; }
    throw new InvalidOperationException($"FAIL: {name} did not throw {typeof(T).Name}");
}

static HttpResponseMessage Json(string content) => new(HttpStatusCode.OK)
{
    Content = new StringContent(content, Encoding.UTF8, "application/json"),
};

const string siteId = "/subscriptions/sub/resourceGroups/rg/providers/Microsoft.Web/sites/orders";
const string componentId = "/subscriptions/sub/resourceGroups/rg/providers/microsoft.insights/components/insights";
var credential = new TestCredential();
var app = new WebAppRef(siteId, "orders", "functionapp,linux", credential, componentId);
var insights = new InsightsRef(componentId, "insights", "app-id");
using var handler = new TestHandler();
using var http = new HttpClient(handler) { Timeout = Timeout.InfiniteTimeSpan };
var service = new AppLogService(http);

var draft = new MessageDraft("""{"event":"created"}""", "application/json", "OrderCreated",
    """{"source":"manual","retry":false,"attempt":3,"fraction":1.5}""");
var message = draft.ToMessage();
Check(message.Body.ToString() == draft.Body && message.ContentType == "application/json"
    && message.Subject == "OrderCreated" && !string.IsNullOrEmpty(message.MessageId), "new messages preserve body and metadata with a unique ID");
Check(message.ApplicationProperties["source"] is "manual" && message.ApplicationProperties["retry"] is false
    && message.ApplicationProperties["attempt"] is 3L && message.ApplicationProperties["fraction"] is 1.5d,
    "custom JSON properties become supported Service Bus scalar types");
Check(new MessageDraft("plain text", "text/plain", "", "").ToMessage().Body.ToString() == "plain text",
    "plain text messages are not parsed as JSON");
Check(draft.ToMessage().MessageId != message.MessageId, "separate manual sends receive different message IDs");
await ThrowsAsync<ArgumentException>(() => Task.FromResult((draft with { Body = " " }).ToMessage()), "reject empty message bodies");
await ThrowsAsync<JsonException>(() => Task.FromResult((draft with { Body = "{broken" }).ToMessage()), "validate JSON message bodies before sending");
await ThrowsAsync<JsonException>(() => Task.FromResult((draft with { ContentType = "application/cloudevents+json; charset=utf-8", Body = "bad" }).ToMessage()),
    "validate vendor JSON content types");
await ThrowsAsync<JsonException>(() => Task.FromResult((draft with { PropertiesJson = "broken" }).ToMessage()), "reject malformed custom properties");
foreach (var invalid in new[] { "[]", """{"nested":{}}""", """{"array":[]}""", """{"null":null}""", """{"":1}""", """{"key":1,"key":2}""", """{"overflow":1e999}""" })
    await ThrowsAsync<ArgumentException>(() => Task.FromResult((draft with { PropertiesJson = invalid }).ToMessage()), $"reject unsupported properties: {invalid}");

var query = AppLogService.BuildQuery(15, 2, "' | take 1 //\n\\", "orders");
Check(query.Contains("ago(15m)") && query.Contains(">= 2") && query.Contains("take 500"), "query time, severity and result limits");
Check(query.Contains(@"message contains '\' | take 1 //\n\\'") && query.Contains("cloud_RoleName =~ 'orders'"),
    "search is escaped as data, and role filter is explicit");
Check(!AppLogService.BuildQuery(30, 0, "", "").Contains("cloud_RoleName =~"), "blank role includes shared component logs");
await ThrowsAsync<ArgumentOutOfRangeException>(() => Task.FromResult(AppLogService.BuildQuery(0, 1, "", "")), "reject invalid query range");
await ThrowsAsync<ArgumentOutOfRangeException>(() => Task.FromResult(AppLogService.BuildQuery(30, 5, "", "")), "reject invalid severity");

var buffer = new LogBuffer(10);
buffer.Append("123456");
buffer.Append("789012");
Check(buffer.ToString() == "3456789012" && buffer.Truncated, "live buffer drops oldest text at capacity");
buffer.Append(new string('x', 100));
Check(buffer.ToString() == new string('x', 10), "single oversized chunk is bounded");
buffer.Clear();
Check(buffer.ToString() == "" && !buffer.Truncated, "clear resets truncation marker");

Check(LogFilter.DetectLevel("2026-11-03T10:00:00  PID[42] Information Started") == 1
    && LogFilter.DetectLevel("fail: Orders.Api[0] boom") == 3
    && LogFilter.DetectLevel("2026-11-03 [Warning] Function slow") == 2
    && LogFilter.DetectLevel("dbug: cache hit") == 0
    && LogFilter.DetectLevel("[CRITICAL] disk full") == 4
    && LogFilter.DetectLevel("   at Orders.Handler.Run()") == null
    && LogFilter.DetectLevel("loaded informational.dll") == null, "live log levels are detected from common formats");
const string stream = "info: started\nfail: boom\n   at Orders.Run()\nwarn: slow\ninfo: done\n";
Check(new LogFilter(3).Apply(stream, out var shownLines, out var totalLines) == "fail: boom\n   at Orders.Run()\n"
    && shownLines == 2 && totalLines == 5, "level filter keeps stack-trace continuation lines with their entry");
Check(new LogFilter(0, "SLOW").Apply(stream, out _, out _) == "warn: slow\n", "text filter is case-insensitive");
Check(new LogFilter(0, "boom").Apply(stream, out _, out _) == "fail: boom\n   at Orders.Run()\n", "text matches keep the whole multi-line entry");
Check(new LogFilter(0, "Orders.Run").Apply(stream, out _, out _) == "fail: boom\n   at Orders.Run()\n", "matching a stack-trace line shows its entry header");
Check(new LogFilter(0, @"^(fail|warn):", regex: true).Apply(stream, out _, out _) == "fail: boom\n   at Orders.Run()\nwarn: slow\n", "regex filter matches entries");
Check(new LogFilter().Apply(stream, out var allShown, out _) == stream && allShown == 5, "empty filter returns output unchanged");
await ThrowsAsync<ArgumentException>(() => Task.FromResult(new LogFilter(0, "(", regex: true)), "invalid regex is reported, not applied");
var entry = new LogEntry("2026-11-03T10:00:00Z", "2", "orders", "Payment retry");
Check(new LogFilter(2, "payment").Matches(entry) && !new LogFilter(3).Matches(entry) && !new LogFilter(0, "refund").Matches(entry)
    && new LogFilter(1).Matches(entry with { Severity = "" }), "recent entries filter by severity and text");

handler.Respond = async request =>
{
    Check(request.Headers.Authorization?.Scheme == "Bearer", "requests use Entra bearer authentication");
    var body = await request.Content!.ReadAsStringAsync();
    using var document = JsonDocument.Parse(body);
    Check(document.RootElement.GetProperty("query").GetString()!.Contains("ago(30m)"), "query is serialized as JSON");
    return Json("""
        {"tables":[{"name":"PrimaryResult",
        "columns":[{"name":"message"},{"name":"cloud_RoleName"},{"name":"severityLevel"},{"name":"timestamp"}],
        "rows":[["hello","orders",2,"2026-11-02T10:00:00Z"]]}]}
        """);
};
var entries = await service.QueryAsync(app, insights, 30, 0, "", "", default);
Check(entries is [{ Message: "hello", Severity: "2", Role: "orders" }], "query reads columns by name, not assumed position");
Check(credential.Scopes.Contains("https://api.applicationinsights.io/.default"), "Insights requests the data-plane scope");

handler.Respond = _ => Task.FromResult(Json("""{"tables":[],"error":{"code":"PartialError"}}"""));
await ThrowsAsync<InvalidOperationException>(async () => await service.QueryAsync(app, insights, 30, 0, "", "", default),
    "partial query failures are not presented as success");
handler.Respond = _ => Task.FromResult(new HttpResponseMessage(HttpStatusCode.Forbidden));
await ThrowsAsync<HttpRequestException>(async () => await service.QueryAsync(app, insights, 30, 0, "", "", default),
    "permission errors are explicit");

var page = 0;
handler.Respond = _ => Task.FromResult(++page == 1
    ? Json("""
        {"value":[{"id":"SITE_ID","name":"orders","kind":"functionapp,linux",
        "tags":{"hidden-link: /app-insights-resource-id":"COMPONENT_ID"}}],
        "nextLink":"https://management.azure.com/next"}
        """.Replace("SITE_ID", siteId).Replace("COMPONENT_ID", componentId))
    : Json("""
        {"value":[{"id":"/subscriptions/sub/resourceGroups/rg/providers/Microsoft.Web/sites/web","name":"web","kind":"app"}]}
        """));
var apps = await service.GetAppsAsync("sub", credential, default);
Check(apps.Count == 2 && apps.Single(a => a.IsFunction).InsightsResourceId == componentId, "app discovery follows pagination and reads Insights link");

handler.Respond = _ => Task.FromResult(Json("""{"value":[],"nextLink":"https://example.com/steal-token"}"""));
await ThrowsAsync<InvalidOperationException>(async () => await service.GetAppsAsync("sub", credential, default),
    "ARM pagination rejects foreign hosts");

page = 0;
handler.Respond = _ => Task.FromResult(++page == 1
    ? Json("""{"value":[]}""")
    : Json("""{"id":"COMPONENT_ID","name":"linked","properties":{"AppId":"linked-id"}}""".Replace("COMPONENT_ID", componentId)));
var components = await service.GetInsightsAsync(app, default);
Check(components is [{ AppId: "linked-id" }], "linked Insights resource is fetched when not in subscription list");

const string metadata = """{"properties":{"hostNameSslStates":[{"hostType":"Repository","name":"orders.scm.westeurope-01.azurewebsites.net"}]}}""";
handler.Respond = request =>
{
    if (request.RequestUri!.Host == "management.azure.com") return Task.FromResult(Json(metadata));
    Check(request.RequestUri.AbsolutePath == "/logstream", "live stream uses the SCM logstream endpoint");
    return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("first\nsecond\n") });
};
var output = new StringBuilder();
await foreach (var chunk in service.TailAsync(app, default)) output.Append(chunk);
Check(output.ToString() == "first\nsecond\n", "live stream returns log output");

handler.Respond = _ => Task.FromResult(Json("""{"properties":{"hostNameSslStates":[{"hostType":"Repository","name":"orders.scm.evil.example"}]}}"""));
await ThrowsAsync<InvalidOperationException>(async () => { await foreach (var _ in service.TailAsync(app, default)) { } },
    "SCM host validation prevents token disclosure");
handler.Respond = _ => Task.FromResult(Json("""{"properties":{}}"""));
await ThrowsAsync<InvalidOperationException>(async () => { await foreach (var _ in service.TailAsync(app, default)) { } },
    "unsupported hosting reports missing SCM endpoint");

handler.Respond = request => Task.FromResult(request.RequestUri!.Host == "management.azure.com"
    ? Json(metadata)
    : new HttpResponseMessage(HttpStatusCode.OK) { Content = new StreamContent(new IdleStream()) });
using var cancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(100));
await ThrowsAsync<OperationCanceledException>(async () => { await foreach (var _ in service.TailAsync(app, cancellation.Token)) { } },
    "Stop cancels even an idle log stream");
Check(credential.Scopes.Contains("https://management.azure.com/.default"), "ARM and Kudu use the management scope");

Console.WriteLine($"{checks} checks passed.");
WorkspaceChecks.Run();
ConfigChecks.Run();
ShellChecks.Run();
LogChecks.Run();
DeploymentChecks.Run();
ConfigCompareChecks.Run();
ServiceBusChecks.Run();
LimitChecks.Run();

sealed class TestHandler : HttpMessageHandler
{
    public Func<HttpRequestMessage, Task<HttpResponseMessage>> Respond { get; set; } =
        _ => throw new InvalidOperationException("Unexpected HTTP request");
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
        Respond(request);
}

sealed class TestCredential : TokenCredential
{
    public HashSet<string> Scopes { get; } = [];
    public override AccessToken GetToken(TokenRequestContext requestContext, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        foreach (var scope in requestContext.Scopes) Scopes.Add(scope);
        return new AccessToken("test-token", DateTimeOffset.UtcNow.AddHours(1));
    }

    public override ValueTask<AccessToken> GetTokenAsync(TokenRequestContext requestContext, CancellationToken cancellationToken) =>
        ValueTask.FromResult(GetToken(requestContext, cancellationToken));
}

sealed class IdleStream : Stream
{
    public override bool CanRead => true;
    public override bool CanSeek => false;
    public override bool CanWrite => false;
    public override long Length => throw new NotSupportedException();
    public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        await Task.Delay(Timeout.Infinite, cancellationToken);
        return 0;
    }
    public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    public override void Flush() => throw new NotSupportedException();
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
}
