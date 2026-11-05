using Azure.Core;
using Azure.Identity;
using Azure.Security.KeyVault.Secrets;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Diagnostics;
using Microsoft.Extensions.Logging.Abstractions;

namespace BillingOperations;

public sealed class XeroClient(HttpClient http, IConfiguration config,
    ILogger<XeroClient>? logger = null) : IXero
{
    private readonly ILogger<XeroClient> log = logger ?? NullLogger<XeroClient>.Instance;
    private readonly DefaultAzureCredential credential = new();
    private readonly SemaphoreSlim tokenLock = new(1, 1);
    private XeroToken? cachedToken;
    private string? cachedScopes;
    private DateTimeOffset tokenExpires;

    public async Task<XeroToken> GetTokenAsync(IReadOnlyList<string> scopes, CancellationToken ct)
    {
        await tokenLock.WaitAsync(ct);
        try
        {
            var scopeText = string.Join(' ', scopes.Where(scope =>
                !string.IsNullOrWhiteSpace(scope)).Select(scope => scope.Trim()));
            if (cachedScopes == scopeText && cachedToken is not null &&
                tokenExpires > DateTimeOffset.UtcNow.AddMinutes(2))
            {
                log.LogDebug("Xero token: using cached token");
                return cachedToken;
            }
            log.LogInformation("Xero token: requesting token; explicit scopes: {HasExplicitScopes}",
                scopeText.Length > 0);
            var id = await SecretAsync("XERO_CLIENT_ID", "XeroClientID", ct);
            var secret = await SecretAsync("XERO_CLIENT_SECRET", "XeroClientSecret", ct);
            var basic = Convert.ToBase64String(Encoding.UTF8.GetBytes($"{id}:{secret}"));
            using var request = new HttpRequestMessage(HttpMethod.Post,
                "https://identity.xero.com/connect/token");
            request.Headers.Authorization = new AuthenticationHeaderValue("Basic", basic);
            var form = new Dictionary<string, string> { ["grant_type"] = "client_credentials" };
            if (scopeText.Length > 0) form["scope"] = scopeText;
            request.Content = new FormUrlEncodedContent(form);
            using var response = await http.SendAsync(request, ct);
            if (!response.IsSuccessStatusCode)
                log.LogWarning("Xero token: request returned HTTP {StatusCode}", (int)response.StatusCode);
            response.EnsureSuccessStatusCode();
            var body = JsonNode.Parse(await response.Content.ReadAsStringAsync(ct))!;
            var accessToken = body["access_token"]?.GetValue<string>()
                ?? throw new InvalidOperationException("Xero token response lacks access_token");
            var token = new XeroToken(accessToken,
                body["expires_in"]?.GetValue<int>() ?? 1800,
                body["token_type"]?.ToString() ?? "Bearer", body["scope"]?.ToString() ?? scopeText);
            cachedToken = token;
            cachedScopes = scopeText;
            tokenExpires = DateTimeOffset.UtcNow.AddSeconds(token.expires_in);
            log.LogInformation("Xero token: acquired; expires in {ExpiresInSeconds} seconds",
                token.expires_in);
            return token;
        }
        finally { tokenLock.Release(); }
    }

    public async Task<XeroUser?> GetUserAsync(string oid, CancellationToken ct)
    {
        log.LogInformation("Graph user: acquiring access token");
        TokenCredential graphCredential = !string.IsNullOrWhiteSpace(config["GRAPH_CLIENT_SECRET"])
            ? new ClientSecretCredential(config.Required("GRAPH_TENANT_ID"),
                config.Required("GRAPH_CLIENT_ID"), config.Required("GRAPH_CLIENT_SECRET"))
            : credential;
        var token = await graphCredential.GetTokenAsync(
            new TokenRequestContext(["https://graph.microsoft.com/.default"]), ct);
        log.LogInformation("Graph user: requesting profile");
        var select = "displayName,givenName,surname,mail,streetAddress,city,state,postalCode,country";
        using var request = new HttpRequestMessage(HttpMethod.Get,
            $"https://graph.microsoft.com/v1.0/users/{Uri.EscapeDataString(oid)}?$select={select}");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token.Token);
        using var response = await http.SendAsync(request, ct);
        if (response.StatusCode == HttpStatusCode.NotFound)
        {
            log.LogWarning("Graph user: profile not found");
            return null;
        }
        if (!response.IsSuccessStatusCode)
            log.LogWarning("Graph user: request returned HTTP {StatusCode}", (int)response.StatusCode);
        response.EnsureSuccessStatusCode();
        log.LogInformation("Graph user: profile loaded");
        var body = JsonNode.Parse(await response.Content.ReadAsStringAsync(ct))!;
        return new XeroUser
        {
            DisplayName = Str(body, "displayName"), GivenName = Str(body, "givenName"),
            Surname = Str(body, "surname"), Mail = Str(body, "mail"),
            StreetAddress = Str(body, "streetAddress"), City = Str(body, "city"),
            State = Str(body, "state"), PostalCode = Str(body, "postalCode"),
            Country = Str(body, "country")
        };
    }

    public async Task<string> GetOrCreateContactAsync(string oid, CancellationToken ct)
    {
        log.LogInformation("Xero contact: loading Graph user");
        var user = await GetUserAsync(oid, ct)
            ?? throw new KeyNotFoundException("Graph user not found");
        if (string.IsNullOrWhiteSpace(user.Mail))
            throw new InvalidOperationException("Graph user has no mail address");
        var escaped = user.Mail.Replace("\\", "\\\\").Replace("\"", "\\\"");
        var where = Uri.EscapeDataString($"EmailAddress==\"{escaped}\"");
        var found = await SendXeroAsync(HttpMethod.Get, $"Contacts?where={where}", null, null, ct);
        var contactId = FirstId(found, "Contacts", "ContactID");
        if (contactId is not null)
        {
            log.LogInformation("Xero contact: existing contact found");
            return contactId;
        }
        log.LogInformation("Xero contact: creating contact");
        var contact = new
        {
            Name = user.DisplayName, FirstName = user.GivenName, LastName = user.Surname,
            EmailAddress = user.Mail,
            Addresses = new[] { new { AddressType = "STREET", AddressLine1 = user.StreetAddress,
                user.City, Region = user.State, user.PostalCode, user.Country } },
            DefaultCurrency = "USD"
        };
        var created = await SendXeroAsync(HttpMethod.Post, "Contacts", contact, null, ct);
        log.LogInformation("Xero contact: contact created");
        return FirstId(created, "Contacts", "ContactID")
            ?? throw new InvalidOperationException("Xero contact response lacks ContactID");
    }

    public async Task<XeroItem> GetItemAsync(string code, CancellationToken ct)
    {
        log.LogInformation("Xero item: loading {ItemCode}", code);
        var body = await SendXeroAsync(HttpMethod.Get,
            $"Items/{Uri.EscapeDataString(code)}", null, null, ct);
        var item = (body["Items"] as JsonArray)?.FirstOrDefault()
            ?? throw new KeyNotFoundException($"Xero item not found: {code}");
        var result = new XeroItem
        {
            Code = Str(item, "Code"), Description = Str(item, "Description"),
            AccountCode = Str(item["SalesDetails"], "AccountCode"),
            UnitPrice = item["SalesDetails"]?["UnitPrice"]?.GetValue<decimal>() ?? 0
        };
        log.LogInformation("Xero item: loaded {ItemCode}", code);
        return result;
    }

    public async Task<string> CreateDraftInvoiceAsync(string contactId, string accountId,
        BillingPeriod period, IReadOnlyList<LineItem> items, CancellationToken ct)
    {
        log.LogInformation("Xero invoice: checking for existing draft");
        var reference = $"BILL-{accountId.ToLowerInvariant()}-{period.Start:yyyy-MM}";
        var where = Uri.EscapeDataString($"Reference==\"{reference}\"");
        var existing = await SendXeroAsync(HttpMethod.Get, $"Invoices?where={where}", null, null, ct);
        var existingId = FirstId(existing, "Invoices", "InvoiceID");
        if (existingId is not null)
        {
            log.LogInformation("Xero invoice: existing invoice found");
            return existingId;
        }
        log.LogInformation("Xero invoice: creating draft with {LineItemCount} lines", items.Count);
        var invoice = new
        {
            Type = "ACCREC", LineAmountTypes = "Exclusive", Status = "DRAFT",
            Date = DateTime.UtcNow.ToString("yyyy-MM-dd"),
            DueDate = DateTime.UtcNow.AddDays(30).ToString("yyyy-MM-dd"),
            Reference = reference, Contact = new { ContactID = contactId },
            LineItems = items.Select(x => new
            {
                x.ItemCode, x.AccountCode, x.UnitPrice, x.Quantity, x.Description
            }).ToArray()
        };
        var created = await SendXeroAsync(HttpMethod.Post, "Invoices",
            new { Invoices = new[] { invoice } }, $"invoice-{accountId.ToLowerInvariant()}-{period.Start:yyyy-MM-dd}", ct);
        log.LogInformation("Xero invoice: draft created");
        return FirstId(created, "Invoices", "InvoiceID")
            ?? throw new InvalidOperationException("Xero invoice response lacks InvoiceID");
    }

    private async Task<JsonNode> SendXeroAsync(HttpMethod method, string relative, object? body,
        string? idempotencyKey, CancellationToken ct)
    {
        var separator = relative.IndexOfAny(['?', '/']);
        var resource = separator < 0 ? relative : relative[..separator];
        var watch = Stopwatch.StartNew();
        log.LogInformation("Xero API: {Method} {Resource} started", method.Method, resource);
        var scopes = (config["XERO_SCOPES"] ?? "").Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var token = await GetTokenAsync(scopes, ct);
        using var request = new HttpRequestMessage(method,
            $"https://api.xero.com/api.xro/2.0/{relative}");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token.access_token);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        if (!string.IsNullOrWhiteSpace(config["XERO_TENANT_ID"]))
            request.Headers.Add("xero-tenant-id", config["XERO_TENANT_ID"]);
        if (idempotencyKey is not null) request.Headers.Add("Idempotency-Key", idempotencyKey);
        if (body is not null)
            request.Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json");
        using var response = await http.SendAsync(request, ct);
        log.Log(response.IsSuccessStatusCode ? LogLevel.Information : LogLevel.Warning,
            "Xero API: {Method} {Resource} returned HTTP {StatusCode} in {ElapsedMs} ms",
            method.Method, resource, (int)response.StatusCode, watch.ElapsedMilliseconds);
        response.EnsureSuccessStatusCode();
        return JsonNode.Parse(await response.Content.ReadAsStringAsync(ct))
            ?? throw new InvalidOperationException("Xero returned an empty body");
    }

    private async Task<string> SecretAsync(string setting, string name, CancellationToken ct)
    {
        if (!string.IsNullOrWhiteSpace(config[setting])) return config[setting]!;
        var client = new SecretClient(new Uri(config.Required("BILLING_KEY_VAULT_URI")), credential);
        return (await client.GetSecretAsync(name, cancellationToken: ct)).Value.Value;
    }

    private static string Str(JsonNode? node, string key) => node?[key]?.ToString() ?? "";
    private static string? FirstId(JsonNode node, string collection, string id) =>
        (node[collection] as JsonArray)?.FirstOrDefault()?[id]?.ToString();
}
