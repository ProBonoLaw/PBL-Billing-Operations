using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace BillingOperations;

public sealed class BillingFlows(IInsights insights, IBillingStore store, IXero xero,
    ILogger<BillingFlows> logger)
{
    public const string StatsQuery = """
        dependencies
        | extend accountId = tostring(customDimensions['operation.account_id']),
                 sessionId = tostring(customDimensions['operation.session_id']),
                 operationId = operation_Id
        | where accountId != '' and sessionId != ''
        | order by timestamp desc
        | project logId = id, timestamp, accountId, sessionId, name, operationId,
                  duration, performanceBucket, success
        """;

    public const string AimieUsageQuery = """
        customEvents
        | where name == "token_usage"
        | extend accountId = tostring(customDimensions["accountId"]),
                 sessionId = tostring(customDimensions["sessionId"]),
                 inputTokens = tolong(customDimensions["inputTokens"]),
                 outputTokens = tolong(customDimensions["outputTokens"]),
                 totalTokens = tolong(customDimensions["totalTokens"]),
                 model = tostring(customDimensions["model"]),
                 llmType = tostring(customDimensions["type"]),
                 reasoningTokens = tolong(customDimensions["reasoningTokens"]),
                 isSecondaryLLM = tostring(customDimensions["isSecondaryLLM"])
        | distinct operationParentId = operation_ParentId, timestamp, name,
                   accountId, sessionId, model, llmType, inputTokens, outputTokens,
                   totalTokens, reasoningTokens, isSecondaryLLM, operationId = operation_Id
        | order by timestamp desc
        """;

    public const string IngestionUsageQuery = """
        customEvents
        | extend accountId = tostring(customDimensions["accountId"]),
                 operationId = operation_Id,
                 model = tostring(customDimensions["model"]),
                 inputTokens = tolong(customMeasurements["inputTokens"]),
                 outputTokens = tolong(customMeasurements["outputTokens"]),
                 reasoningTokens = tolong(customMeasurements["reasoningTokens"]),
                 totalTokens = tolong(customMeasurements["totalTokens"])
        | where accountId != ''
        | distinct operationParentId = operation_ParentId, timestamp, accountId,
                   operationId, name, model, inputTokens, outputTokens,
                   reasoningTokens, totalTokens
        """;

    public async Task<int> RetrieveStatsAsync(CancellationToken ct)
    {
        logger.LogInformation("AIMIE stats import: querying telemetry");
        var rows = await insights.QueryAsync(StatsQuery, TimeSpan.FromHours(1), false, ct);
        logger.LogInformation("AIMIE stats import: received {RowCount} telemetry rows", rows.Count);
        var count = 0;
        await Parallel.ForEachAsync(rows,
            new ParallelOptions { MaxDegreeOfParallelism = 10, CancellationToken = ct },
            async (row, token) =>
        {
            var eventName = Read(row, "name");
            if (eventName is not ("fast-qa" or "declaration")) return;
            var evt = new DependencyEvent(Read(row, "logId"), Read(row, "timestamp"),
                Read(row, "accountId"), Read(row, "sessionId"), eventName,
                Read(row, "operationId"), Number(row, "duration"),
                Read(row, "performanceBucket"), Flag(row, "success"));
            await GenerateActivityRecordAsync(evt, eventName == "declaration", token);
            Interlocked.Increment(ref count);
        });
        logger.LogInformation("Stored {Count} AIMIE activity records", count);
        return count;
    }

    public async Task<ActivityRecord> GenerateActivityRecordAsync(DependencyEvent evt,
        bool declaration, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(evt.LogId) || string.IsNullOrWhiteSpace(evt.AccountId))
            throw new ArgumentException("logId and accountId are required");
        using var activityScope = logger.BeginScope(new Dictionary<string, object?>
        {
            ["AccountRef"] = AccountRef(evt.AccountId),
            ["ActivityKind"] = declaration ? "declaration" : "qa"
        });
        logger.LogInformation("Activity record: querying related telemetry");
        var query = declaration
            ? $"requests | where operation_Id == '{Kql(evt.OperationId)}' | order by timestamp desc | project id, timestamp, name, duration, performanceBucket, success"
            : $"dependencies | where operation_ParentId == '{Kql(evt.LogId)}' and name has 'qa' | order by timestamp desc | project id, timestamp, name, duration, performanceBucket, success";
        var rows = await insights.QueryAsync(query, TimeSpan.FromDays(declaration ? 31 : 28), false, ct);
        logger.LogInformation("Activity record: found {RelatedActivityCount} related events", rows.Count);
        var record = new ActivityRecord
        {
            id = evt.LogId, eventTs = evt.Timestamp, accountId = evt.AccountId,
            sessionId = evt.SessionId, name = evt.Name, operationId = evt.OperationId,
            duration = evt.Duration, performanceBucket = evt.PerformanceBucket, success = evt.Success,
            activities = rows.Select(row => new ActivityDocument
            {
                id = Read(row, "id"), activityTs = Read(row, "timestamp"),
                name = Read(row, "name"), duration = Number(row, "duration"),
                performanceBucket = Read(row, "performanceBucket"), success = Flag(row, "success")
            }).ToList()
        };
        if (declaration)
        {
            var orchestration = record.activities.FirstOrDefault(x =>
                x.name == "orchestration:DeclarationOrchestration");
            if (orchestration is not null)
            {
                record.duration += orchestration.duration;
                record.performanceBucket = orchestration.performanceBucket;
                record.success = orchestration.success;
            }
        }
        logger.LogInformation("Activity record: writing Cosmos document");
        await store.UpsertActivityAsync(record, ct);
        logger.LogInformation("Activity record: Cosmos document written");
        return record;
    }

    public async Task<int> RetrieveAimieUsageAsync(CancellationToken ct) =>
        await RetrieveUsageAsync(AimieUsageQuery, false, ct);

    public async Task<int> RetrieveIngestionUsageAsync(CancellationToken ct) =>
        await RetrieveUsageAsync(IngestionUsageQuery, true, ct);

    private async Task<int> RetrieveUsageAsync(string query, bool ingestion, CancellationToken ct)
    {
        var origin = ingestion ? "ingestion" : "AIMIE";
        logger.LogInformation("{Origin} usage import: querying telemetry", origin);
        var rows = await insights.QueryAsync(query, TimeSpan.FromHours(1), ingestion, ct);
        logger.LogInformation("{Origin} usage import: received {RowCount} telemetry rows",
            origin, rows.Count);
        var count = 0;
        await Parallel.ForEachAsync(rows,
            new ParallelOptions { MaxDegreeOfParallelism = ingestion ? 25 : 10,
                CancellationToken = ct },
            async (row, token) =>
        {
            var accountId = Read(row, "accountId");
            if (string.IsNullOrWhiteSpace(accountId)) return;
            var timestamp = Read(row, "timestamp");
            var operationId = Read(row, "operationId");
            var model = Read(row, "model");
            var sessionId = ingestion ? operationId : Read(row, "sessionId");
            var usage = new UsageDocument
            {
                id = ingestion ? $"{timestamp}_{operationId}_{model}" :
                    $"{timestamp}_{operationId}_{model}{sessionId}",
                accountId = accountId, usageTs = timestamp, origin = ingestion ? "Ingress" : "AIMIE",
                model = model, llmType = ingestion ? "azure-openai-chat" : Read(row, "llmType"),
                sessionId = sessionId, operationId = operationId,
                operationParentId = Read(row, "operationParentId"),
                inputTokens = Integer(row, "inputTokens"), outputTokens = Integer(row, "outputTokens"),
                totalTokens = Integer(row, "totalTokens"), reasoningTokens = Integer(row, "reasoningTokens"),
                isSecondaryLLM = !ingestion && Flag(row, "isSecondaryLLM")
            };
            await store.UpsertUsageAsync(usage, token);
            Interlocked.Increment(ref count);
        });
        logger.LogInformation("{Origin} usage import: wrote {Count} Cosmos records", origin, count);
        return count;
    }

    public async Task<IReadOnlyList<LineItem>> GenerateLineItemsAsync(UsageGroup group,
        string billingCode, bool billingCodeOnly, CancellationToken ct)
    {
        if (billingCodeOnly)
        {
            logger.LogInformation("Line items: resolving base Xero item {ItemCode}", billingCode);
            var baseItem = await xero.GetItemAsync(billingCode, ct);
            logger.LogInformation("Line items: base Xero item resolved");
            return [new(baseItem.Code, baseItem.AccountCode, baseItem.UnitPrice, 1m,
                baseItem.Description)];
        }
        var suffix = group.LlmType switch
        {
            "azure-openai-chat" or "azure-anthropic-chat" => "-azure",
            "openai-chat" => "-openai", _ => ""
        };
        var productCode = group.Model + suffix;
        var purpose = group.Origin == "AIMIE"
            ? "answer questions and/or generate declarations."
            : "onboard documentation.";
        logger.LogInformation("Line items: resolving usage Xero items for {ProductCode}", productCode);
        if (productCode.Contains("embedding", StringComparison.OrdinalIgnoreCase))
        {
            var item = await xero.GetItemAsync(productCode, ct);
            logger.LogInformation("Line items: embedding item resolved");
            return [new(item.Code, item.AccountCode, item.UnitPrice,
                group.InputTokens / 1_000_000m, $"The embedding tokens used to {purpose}")];
        }
        var input = await xero.GetItemAsync("input-" + productCode, ct);
        var output = await xero.GetItemAsync("output-" + productCode, ct);
        logger.LogInformation("Line items: input and output items resolved");
        return
        [
            new(input.Code, input.AccountCode, input.UnitPrice,
                group.InputTokens / 1_000_000m, $"The data input tokens used to {purpose}"),
            new(output.Code, output.AccountCode, output.UnitPrice,
                group.OutputTokens / 1_000_000m, $"The data output tokens used to {purpose}")
        ];
    }

    public async Task<InvoiceResult> GenerateInvoiceAsync(BillableAccount account,
        BillingPeriod period, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(account.Oid) || string.IsNullOrWhiteSpace(account.Email) ||
            string.IsNullOrWhiteSpace(account.CaseAccountId) ||
            string.IsNullOrWhiteSpace(account.CaseNumber) ||
            string.IsNullOrWhiteSpace(account.BillingCode))
            throw new ArgumentException("All account billing fields are required");
        using var invoiceScope = logger.BeginScope(new Dictionary<string, object?>
        {
            ["AccountRef"] = AccountRef(account.CaseAccountId),
            ["BillingMonth"] = period.Start.ToString("yyyy-MM", CultureInfo.InvariantCulture)
        });
        logger.LogInformation("Invoice: loading or creating billing run");
        var run = await store.GetOrCreateRunAsync(account.CaseAccountId, period, ct);
        logger.LogInformation("Invoice: loading unbilled usage snapshot");
        var usage = await store.GetUsageAsync(account.CaseAccountId, period, run.snapshotAt, ct);
        logger.LogInformation("Invoice: loaded {UsageCount} usage records", usage.Count);
        if (run.invoiceId is not null)
        {
            logger.LogInformation("Invoice: existing invoice found; marking remaining usage billed");
            await store.MarkBilledAsync(usage, run.invoiceId, ct);
            logger.LogInformation("Invoice: existing run completed");
            return new InvoiceResult(account.CaseAccountId, run.invoiceId, run.lineItemCount);
        }
        logger.LogInformation("Invoice: resolving Xero contact");
        var contactId = await xero.GetOrCreateContactAsync(account.Oid, ct);
        logger.LogInformation("Invoice: Xero contact resolved");
        var lineItems = new List<LineItem>();
        lineItems.AddRange(await GenerateLineItemsAsync(new UsageGroup(account.CaseAccountId,
            "", "", "", 0, 0, 0, 0), account.BillingCode, true, ct));
        var usageGroups = usage.GroupBy(x => (x.model, x.llmType, x.origin)).ToArray();
        logger.LogInformation("Invoice: building line items for {UsageGroupCount} usage groups",
            usageGroups.Length);
        foreach (var group in usageGroups)
        {
            var total = new UsageGroup(account.CaseAccountId, group.Key.model,
                group.Key.llmType, group.Key.origin, group.Sum(x => x.inputTokens ?? 0),
                group.Sum(x => x.outputTokens ?? 0), group.Sum(x => x.reasoningTokens ?? 0),
                group.Sum(x => x.totalTokens ?? 0));
            lineItems.AddRange(await GenerateLineItemsAsync(total, account.BillingCode, false, ct));
        }
        logger.LogInformation("Invoice: ensuring Xero draft with {LineItemCount} lines", lineItems.Count);
        var invoiceId = await xero.CreateDraftInvoiceAsync(contactId, account.CaseAccountId,
            period, lineItems, ct);
        logger.LogInformation("Invoice: Xero draft ensured; recording billing run");
        await store.SetInvoiceAsync(run, invoiceId, lineItems.Count, ct);
        logger.LogInformation("Invoice: marking {UsageCount} usage records billed", usage.Count);
        await store.MarkBilledAsync(usage, invoiceId, ct);
        logger.LogInformation("Invoice: completed with {LineItemCount} lines", lineItems.Count);
        return new InvoiceResult(account.CaseAccountId, invoiceId, lineItems.Count);
    }

    public async Task<IReadOnlyList<InvoiceResult>> GenerateMonthlyInvoicesAsync(
        DateTimeOffset now, CancellationToken ct)
    {
        var period = BillingPeriod.PreviousCompleteMonth(now);
        logger.LogInformation("Monthly invoices: loading billable accounts for {BillingMonth}",
            period.Start.ToString("yyyy-MM", CultureInfo.InvariantCulture));
        var accounts = await store.GetBillableAccountsAsync(ct);
        var uniqueAccounts = accounts.GroupBy(x => x.CaseAccountId).Select(x => x.First()).ToArray();
        logger.LogInformation("Monthly invoices: found {AccountRows} rows and {AccountCount} case accounts",
            accounts.Count, uniqueAccounts.Length);
        var results = new List<InvoiceResult>();
        var errors = new List<Exception>();
        foreach (var account in uniqueAccounts)
        {
            try { results.Add(await GenerateInvoiceAsync(account, period, ct)); }
            catch (Exception e) when (e is not OperationCanceledException)
            {
                logger.LogError(e, "Monthly invoices: account {AccountRef} failed",
                    AccountRef(account.CaseAccountId));
                errors.Add(e);
            }
        }
        logger.LogInformation("Monthly invoices: {CompletedCount} completed, {FailedCount} failed",
            results.Count, errors.Count);
        if (errors.Count > 0) throw new AggregateException(
            $"Invoice generation failed for {errors.Count} account(s)", errors);
        return results;
    }

    public async Task<int> CleanupUsageAsync(DateTimeOffset now, CancellationToken ct)
    {
        var cutoff = new DateTimeOffset(now.Year, now.Month, 1, 0, 0, 0, TimeSpan.Zero);
        logger.LogInformation("Usage cleanup: deleting billed records before {Cutoff}",
            cutoff.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));
        var count = await store.CleanupBilledAsync(cutoff, ct);
        logger.LogInformation("Usage cleanup: deleted {RecordCount} records", count);
        return count;
    }

    private static string AccountRef(string? accountId) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(accountId ?? "")))[..12];

    private static string Kql(string value) => value.Replace("'", "''");
    private static string Read(Dictionary<string, object?> row, string key) =>
        row.TryGetValue(key, out var value) ? Convert.ToString(value, CultureInfo.InvariantCulture) ?? "" : "";
    private static long Integer(Dictionary<string, object?> row, string key) =>
        long.TryParse(Read(row, key), NumberStyles.Any, CultureInfo.InvariantCulture, out var value)
            ? value : (long)Number(row, key);
    private static double Number(Dictionary<string, object?> row, string key)
    {
        var text = Read(row, key);
        if (double.TryParse(text, NumberStyles.Any, CultureInfo.InvariantCulture, out var value))
            return value;
        return TimeSpan.TryParse(text, CultureInfo.InvariantCulture, out var duration)
            ? duration.TotalMilliseconds : 0;
    }
    private static bool Flag(Dictionary<string, object?> row, string key) =>
        bool.TryParse(Read(row, key), out var value) && value;
}
