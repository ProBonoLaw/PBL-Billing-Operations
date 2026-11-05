using BillingOperations;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Globalization;
using System.Diagnostics;

var role = Environment.GetEnvironmentVariable("ROLE") ?? "gateway";
var builder = WebApplication.CreateBuilder(args);
builder.Logging.ClearProviders();
builder.Logging.AddJsonConsole(options => options.IncludeScopes = true);
builder.Logging.AddFilter("Microsoft.AspNetCore", LogLevel.Warning);
builder.Logging.AddFilter("System.Net.Http.HttpClient", LogLevel.Warning);
Settings.Configure(builder.Configuration);
builder.Services.ConfigureHttpJsonOptions(options =>
    options.SerializerOptions.PropertyNamingPolicy = null);
builder.Services.AddHttpClient<IInsights, InsightsClient>(client =>
    client.Timeout = TimeSpan.FromMinutes(2));
builder.Services.AddHttpClient<IXero, XeroClient>(client =>
    client.Timeout = TimeSpan.FromMinutes(2));
builder.Services.AddSingleton<IBillingStore, CosmosBillingStore>();
builder.Services.AddScoped<BillingFlows>();

if (role != "gateway")
{
    using var host = builder.Build();
    var runLogger = host.Services.GetRequiredService<ILoggerFactory>()
        .CreateLogger("BillingOperations.Run");
    var runId = Guid.NewGuid().ToString("N");
    using var runScope = runLogger.BeginScope(new Dictionary<string, object?>
    {
        ["RunId"] = runId, ["Role"] = role
    });
    var runWatch = Stopwatch.StartNew();
    runLogger.LogInformation("Worker run started");
    using var scope = host.Services.CreateScope();
    var flows = scope.ServiceProvider.GetRequiredService<BillingFlows>();
    var ct = CancellationToken.None;
    try
    {
        switch (role)
        {
            case "aimie-stats": await flows.RetrieveStatsAsync(ct); break;
            case "aimie-token-usage": await flows.RetrieveAimieUsageAsync(ct); break;
            case "ingestion-token-usage": await flows.RetrieveIngestionUsageAsync(ct); break;
            case "cleanup-usage-tokens": await flows.CleanupUsageAsync(DateTimeOffset.UtcNow, ct); break;
            case "invoice-generator": await flows.GenerateMonthlyInvoicesAsync(DateTimeOffset.UtcNow, ct); break;
            default: throw new ArgumentException($"Unknown ROLE: {role}");
        }
        runLogger.LogInformation("Worker run completed in {ElapsedMs} ms", runWatch.ElapsedMilliseconds);
    }
    catch (Exception error)
    {
        runLogger.LogError(error, "Worker run failed after {ElapsedMs} ms", runWatch.ElapsedMilliseconds);
        throw;
    }
    return;
}

var key = builder.Configuration.Required("API_KEY");
var app = builder.Build();
app.MapGet("/healthz", () => Results.Ok(new { status = "ok" }));
app.Use(async (ctx, next) =>
{
    if (ctx.Request.Path == "/healthz") { await next(); return; }
    var watch = Stopwatch.StartNew();
    using var requestScope = app.Logger.BeginScope(new Dictionary<string, object?>
    {
        ["RequestId"] = ctx.TraceIdentifier,
        ["TraceId"] = Activity.Current?.TraceId.ToString() ?? ""
    });
    ctx.Response.Headers["X-Request-ID"] = ctx.TraceIdentifier;
    app.Logger.LogInformation("HTTP request started: {Method} {Path}",
        ctx.Request.Method, ctx.Request.Path.Value);
    try { await next(); }
    catch (Exception error)
    {
        app.Logger.LogError(error, "HTTP request failed: {Method} {Path} after {ElapsedMs} ms",
            ctx.Request.Method, ctx.Request.Path.Value, watch.ElapsedMilliseconds);
        throw;
    }
    finally
    {
        app.Logger.LogInformation("HTTP request completed: {Method} {Path} {StatusCode} in {ElapsedMs} ms",
            ctx.Request.Method, ctx.Request.Path.Value, ctx.Response.StatusCode,
            watch.ElapsedMilliseconds);
    }
});
app.Use(async (ctx, next) =>
{
    try { await next(); }
    catch (JsonException)
    {
        ctx.Response.StatusCode = StatusCodes.Status400BadRequest;
        await ctx.Response.WriteAsJsonAsync(new { error = "invalid_json" });
    }
    catch (ArgumentException)
    {
        ctx.Response.StatusCode = StatusCodes.Status400BadRequest;
        await ctx.Response.WriteAsJsonAsync(new { error = "invalid_request" });
    }
    catch (HttpRequestException)
    {
        ctx.Response.StatusCode = StatusCodes.Status502BadGateway;
        await ctx.Response.WriteAsJsonAsync(new { error = "upstream_request_failed" });
    }
});
app.Use(async (ctx, next) =>
{
    if (ctx.Request.Path == "/healthz") { await next(); return; }
    var supplied = ctx.Request.Headers["X-API-Key"].ToString();
    var a = Encoding.UTF8.GetBytes(supplied);
    var b = Encoding.UTF8.GetBytes(key);
    if (a.Length != b.Length || !CryptographicOperations.FixedTimeEquals(a, b))
    {
        ctx.Response.StatusCode = StatusCodes.Status401Unauthorized;
        return;
    }
    await next();
});

