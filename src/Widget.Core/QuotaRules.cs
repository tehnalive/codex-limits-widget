namespace LimitsWidget.Core;

public static class QuotaRules
{
    public static QuotaBucket? Select(IReadOnlyList<QuotaBucket> buckets, string? selectedId)
    {
        if (selectedId is not null) return buckets.FirstOrDefault(bucket => bucket.Id == selectedId);
        return buckets.FirstOrDefault(bucket => bucket.Id == "codex") ?? (buckets.Count == 1 ? buckets[0] : null);
    }

    public static string? NotificationKey(QuotaSnapshot snapshot, QuotaBucket bucket, QuotaWindow? window, string slot, DateTimeOffset? currentTime = null)
    {
        var now = currentTime ?? DateTimeOffset.UtcNow;
        if (snapshot.AccountNamespace.StartsWith("unknown-", StringComparison.Ordinal)
            || window is null || !double.IsFinite(window.UsedPercent) || window.Remaining >= 20 || window.ResetsAt is null
            || window.ResetsAt is < -62135596800 or > 253402300799
            || snapshot.CapturedAt > now.AddSeconds(5) || now - snapshot.CapturedAt > TimeSpan.FromMinutes(2)
            || DateTimeOffset.FromUnixTimeSeconds(window.ResetsAt.Value) <= now)
            return null;
        return $"{snapshot.AccountNamespace}|{bucket.Id}|{slot}|{window.ResetsAt}";
    }

    public static bool ClaimNotification(QuotaSnapshot snapshot, QuotaBucket bucket, QuotaWindow? window, string slot,
        IDictionary<string, long> notifiedWindows, DateTimeOffset? currentTime = null)
    {
        var now = currentTime ?? DateTimeOffset.UtcNow;
        if (NotificationKey(snapshot, bucket, window, slot, now) is null) return false;
        string identity = $"{snapshot.AccountNamespace}|{bucket.Id}|{slot}";
        long reset = window!.ResetsAt!.Value;
        if (notifiedWindows.TryGetValue(identity, out long previous)
            && (now.ToUnixTimeSeconds() <= previous || reset <= previous || reset - previous <= 120))
            return false;
        notifiedWindows[identity] = reset;
        return true;
    }
}
