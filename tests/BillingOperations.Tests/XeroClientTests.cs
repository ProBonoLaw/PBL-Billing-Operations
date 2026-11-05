using BillingOperations;
using Microsoft.Extensions.Configuration;
using System.Net;
using System.Text.Json;
using Xunit;

namespace BillingOperations.Tests;

public sealed class XeroClientTests
{
    [Fact]
    public async Task CreatesDraftWithStableReferenceAndIdempotencyKey()
    {
        var handler = new Handler();
        var config = new ConfigurationBuilder().AddInMemoryCollection(
            new Dictionary<string, string?>
            {
                ["XERO_CLIENT_ID"] = "client", ["XERO_CLIENT_SECRET"] = "secret",
                ["XERO_TENANT_ID"] = "tenant"
            }).Build();
        var xero = new XeroClient(new HttpClient(handler), config);
        var period = BillingPeriod.PreviousCompleteMonth(new DateTimeOffset(2026, 10, 1,
            4, 0, 0, TimeSpan.Zero));
        var id = await xero.CreateDraftInvoiceAsync("contact", "ACCOUNT", period,
            [new("item", "200", 2.5m, 1.5m, "usage")], default);
        Assert.Equal("invoice-id", id);
        Assert.Equal(1, handler.TokenCalls);
        Assert.Equal("grant_type=client_credentials", handler.TokenForms.Single());
        Assert.Equal("invoice-account-2026-09-01", handler.IdempotencyKey);
        Assert.Equal("tenant", handler.TenantId);
        using var body = JsonDocument.Parse(handler.InvoiceBody!);
        var invoice = body.RootElement.GetProperty("Invoices")[0];
        Assert.Equal("BILL-account-2026-09", invoice.GetProperty("Reference").GetString());
        Assert.Equal("DRAFT", invoice.GetProperty("Status").GetString());
        Assert.Equal("contact", invoice.GetProperty("Contact").GetProperty("ContactID").GetString());
        Assert.Equal(1.5m, invoice.GetProperty("LineItems")[0].GetProperty("Quantity").GetDecimal());
        var item = await xero.GetItemAsync("item", default);
        Assert.Equal(2.5m, item.UnitPrice);
        Assert.Equal("200", item.AccountCode);
        Assert.Equal(1, handler.TokenCalls);
    }

    [Fact]
    public async Task ExplicitScopesAreIncludedInTokenRequest()
    {
        var handler = new Handler();
        var config = new ConfigurationBuilder().AddInMemoryCollection(
            new Dictionary<string, string?>
            {
                ["XERO_CLIENT_ID"] = "client", ["XERO_CLIENT_SECRET"] = "secret"
            }).Build();
        var xero = new XeroClient(new HttpClient(handler), config);

        await xero.GetTokenAsync(["accounting.contacts", "accounting.settings"], default);

        var form = handler.TokenForms.Single();
        Assert.Contains("grant_type=client_credentials", form);
        Assert.Contains("scope=accounting.contacts", form);
        Assert.Contains("accounting.settings", form);
    }

    private sealed class Handler : HttpMessageHandler
    {
        public int TokenCalls { get; private set; }
        public List<string> TokenForms { get; } = [];
        public string? InvoiceBody { get; private set; }
        public string? IdempotencyKey { get; private set; }
        public string? TenantId { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            if (request.RequestUri!.Host == "identity.xero.com")
            {
                TokenCalls++;
                Assert.Equal("Basic", request.Headers.Authorization?.Scheme);
                TokenForms.Add(await request.Content!.ReadAsStringAsync(cancellationToken));
                return Json("{\"access_token\":\"token\",\"expires_in\":1800}");
            }
            TenantId = request.Headers.GetValues("xero-tenant-id").Single();
            if (request.RequestUri.AbsolutePath.Contains("/Items/", StringComparison.Ordinal))
                return Json("{\"Items\":[{\"Code\":\"item\",\"Description\":\"service\",\"SalesDetails\":{\"AccountCode\":\"200\",\"UnitPrice\":2.5}}]}");
            if (request.Method == HttpMethod.Get)
            {
                Assert.Contains("Reference%3D%3D", request.RequestUri.ToString(),
                    StringComparison.OrdinalIgnoreCase);
                return Json("{\"Invoices\":[]}");
            }
            IdempotencyKey = request.Headers.GetValues("Idempotency-Key").Single();
            InvoiceBody = await request.Content!.ReadAsStringAsync(cancellationToken);
            return Json("{\"Invoices\":[{\"InvoiceID\":\"invoice-id\"}]}");
        }

        private static HttpResponseMessage Json(string body) => new(HttpStatusCode.OK)
        {
            Content = new StringContent(body, System.Text.Encoding.UTF8, "application/json")
        };
    }
}
