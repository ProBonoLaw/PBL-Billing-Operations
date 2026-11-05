using Azure.Core;
using Azure.Identity;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Diagnostics;
using Microsoft.Extensions.Logging.Abstractions;

namespace BillingOperations;

public sealed class InsightsClient(HttpClient http, IConfiguration config,
    ILogger<InsightsClient>? logger = null) : IInsights
{
    private readonly DefaultAzureCredential credential = new();
    private readonly ILogger<InsightsClient> log = logger ?? NullLogger<InsightsClient>.Instance;

    public async Task<IReadOnlyList<Dictionary<string, object?>>> QueryAsync(string query,
        TimeSpan span, bool ingestion, CancellationToken ct)
    {
        var source = ingestion ? "ingestion" : "AIMIE";
        var watch = Stopwatch.StartNew();
        log.LogInformation("Application Insights: acquiring {Source} query token for {WindowMinutes} minute window",
            source, span.TotalMinutes);
        var appId = config.Required(ingestion ? "INGESTION_APP_INSIGHTS_APP_ID" :
            "AIMIE_APP_INSIGHTS_APP_ID");
        var token = await credential.GetTokenAsync(
            new TokenRequestContext(["https://api.applicationinsights.io/.default"]), ct);
        using var request = new HttpRequestMessage(HttpMethod.Post,
            $"https://api.applicationinsights.io/v1/apps/{Uri.EscapeDataString(appId)}/query");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token.Token);
        request.Content = new StringContent(JsonSerializer.Serialize(new
        {
            query, timespan = System.Xml.XmlConvert.ToString(span)
        }), Encoding.UTF8, "application/json");
        log.LogInformation("Application Insights: submitting {Source} query", source);
        using var response = await http.SendAsync(request, ct);
        if (!response.IsSuccessStatusCode)
            log.LogWarning("Application Insights: {Source} query returned HTTP {StatusCode}",
                source, (int)response.StatusCode);
        response.EnsureSuccessStatusCode();
        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
        var results = new List<Dictionary<string, object?>>();
        if (!doc.RootElement.TryGetProperty("tables", out var tables))
        {
            log.LogInformation("Application Insights: {Source} query completed with no tables in {ElapsedMs} ms",
                source, watch.ElapsedMilliseconds);
            return results;
        }
        foreach (var table in tables.EnumerateArray())
        {
            var columns = table.GetProperty("columns").EnumerateArray()
                .Select(x => x.GetProperty("name").GetString()!).ToArray();
            foreach (var row in table.GetProperty("rows").EnumerateArray())
            {
                var result = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
                var i = 0;
                foreach (var value in row.EnumerateArray()) result[columns[i++]] = Value(value);
                results.Add(result);
            }
        }
        log.LogInformation("Application Insights: {Source} query returned {RowCount} rows in {ElapsedMs} ms",
            source, results.Count, watch.ElapsedMilliseconds);
        return results;
    }

    private static object? Value(JsonElement value) => value.ValueKind switch
    {
        JsonValueKind.String => value.GetString(),
        JsonValueKind.Number => value.GetDouble(),
        JsonValueKind.True => true,
        JsonValueKind.False => false,
        _ => null
    };
}
