using Azure.Identity;
using Microsoft.Azure.Cosmos;
using System.Net;

namespace BillingOperations;

public sealed class CosmosBillingStore : IBillingStore
{
    private readonly Container usage;
    private readonly Container stats;
    private readonly Container accounts;
    private readonly Container runs;

    public CosmosBillingStore(IConfiguration config)
    {
        var options = new CosmosClientOptions { AllowBulkExecution = true };
        var client = !string.IsNullOrWhiteSpace(config["COSMOS_CONNECTION_STRING"])
            ? new CosmosClient(config.Required("COSMOS_CONNECTION_STRING"), options)
            : new CosmosClient(config.Required("COSMOS_ENDPOINT"), new DefaultAzureCredential(), options);
        var db = client.GetDatabase(config.Required("COSMOS_DATABASE"));
        usage = db.GetContainer(config["COSMOS_USAGE_CONTAINER"] ?? "accountUsage");
        stats = db.GetContainer(config["COSMOS_STATS_CONTAINER"] ?? "aimieStats");
        accounts = db.GetContainer(config["COSMOS_ACCOUNTS_CONTAINER"] ?? "accounts");
        runs = db.GetContainer(config["COSMOS_RUNS_CONTAINER"] ?? "billingRuns");
    }

    public async Task UpsertActivityAsync(ActivityRecord record, CancellationToken ct)
    {
        var partitionKey = new PartitionKeyBuilder().Add(record.accountId).Add(record.name).Build();
        await stats.UpsertItemAsync(record, partitionKey, cancellationToken: ct);
    }

    public async Task UpsertUsageAsync(UsageDocument record, CancellationToken ct)
    {
        record.firstSeen = DateTimeOffset.UtcNow.ToString("O");
        try
        {
            await usage.CreateItemAsync(record, new PartitionKey(record.accountId), cancellationToken: ct);
        }
        catch (CosmosException e) when (e.StatusCode == HttpStatusCode.Conflict)
        {
            await usage.PatchItemAsync<UsageDocument>(record.id, new PartitionKey(record.accountId),
                [PatchOperation.Set("/sessionId", record.sessionId),
                 PatchOperation.Set("/usageTs", record.usageTs),
                 PatchOperation.Set("/origin", record.origin),
                 PatchOperation.Set("/model", record.model),
                 PatchOperation.Set("/llmType", record.llmType),
                 PatchOperation.Set("/inputTokens", record.inputTokens),
                 PatchOperation.Set("/outputTokens", record.outputTokens),
                 PatchOperation.Set("/totalTokens", record.totalTokens),
                 PatchOperation.Set("/reasoningTokens", record.reasoningTokens),
                 PatchOperation.Set("/isSecondaryLLM", record.isSecondaryLLM),
                 PatchOperation.Set("/operationId", record.operationId),
                 PatchOperation.Set("/operationParentId", record.operationParentId)],
                cancellationToken: ct);
        }
    }

    public Task<IReadOnlyList<UsageDocument>> GetUsageAsync(string accountId, BillingPeriod period,
        string snapshotAt, CancellationToken ct) => QueryAsync<UsageDocument>(usage,
            new QueryDefinition("SELECT * FROM c WHERE c.accountId = @account AND c.usageTs >= @start AND c.usageTs < @end AND (NOT IS_DEFINED(c.billingStatus) OR IS_NULL(c.billingStatus) OR c.billingStatus != 'billed') AND (NOT IS_DEFINED(c.firstSeen) OR IS_NULL(c.firstSeen) OR c.firstSeen <= @snapshot)")
                .WithParameter("@account", accountId).WithParameter("@start", period.StartIso)
                .WithParameter("@end", period.EndIso).WithParameter("@snapshot", snapshotAt), ct,
            new QueryRequestOptions { PartitionKey = new PartitionKey(accountId) });

