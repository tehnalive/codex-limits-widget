using LimitsWidget.Core;
using LimitsWidget.Adapters;

if (await FakeServer.RunIfRequested(args)) return;

static void Check(bool condition, string description)
{
    if (!condition) throw new Exception(description);
}
var window = new QuotaWindow(81, 300, DateTimeOffset.UtcNow.AddHours(1).ToUnixTimeSeconds());
var codex = new QuotaBucket("codex", null, window, null);
var other = new QuotaBucket("other", null, null, null);
Check(window.Remaining == 19, "Use remaining rather than used percent");
Check(new QuotaWindow(110, null, null).Remaining == 0, "Clamp excess usage");
Check(new QuotaWindow(-10, null, null).Remaining == 100, "Clamp negative usage");
Check(QuotaRules.Select([other, codex], null) == codex, "Prefer codex by default");
Check(QuotaRules.Select([other, codex], "removed") is null, "Do not silently replace a removed selected quota");
Check(QuotaRules.Select([other, new("third", null, null, null)], null) is null, "Require explicit choice for ambiguous buckets");
var snapshot = new QuotaSnapshot("hashed-account", DateTimeOffset.UtcNow, [codex]);
var key = QuotaRules.NotificationKey(snapshot, codex, window, "primary");
Check(key is not null, "Notify below 20%");
Check(QuotaRules.NotificationKey(snapshot, codex, window with { UsedPercent = 80 }, "primary") is null, "Do not notify at exactly 20%");
Check(QuotaRules.NotificationKey(snapshot, codex, window with { ResetsAt = null }, "primary") is null, "No notification for unverified window");
Check(QuotaRules.NotificationKey(snapshot, codex, window with { ResetsAt = 1 }, "primary") is null, "Do not notify for elapsed window");
Check(QuotaRules.NotificationKey(snapshot with { AccountNamespace = "different" }, codex, window, "primary") != key, "Isolate account notification state");
Check(QuotaRules.NotificationKey(snapshot, codex, window with { ResetsAt = long.MaxValue }, "primary") is null, "Reject invalid server timestamp");
Check(QuotaRules.NotificationKey(snapshot, codex, window, "primary", snapshot.CapturedAt.AddMinutes(3)) is null, "No notifications from stale snapshots");
Check(QuotaRules.NotificationKey(snapshot with { AccountNamespace = "unknown-session" }, codex, window, "primary") is null, "Do not notify without stable account identity");
var notified = new Dictionary<string, long>();
Check(QuotaRules.ClaimNotification(snapshot, codex, window, "primary", notified), "First low-quota alert is claimed");
Check(!QuotaRules.ClaimNotification(snapshot, codex, window with { ResetsAt = window.ResetsAt + 1 }, "primary", notified), "One second reset jitter must not repeat the warning");
var restored = System.Text.Json.JsonSerializer.Deserialize<Dictionary<string, long>>(System.Text.Json.JsonSerializer.Serialize(notified))!;
Check(!QuotaRules.ClaimNotification(snapshot, codex, window with { ResetsAt = window.ResetsAt - 1 }, "primary", restored), "Restart and negative reset jitter must not repeat the warning");
var nextCycle = snapshot with { CapturedAt = snapshot.CapturedAt.AddHours(2) };
Check(QuotaRules.ClaimNotification(nextCycle, codex, window with { ResetsAt = window.ResetsAt + 18000 }, "primary", restored, nextCycle.CapturedAt), "A genuine new window allows one warning again");
Console.WriteLine("PASS: quota selection, remaining %, notification windows.");

var fakeExecutable = Environment.ProcessPath ?? throw new Exception("Test executable unavailable");
foreach (var scenario in new[] { "quota", "empty-map", "invalid-values", "legacy" })
{
    Environment.SetEnvironmentVariable("WIDGET_TEST_SCENARIO", scenario);
    await using var adapter = new CodexSource(fakeExecutable);
    var actual = await adapter.ReadQuotaAsync(CancellationToken.None);
    Check(!actual.AccountNamespace.Contains("example.test"), "Identity is hashed");
    if (scenario == "empty-map") Check(actual.Buckets.Count == 0, "Empty map does not fall back to legacy");
    else if (scenario == "invalid-values") Check(actual.Buckets[0].Primary is null, "Missing usedPercent is unknown, not zero");
    else Check(actual.Buckets[0].Primary?.Remaining == 19, "Actual RPC maps usage to remaining");
    Check((await adapter.ReadQuotaAsync(CancellationToken.None)).Buckets.Count == actual.Buckets.Count, "Repeated quota reads reconnect safely");
}
Environment.SetEnvironmentVariable("WIDGET_TEST_SCENARIO", "hang");
await using (var adapter = new CodexSource(fakeExecutable))
{
    using var cancellation = new CancellationTokenSource(200);
    try { await adapter.ReadQuotaAsync(cancellation.Token); throw new Exception("Cancellation was ignored"); }
    catch (OperationCanceledException) { }
}
Environment.SetEnvironmentVariable("WIDGET_TEST_SCENARIO", null);
Console.WriteLine("PASS: real stdio transport with fake server, nullable quotas, cancellation and cleanup.");