app.MapPost("/v1/billing/activities/qa", async (HttpRequest request, BillingFlows flows,
    CancellationToken ct) =>
{
    var evt = await ReadEventAsync(request, ct);
    return Results.Ok(await flows.GenerateActivityRecordAsync(evt, false, ct));
});
app.MapPost("/v1/billing/activities/declaration", async (HttpRequest request,
    BillingFlows flows, CancellationToken ct) =>
{
    var evt = await ReadEventAsync(request, ct);
    return Results.Ok(await flows.GenerateActivityRecordAsync(evt, true, ct));
});
app.MapPost("/v1/billing/xero/token", async (HttpRequest request, IXero xero,
    CancellationToken ct) =>
{
    using var json = await JsonDocument.ParseAsync(request.Body, cancellationToken: ct);
    var scopes = json.RootElement.TryGetProperty("scopes", out var values)
        ? values.EnumerateArray().Select(x => x.GetString() ?? "").ToArray() : [];
    return Results.Ok(await xero.GetTokenAsync(scopes, ct));
});
app.MapPost("/v1/billing/xero/contact", async (HttpRequest request, IXero xero,
    CancellationToken ct) =>
{
    using var json = await JsonDocument.ParseAsync(request.Body, cancellationToken: ct);
    var oid = Text(json.RootElement, "oid");
    if (string.IsNullOrWhiteSpace(oid)) return Results.BadRequest("oid is required");
    try { return Results.Ok(new { contactId = await xero.GetOrCreateContactAsync(oid, ct) }); }
    catch (KeyNotFoundException) { return Results.NotFound("User Not Found"); }
});
app.MapPost("/v1/billing/invoices/line-items", async (HttpRequest request,
    BillingFlows flows, CancellationToken ct) =>
{
    using var json = await JsonDocument.ParseAsync(request.Body, cancellationToken: ct);
    var root = json.RootElement;
    if (!new[] { "accountId", "billingCode", "model", "llmType", "origin" }
            .All(key => HasString(root, key)) ||
        !new[] { "inputTokens", "outputTokens", "reasoningTokens", "totalTokens" }
            .All(key => HasLong(root, key)) || !HasBool(root, "billingCodeOnly"))
        return Results.BadRequest("line-item fields are required with their Logic App types");
    var group = new UsageGroup(Text(root, "accountId"), Text(root, "model"),
        Text(root, "llmType"), Text(root, "origin"), Number(root, "inputTokens"),
        Number(root, "outputTokens"), Number(root, "reasoningTokens"), Number(root, "totalTokens"));
    var code = Text(root, "billingCode");
    if (string.IsNullOrWhiteSpace(code)) return Results.BadRequest("billingCode is required");
    var codeOnly = root.TryGetProperty("billingCodeOnly", out var value) &&
        value.ValueKind == JsonValueKind.True;
    var items = await flows.GenerateLineItemsAsync(group, code, codeOnly, ct);
    return Results.Ok(items.Select(item => new
    {
        item.ItemCode, item.AccountCode,
        UnitPrice = item.UnitPrice.ToString(CultureInfo.InvariantCulture),
        Quantity = item.Quantity.ToString(CultureInfo.InvariantCulture),
        item.Description
    }).ToArray());
});
app.MapPost("/v1/billing/invoices/accounts", async (HttpRequest request,
    BillingFlows flows, CancellationToken ct) =>
{
    using var json = await JsonDocument.ParseAsync(request.Body, cancellationToken: ct);
    var root = json.RootElement;
    var account = new BillableAccount(Text(root, "oid"), Text(root, "email"),
        Text(root, "caseAccountId"), Text(root, "caseNumber"), Text(root, "billingCode"));
    if (!new[] { "oid", "email", "caseAccountId", "caseNumber", "billingCode" }
            .All(key => HasString(root, key)))
        return Results.BadRequest("oid, email, caseAccountId, caseNumber and billingCode are required");
    var requestedMonth = Text(root, "billingMonth");
    var period = string.IsNullOrWhiteSpace(requestedMonth)
        ? BillingPeriod.CurrentMonth(DateTimeOffset.UtcNow)
        : BillingPeriod.ForMonth(requestedMonth);
    return Results.Ok(await flows.GenerateInvoiceAsync(account, period, ct));
});
await app.RunAsync();

static async Task<DependencyEvent> ReadEventAsync(HttpRequest request, CancellationToken ct)
{
    using var json = await JsonDocument.ParseAsync(request.Body, cancellationToken: ct);
    var root = json.RootElement;
    return new DependencyEvent(Text(root, "logId"), Text(root, "timestamp"),
        Text(root, "accountId"), Text(root, "sessionId"), Text(root, "name"),
        Text(root, "operationId"), Duration(root, "duration"),
        Text(root, "performanceBucket"), Flag(root, "success"));
}

static string Text(JsonElement root, string key) =>
    root.TryGetProperty(key, out var value) ? value.ToString() : "";
static long Number(JsonElement root, string key) =>
    root.TryGetProperty(key, out var value) && long.TryParse(value.ToString(), out var number)
        ? number : 0;
static double Duration(JsonElement root, string key)
{
    var text = Text(root, key);
    if (double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var value))
        return value;
    return TimeSpan.TryParse(text, CultureInfo.InvariantCulture, out var span)
        ? span.TotalMilliseconds : 0;
}
static bool Flag(JsonElement root, string key) =>
    root.TryGetProperty(key, out var value) && bool.TryParse(value.ToString(), out var flag) && flag;
static bool HasString(JsonElement root, string key) =>
    root.TryGetProperty(key, out var value) && value.ValueKind == JsonValueKind.String;
static bool HasLong(JsonElement root, string key) =>
    root.TryGetProperty(key, out var value) && value.ValueKind == JsonValueKind.Number &&
    value.TryGetInt64(out _);
static bool HasBool(JsonElement root, string key) =>
    root.TryGetProperty(key, out var value) && value.ValueKind is JsonValueKind.True or JsonValueKind.False;
