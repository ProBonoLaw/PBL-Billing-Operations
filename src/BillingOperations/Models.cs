namespace BillingOperations;

public sealed record DependencyEvent(
    string LogId, string Timestamp, string AccountId, string SessionId,
    string Name, string OperationId, double Duration, string PerformanceBucket, bool Success);

public sealed class ActivityRecord
{
    public string id { get; set; } = "";
    public string accountId { get; set; } = "";
    public string eventTs { get; set; } = "";
    public string sessionId { get; set; } = "";
    public string name { get; set; } = "";
    public string operationId { get; set; } = "";
    public double duration { get; set; }
    public string performanceBucket { get; set; } = "";
    public bool success { get; set; }
    public List<ActivityDocument> activities { get; set; } = [];
}

public sealed class ActivityDocument
{
    public string id { get; set; } = "";
    public string activityTs { get; set; } = "";
    public string name { get; set; } = "";
    public double duration { get; set; }
    public string performanceBucket { get; set; } = "";
    public bool success { get; set; }
}

public sealed class UsageDocument
{
    public string id { get; set; } = "";
    public string accountId { get; set; } = "";
    public string sessionId { get; set; } = "";
    public string usageTs { get; set; } = "";
    public string? firstSeen { get; set; }
    public string origin { get; set; } = "";
    public string model { get; set; } = "";
    public string llmType { get; set; } = "";
    public long? inputTokens { get; set; }
    public long? outputTokens { get; set; }
    public long? totalTokens { get; set; }
    public long? reasoningTokens { get; set; }
    public bool? isSecondaryLLM { get; set; }
    public string operationId { get; set; } = "";
    public string operationParentId { get; set; } = "";
    public string? billingStatus { get; set; }
    public string? invoiceId { get; set; }
}

public sealed record UsageGroup(string AccountId, string Model, string LlmType, string Origin,
    long InputTokens, long OutputTokens, long ReasoningTokens, long TotalTokens);

public sealed record BillableAccount(string Oid, string Email, string CaseAccountId,
    string CaseNumber, string BillingCode);

public sealed record LineItem(string ItemCode, string AccountCode, decimal UnitPrice,
    decimal Quantity, string Description);

public sealed record BillingPeriod(DateTimeOffset Start, DateTimeOffset End)
{
    public static BillingPeriod CurrentMonth(DateTimeOffset now)
    {
        var start = new DateTimeOffset(now.Year, now.Month, 1, 0, 0, 0, TimeSpan.Zero);
        return new(start, start.AddMonths(1));
    }
    public static BillingPeriod PreviousCompleteMonth(DateTimeOffset now)
    {
        var end = CurrentMonth(now).Start;
        return new(end.AddMonths(-1), end);
    }
    public static BillingPeriod ForMonth(string value)
    {
        if (!DateTime.TryParseExact(value, "yyyy-MM", System.Globalization.CultureInfo.InvariantCulture,
            System.Globalization.DateTimeStyles.None, out var month))
            throw new ArgumentException("billingMonth must be yyyy-MM");
        return CurrentMonth(new DateTimeOffset(month.Year, month.Month, 1, 0, 0, 0,
            TimeSpan.Zero));
    }
    public string StartIso => Start.ToString("yyyy-MM-dd");
    public string EndIso => End.ToString("yyyy-MM-dd");
}

public sealed record InvoiceResult(string AccountId, string InvoiceId, int LineItemCount);
public sealed record XeroToken(string access_token, int expires_in, string token_type, string scope);

public sealed class BillingRun
{
    public string id { get; set; } = "";
    public string accountId { get; set; } = "";
    public string snapshotAt { get; set; } = "";
    public string? invoiceId { get; set; }
    public int lineItemCount { get; set; }
}

public sealed class XeroItem
{
    public string Code { get; set; } = "";
    public string Description { get; set; } = "";
    public string AccountCode { get; set; } = "";
    public decimal UnitPrice { get; set; }
}

public sealed class XeroUser
{
    public string DisplayName { get; set; } = "";
    public string GivenName { get; set; } = "";
    public string Surname { get; set; } = "";
    public string Mail { get; set; } = "";
    public string StreetAddress { get; set; } = "";
    public string City { get; set; } = "";
    public string State { get; set; } = "";
    public string PostalCode { get; set; } = "";
    public string Country { get; set; } = "";
}
