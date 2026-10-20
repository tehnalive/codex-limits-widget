namespace LimitsWidget.Core;

public sealed record QuotaWindow(double UsedPercent, int? DurationMinutes, long? ResetsAt)
{
    public double Remaining => Math.Clamp(100 - UsedPercent, 0, 100);
}
public sealed record QuotaBucket(string Id, string? Name, QuotaWindow? Primary, QuotaWindow? Secondary);
public sealed record QuotaSnapshot(string AccountNamespace, DateTimeOffset CapturedAt, IReadOnlyList<QuotaBucket> Buckets);
public sealed class SourceUnavailableException(string message) : Exception(message);
public interface ICodexSource : IAsyncDisposable
{
    Task<QuotaSnapshot> ReadQuotaAsync(CancellationToken cancellationToken);
}
