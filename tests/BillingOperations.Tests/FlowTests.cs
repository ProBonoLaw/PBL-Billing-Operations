using BillingOperations;
using Microsoft.Extensions.Logging.Abstractions;
using Newtonsoft.Json;
using Xunit;

namespace BillingOperations.Tests;

public sealed class FlowTests
{
    [Fact]
    public async Task ImportsAimieAndIngestionUsageWithStableSourceIds()
    {
        var insights = new FakeInsights();
        var store = new FakeStore();
        var flows = Create(insights, store, new FakeXero());
        insights.Rows = [Row(("accountId", "a"), ("timestamp", "2026-09-10T12:00:00Z"),
            ("operationId", "op"), ("sessionId", "s"), ("model", "m"),
            ("llmType", "openai-chat"), ("inputTokens", 150), ("outputTokens", 25),
            ("isSecondaryLLM", "True"))];
        Assert.Equal(1, await flows.RetrieveAimieUsageAsync(default));
        Assert.Equal("2026-09-10T12:00:00Z_op_ms", store.Usage[0].id);
        Assert.Equal("AIMIE", store.Usage[0].origin);
        Assert.True(store.Usage[0].isSecondaryLLM);

        insights.Rows = [Row(("accountId", "a"), ("timestamp", "2026-09-10T12:00:00Z"),
            ("operationId", "op2"), ("model", "e"), ("inputTokens", 500))];
        Assert.Equal(1, await flows.RetrieveIngestionUsageAsync(default));
        Assert.Equal("2026-09-10T12:00:00Z_op2_e", store.Usage[1].id);
        Assert.Equal("Ingress", store.Usage[1].origin);
        Assert.Equal("op2", store.Usage[1].sessionId);
        Assert.Equal("azure-openai-chat", store.Usage[1].llmType);
    }

    [Fact]
    public async Task DeclarationAddsOrchestrationDurationAndStatus()
    {
        var insights = new FakeInsights
        {
            Rows = [Row(("id", "child"), ("timestamp", "2026-09-10"),
                ("name", "orchestration:DeclarationOrchestration"),
                ("duration", "00:00:02"), ("performanceBucket", "1-3s"),
                ("success", "False"))]
        };
        var store = new FakeStore();
        var flows = Create(insights, store, new FakeXero());
        var result = await flows.GenerateActivityRecordAsync(new DependencyEvent(
            "log", "2026-09-10", "account", "session", "declaration", "op", 100,
            "<250ms", true), true, default);
        Assert.Equal(2100, result.duration);
        Assert.False(result.success);
        Assert.Equal("1-3s", result.performanceBucket);
        Assert.Single(result.activities);
        Assert.Equal("log", store.Activity!.id);
    }

    [Fact]
    public async Task LineItemsApplyProviderSuffixEmbeddingAndMillionTokenUnits()
    {
        var xero = new FakeXero();
        xero.Items["input-gpt-4o-azure"] = new XeroItem { Code = "in", AccountCode = "200", UnitPrice = 3m };
        xero.Items["output-gpt-4o-azure"] = new XeroItem { Code = "out", AccountCode = "200", UnitPrice = 5m };
        xero.Items["text-embedding-openai"] = new XeroItem { Code = "emb", AccountCode = "200", UnitPrice = 1m };
        var flows = Create(new FakeInsights(), new FakeStore(), xero);
        var lines = await flows.GenerateLineItemsAsync(new UsageGroup("a", "gpt-4o",
            "azure-openai-chat", "AIMIE", 1_500_000, 250_000, 0, 1_750_000), "base", false, default);
        Assert.Equal(2, lines.Count);
        Assert.Equal(1.5m, lines[0].Quantity);
        Assert.Equal(.25m, lines[1].Quantity);
        Assert.Contains("answer questions", lines[0].Description);

        lines = await flows.GenerateLineItemsAsync(new UsageGroup("a", "text-embedding",
            "openai-chat", "Ingress", 2_000_000, 0, 0, 2_000_000), "base", false, default);
        Assert.Single(lines);
        Assert.Equal("emb", lines[0].ItemCode);
        Assert.Equal(2m, lines[0].Quantity);
        Assert.Contains("onboard documentation", lines[0].Description);
    }

