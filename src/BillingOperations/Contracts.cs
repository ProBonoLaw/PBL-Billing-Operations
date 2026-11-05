namespace BillingOperations;

public interface IInsights
{
    Task<IReadOnlyList<Dictionary<string, object?>>> QueryAsync(string query, TimeSpan span,
        bool ingestion, CancellationToken ct);
}

public interface IBillingStore
{
    Task UpsertActivityAsync(ActivityRecord record, CancellationToken ct);
    Task UpsertUsageAsync(UsageDocument usage, CancellationToken ct);
    Task<IReadOnlyList<UsageDocument>> GetUsageAsync(string accountId, BillingPeriod period,
        string snapshotAt,
        CancellationToken ct);
    Task<IReadOnlyList<BillableAccount>> GetBillableAccountsAsync(CancellationToken ct);
    Task<BillingRun> GetOrCreateRunAsync(string accountId, BillingPeriod period,
        CancellationToken ct);
    Task SetInvoiceAsync(BillingRun run, string invoiceId, int lineItemCount, CancellationToken ct);
    Task MarkBilledAsync(IEnumerable<UsageDocument> usage, string invoiceId, CancellationToken ct);
    Task<int> CleanupBilledAsync(DateTimeOffset before, CancellationToken ct);
}

public interface IXero
{
    Task<XeroToken> GetTokenAsync(IReadOnlyList<string> scopes, CancellationToken ct);
    Task<XeroUser?> GetUserAsync(string oid, CancellationToken ct);
    Task<string> GetOrCreateContactAsync(string oid, CancellationToken ct);
    Task<XeroItem> GetItemAsync(string code, CancellationToken ct);
    Task<string> CreateDraftInvoiceAsync(string contactId, string accountId,
        BillingPeriod period, IReadOnlyList<LineItem> items, CancellationToken ct);
}
