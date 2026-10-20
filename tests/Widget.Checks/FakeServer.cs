using System.Text.Json;

internal static class FakeServer
{
    public static async Task<bool> RunIfRequested(string[] args)
    {
        if (!args.Contains("app-server")) return false;
        var scenario = Environment.GetEnvironmentVariable("WIDGET_TEST_SCENARIO");
        while (await Console.In.ReadLineAsync() is string line)
        {
            using var message = JsonDocument.Parse(line);
            var root = message.RootElement;
            if (!root.TryGetProperty("id", out var idElement)) continue;
            int id = idElement.GetInt32();
            var method = root.GetProperty("method").GetString();
            if (scenario == "hang") { await Task.Delay(TimeSpan.FromMinutes(1)); continue; }
            object? result = method switch
            {
                "initialize" => new { userAgent = "fixture" },
                "account/read" => new { account = new { type = "chatgpt", email = "synthetic@example.test" } },
                "account/rateLimits/read" => Quota(scenario),
                _ => null
            };
            if (result is null) Console.WriteLine(JsonSerializer.Serialize(new { id, error = new { code = -32601, message = "Synthetic method unavailable" } }));
            else Console.WriteLine(JsonSerializer.Serialize(new { id, result }));
        }
        return true;
    }

    private static object Quota(string? scenario)
    {
        object bucket = scenario == "invalid-values"
            ? new { limitId = "codex", primary = new { usedPercent = (double?)null, windowDurationMins = 300, resetsAt = long.MaxValue } }
            : new { limitId = "codex", primary = new { usedPercent = 81, windowDurationMins = 300, resetsAt = DateTimeOffset.UtcNow.AddHours(1).ToUnixTimeSeconds() } };
        if (scenario == "legacy") return new { accountId = "synthetic-account", rateLimits = bucket };
        var map = new Dictionary<string, object>();
        if (scenario != "empty-map") map.Add("codex", bucket);
        return new { accountId = "synthetic-account", rateLimits = bucket, rateLimitsByLimitId = map };
    }
}