    [Fact]
    public async Task MonthlyRunUsesPreviousMonthAndKeepsFirstUsageSnapshot()
    {
        var store = new FakeStore();
        store.Accounts.Add(new("oid", "email", "a", "case", "base"));
        store.Accounts.Add(new("oid", "email", "a", "case", "base"));
        store.Usage.Add(new UsageDocument { id = "first", accountId = "a",
            usageTs = "2026-09-15", firstSeen = "2026-09-16T00:00:00.0000000+00:00",
            model = "gpt", llmType = "openai-chat",
            origin = "AIMIE", inputTokens = 1_000_000 });
        var xero = new FakeXero();
        xero.Items["base"] = new XeroItem { Code = "base", AccountCode = "200", UnitPrice = 10m };
        xero.Items["input-gpt-openai"] = new XeroItem { Code = "in", AccountCode = "200", UnitPrice = 1m };
        xero.Items["output-gpt-openai"] = new XeroItem { Code = "out", AccountCode = "200", UnitPrice = 1m };
        var flows = Create(new FakeInsights(), store, xero);
        var first = await flows.GenerateMonthlyInvoicesAsync(new DateTimeOffset(2026, 10, 1, 0, 0, 0,
            TimeSpan.Zero), default);
        Assert.Single(first);
        Assert.Equal(3, first[0].LineItemCount);
        Assert.Equal("2026-09-01", xero.Period!.StartIso);
        Assert.Equal("2026-10-01", xero.Period.EndIso);
        Assert.Equal("billed", store.Usage[0].billingStatus);

        store.Usage.Add(new UsageDocument { id = "late", accountId = "a",
            usageTs = "2026-09-20", firstSeen = "2099-09-20T00:00:00.0000000+00:00",
            model = "gpt", llmType = "openai-chat", origin = "AIMIE" });
        await flows.GenerateMonthlyInvoicesAsync(new DateTimeOffset(2026, 10, 2, 0, 0, 0,
            TimeSpan.Zero), default);
        Assert.Equal(1, xero.InvoiceCreates);
        Assert.Null(store.Usage[1].billingStatus);
    }

    [Fact]
    public async Task InvoiceAcceptsNullTokenFieldsInExistingCosmosUsage()
    {
        var usage = JsonConvert.DeserializeObject<UsageDocument>("""
            {"id":"legacy","accountId":"a","usageTs":"2026-09-15",
             "model":"gpt","llmType":"openai-chat","origin":"AIMIE",
             "inputTokens":null,"outputTokens":null,"reasoningTokens":null,
             "totalTokens":null,"isSecondaryLLM":null}
            """)!;
        var store = new FakeStore();
        store.Accounts.Add(new("oid", "email", "a", "case", "base"));
        store.Usage.Add(usage);
        var xero = new FakeXero();
        xero.Items["base"] = new XeroItem { Code = "base", AccountCode = "200" };
        xero.Items["input-gpt-openai"] = new XeroItem { Code = "in", AccountCode = "200" };
        xero.Items["output-gpt-openai"] = new XeroItem { Code = "out", AccountCode = "200" };

        var result = await Create(new FakeInsights(), store, xero).GenerateMonthlyInvoicesAsync(
            new DateTimeOffset(2026, 10, 1, 0, 0, 0, TimeSpan.Zero), default);

        Assert.Single(result);
        Assert.Equal(3, result[0].LineItemCount);
    }

    [Fact]
    public void BillingPeriodSelectsPreviousCompleteUtcMonth()
    {
        var period = BillingPeriod.PreviousCompleteMonth(new DateTimeOffset(2026, 1, 1,
            5, 0, 0, TimeSpan.Zero));
        Assert.Equal("2025-12-01", period.StartIso);
        Assert.Equal("2026-01-01", period.EndIso);
        Assert.Equal("2026-03-01", BillingPeriod.ForMonth("2026-03").StartIso);
        Assert.Throws<ArgumentException>(() => BillingPeriod.ForMonth("March 2026"));
    }