    public Task<IReadOnlyList<BillableAccount>> GetBillableAccountsAsync(CancellationToken ct) =>
        QueryAsync<BillableAccount>(accounts, new QueryDefinition("""
            SELECT oid AS Oid, email AS Email, cases.accountId AS CaseAccountId,
                   cases.caseNumber AS CaseNumber, cases.billingCode AS BillingCode
            FROM c JOIN oid IN c.oids JOIN email IN c.emailAddresses
                   JOIN cases IN c.casesAccounts
            WHERE c.accountId = '00000000-0000-0000-0000-000000000000'
              AND IS_DEFINED(cases.billingCode)
            """), ct);

    public async Task<BillingRun> GetOrCreateRunAsync(string accountId,
        BillingPeriod period, CancellationToken ct)
    {
        var id = $"{accountId}_{period.Start:yyyy-MM}";
        var key = new PartitionKey(accountId);
        try { return (await runs.ReadItemAsync<BillingRun>(id, key, cancellationToken: ct)).Resource; }
        catch (CosmosException e) when (e.StatusCode == HttpStatusCode.NotFound) { }
        var run = new BillingRun
        {
            id = id, accountId = accountId, snapshotAt = DateTimeOffset.UtcNow.ToString("O")
        };
        try { return (await runs.CreateItemAsync(run, key, cancellationToken: ct)).Resource; }
        catch (CosmosException e) when (e.StatusCode == HttpStatusCode.Conflict)
        {
            return (await runs.ReadItemAsync<BillingRun>(id, key, cancellationToken: ct)).Resource;
        }
    }

    public async Task SetInvoiceAsync(BillingRun run, string invoiceId, int lineItemCount,
        CancellationToken ct)
    {
        await runs.PatchItemAsync<BillingRun>(run.id, new PartitionKey(run.accountId),
            [PatchOperation.Set("/invoiceId", invoiceId),
             PatchOperation.Set("/lineItemCount", lineItemCount)], cancellationToken: ct);
    }

    public async Task MarkBilledAsync(IEnumerable<UsageDocument> records, string invoiceId,
        CancellationToken ct)
    {
        foreach (var record in records)
        {
            await usage.PatchItemAsync<UsageDocument>(record.id, new PartitionKey(record.accountId),
                [PatchOperation.Set("/billingStatus", "billed"),
                 PatchOperation.Set("/invoiceId", invoiceId)], cancellationToken: ct);
        }
    }

    public async Task<int> CleanupBilledAsync(DateTimeOffset before, CancellationToken ct)
    {
        var cutoff = before.ToString("yyyy-MM-dd");
        var count = 0;
        // Drain every page. The Logic App's TOP 1000 left old records behind.
        while (true)
        {
            var batch = await QueryAsync<UsageDocument>(usage,
                new QueryDefinition("SELECT TOP 100 c.id, c.accountId FROM c WHERE c.billingStatus = 'billed' AND IS_DEFINED(c.invoiceId) AND c.usageTs < @cutoff")
                    .WithParameter("@cutoff", cutoff), ct);
            if (batch.Count == 0) return count;
            await Parallel.ForEachAsync(batch,
                new ParallelOptions { MaxDegreeOfParallelism = 5, CancellationToken = ct },
                async (item, token) =>
            {
                try
                {
                    await usage.DeleteItemAsync<UsageDocument>(item.id, new PartitionKey(item.accountId),
                        cancellationToken: token);
                    Interlocked.Increment(ref count);
                }
                catch (CosmosException e) when (e.StatusCode == HttpStatusCode.NotFound) { }
            });
        }
    }

    private static async Task<IReadOnlyList<T>> QueryAsync<T>(Container container,
        QueryDefinition query, CancellationToken ct, QueryRequestOptions? options = null)
    {
        var result = new List<T>();
        using var iterator = container.GetItemQueryIterator<T>(query, requestOptions: options);
        while (iterator.HasMoreResults)
            result.AddRange(await iterator.ReadNextAsync(ct));
        return result;
    }
}