    private static BillingFlows Create(IInsights insights, IBillingStore store, IXero xero) =>
        new(insights, store, xero, NullLogger<BillingFlows>.Instance);

    private static Dictionary<string, object?> Row(params (string Key, object? Value)[] values) =>
        values.ToDictionary(x => x.Key, x => x.Value, StringComparer.OrdinalIgnoreCase);

    private sealed class FakeInsights : IInsights
    {
        public IReadOnlyList<Dictionary<string, object?>> Rows { get; set; } = [];
        public Task<IReadOnlyList<Dictionary<string, object?>>> QueryAsync(string query,
            TimeSpan span, bool ingestion, CancellationToken ct) => Task.FromResult(Rows);
    }

    private sealed class FakeStore : IBillingStore
    {
        public List<UsageDocument> Usage { get; } = [];
        public List<BillableAccount> Accounts { get; } = [];
        public ActivityRecord? Activity { get; private set; }
        private BillingRun? run;
        public Task UpsertActivityAsync(ActivityRecord record, CancellationToken ct)
        { Activity = record; return Task.CompletedTask; }
        public Task UpsertUsageAsync(UsageDocument usage, CancellationToken ct)
        { Usage.Add(usage); return Task.CompletedTask; }
        public Task<IReadOnlyList<UsageDocument>> GetUsageAsync(string accountId,
            BillingPeriod period, string snapshotAt, CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<UsageDocument>>(
            Usage.Where(x => x.accountId == accountId && x.usageTs.CompareTo(period.StartIso) >= 0
                && x.usageTs.CompareTo(period.EndIso) < 0 && x.billingStatus != "billed"
                && (x.firstSeen is null || string.CompareOrdinal(x.firstSeen, snapshotAt) <= 0)).ToList());
        public Task<IReadOnlyList<BillableAccount>> GetBillableAccountsAsync(CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<BillableAccount>>(Accounts);
        public Task<BillingRun> GetOrCreateRunAsync(string accountId, BillingPeriod period,
            CancellationToken ct)
        {
            run ??= new BillingRun { id = accountId + period.StartIso,
                accountId = accountId, snapshotAt = DateTimeOffset.UtcNow.ToString("O") };
            return Task.FromResult(run);
        }
        public Task SetInvoiceAsync(BillingRun billingRun, string invoiceId, int count, CancellationToken ct)
        { billingRun.invoiceId = invoiceId; billingRun.lineItemCount = count; return Task.CompletedTask; }
        public Task MarkBilledAsync(IEnumerable<UsageDocument> usage, string invoiceId, CancellationToken ct)
        { foreach (var x in usage) { x.billingStatus = "billed"; x.invoiceId = invoiceId; }
            return Task.CompletedTask; }
        public Task<int> CleanupBilledAsync(DateTimeOffset before, CancellationToken ct) =>
            Task.FromResult(0);
    }

    private sealed class FakeXero : IXero
    {
        public Dictionary<string, XeroItem> Items { get; } = [];
        public BillingPeriod? Period { get; private set; }
        public int InvoiceCreates { get; private set; }
        public Task<XeroToken> GetTokenAsync(IReadOnlyList<string> scopes, CancellationToken ct) =>
            Task.FromResult(new XeroToken("token", 1800, "Bearer", ""));
        public Task<XeroUser?> GetUserAsync(string oid, CancellationToken ct) =>
            Task.FromResult<XeroUser?>(new XeroUser());
        public Task<string> GetOrCreateContactAsync(string oid, CancellationToken ct) =>
            Task.FromResult("contact");
        public Task<XeroItem> GetItemAsync(string code, CancellationToken ct) =>
            Task.FromResult(Items[code]);
        public Task<string> CreateDraftInvoiceAsync(string contactId, string accountId,
            BillingPeriod period, IReadOnlyList<LineItem> items, CancellationToken ct)
        { Period = period; InvoiceCreates++; return Task.FromResult("invoice"); }
    }
}
