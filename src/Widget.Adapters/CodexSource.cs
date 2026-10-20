using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using LimitsWidget.Core;

namespace LimitsWidget.Adapters;

/// <summary>Read-only, serialized stdio client. Never reads auth files or rollouts.</summary>
public sealed class CodexSource(string? executablePath = null) : ICodexSource
{
    private readonly SemaphoreSlim gate = new(1, 1);
    private Process? process;
    private Task? stderrDrain;
    private bool disposed;
    private int requestId;
    private string accountNamespace = "unknown-" + Guid.NewGuid().ToString("N");
    private static readonly HashSet<string> Methods = ["initialize", "account/read", "account/rateLimits/read"];

    public Task<QuotaSnapshot> ReadQuotaAsync(CancellationToken cancellationToken) => ExecuteAsync(async ct =>
    {
        var account = await RpcAsync("account/read", new { refreshToken = false }, ct);
        var accountKind = S(P(account, "account"), "type");
        if (accountKind != "chatgpt") throw new SourceUnavailableException("Войдите в Codex с подпиской ChatGPT.");
        var quota = await RpcAsync("account/rateLimits/read", new { excludeResetCreditDetails = true }, ct);
        // Hash identity in memory; never persist or display the backend ID or email.
        var discriminator = S(quota, "accountId") ?? S(P(account, "account"), "email");
        accountNamespace = discriminator is null ? "unknown-" + Guid.NewGuid().ToString("N") : "codex-" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes("limits-widget:v1:" + discriminator)));
        List<QuotaBucket> buckets = [];
        var multi = P(quota, "rateLimitsByLimitId");
        if (multi.ValueKind == JsonValueKind.Object)
            foreach (var item in multi.EnumerateObject()) buckets.Add(Bucket(item.Name, item.Value));
        if (multi.ValueKind != JsonValueKind.Object && P(quota, "rateLimits").ValueKind == JsonValueKind.Object)
            buckets.Add(Bucket("codex", P(quota, "rateLimits")));
        return new QuotaSnapshot(accountNamespace, DateTimeOffset.UtcNow, buckets);
    }, cancellationToken);

    private async Task<T> ExecuteAsync<T>(Func<CancellationToken, Task<T>> read, CancellationToken ct)
    {
        await gate.WaitAsync(ct);
        try
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromSeconds(25));
            try
            {
                await EnsureStartedAsync(timeout.Token);
                return await read(timeout.Token);
            }
            catch (OperationCanceledException) { Stop(); if (ct.IsCancellationRequested) throw; throw new SourceUnavailableException("Codex не ответил вовремя. Повторите обновление."); }
            catch (RpcUnavailableException) { Stop(); throw new SourceUnavailableException("Эти данные Codex сейчас недоступны."); }
            catch (SourceUnavailableException) { throw; }
            catch (Exception ex) when (ex is IOException or JsonException or InvalidOperationException or System.ComponentModel.Win32Exception)
            { Stop(); throw new SourceUnavailableException("Не удалось прочитать данные Codex. Проверьте установку и вход."); }
        }
        finally
        {
            // Account reads are stateless. Release the large helper between polls;
            // the next operation performs a new initialize handshake.
            Stop();
            gate.Release();
        }
    }

    private async Task EnsureStartedAsync(CancellationToken ct)
    {
        if (process is { HasExited: false }) return;
        Stop();
        var path = executablePath ?? FindExecutable();
        if (path is null) throw new SourceUnavailableException("Codex CLI не найден. Установите Codex CLI или приложение Codex.");
        var start = new ProcessStartInfo(path) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true, StandardOutputEncoding = Encoding.UTF8, StandardErrorEncoding = Encoding.UTF8 };
        foreach (var arg in new[] { "app-server", "--stdio", "-c", "analytics.enabled=false", "-c", "otel.exporter=\"none\"" }) start.ArgumentList.Add(arg);
        process = Process.Start(start) ?? throw new SourceUnavailableException("Не удалось запустить Codex.");
        process.StandardInput.AutoFlush = true;
        stderrDrain = DrainAsync(process.StandardError);
        await RpcAsync("initialize", new { clientInfo = new { name = "limits_widget", version = "0.1.0" }, capabilities = new { experimentalApi = true } }, ct);
        await process.StandardInput.WriteLineAsync("{\"method\":\"initialized\"}".AsMemory(), ct);
    }

    private async Task<JsonElement> RpcAsync(string method, object parameters, CancellationToken ct)
    {
        if (!Methods.Contains(method)) throw new InvalidOperationException("Method outside allowlist.");
        var active = process ?? throw new IOException();
        var id = ++requestId;
        var request = JsonSerializer.Serialize(new { id, method, @params = parameters });
        await active.StandardInput.WriteLineAsync(request.AsMemory(), ct);
        while (true)
        {
            var line = await active.StandardOutput.ReadLineAsync(ct) ?? throw new IOException();
            using var document = JsonDocument.Parse(line);
            var root = document.RootElement;
            if (L(root, "id") != id) continue; // Discard notifications and unmatched replies without logging.
            if (P(root, "error").ValueKind == JsonValueKind.Object) throw new RpcUnavailableException();
            return P(root, "result").Clone();
        }
    }

    private static async Task DrainAsync(StreamReader reader)
    {
        try { var buffer = new char[1024]; while (await reader.ReadAsync(buffer) != 0) { } }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException) { }
    }

    private void Stop()
    {
        var old = process; process = null;
        if (old is null) return;
        try { if (!old.HasExited) old.Kill(entireProcessTree: true); } catch (InvalidOperationException) { } catch (System.ComponentModel.Win32Exception) { }
        old.Dispose();
    }

    public async ValueTask DisposeAsync()
    {
        await gate.WaitAsync();
        try { if (disposed) return; disposed = true; Stop(); if (stderrDrain is not null) await stderrDrain; }
        finally { gate.Release(); }
    }

    public static string? FindExecutable()
    {
        // Only native binaries: never invoke shell wrappers, interpolate arguments, or execute npm.
        var roots = new List<string>();
        foreach (var entry in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            var dir = entry.Trim('"');
            var direct = Path.Combine(dir, "codex.exe");
            if (File.Exists(direct)) return direct;
            roots.Add(Path.Combine(dir, "node_modules", "@openai", "codex"));
        }
        roots.Add(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "npm", "node_modules", "@openai", "codex"));
        foreach (var root in roots)
            foreach (var arch in new[] { "x64", "arm64" })
            {
                var target = arch == "x64" ? "x86_64-pc-windows-msvc" : "aarch64-pc-windows-msvc";
                foreach (var native in new[] { Path.Combine(root, "vendor", target, "bin", "codex.exe"), Path.Combine(root, "node_modules", "@openai", "codex-win32-" + arch, "vendor", target, "bin", "codex.exe") })
                    if (File.Exists(native)) return native;
            }
        var packaged = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "OpenAI", "Codex", "bin");
        if (Directory.Exists(packaged))
            foreach (var directory in Directory.EnumerateDirectories(packaged).OrderByDescending(Directory.GetLastWriteTimeUtc))
            { var path = Path.Combine(directory, "codex.exe"); if (File.Exists(path)) return path; }
        return null;
    }

    private static QuotaBucket Bucket(string id, JsonElement b) => new(id, S(b, "limitName"), Window(P(b, "primary")), Window(P(b, "secondary")));
    private static QuotaWindow? Window(JsonElement w)
    {
        if (w.ValueKind != JsonValueKind.Object || !P(w, "usedPercent").TryGetDoubleSafe(out var used) || !double.IsFinite(used)) return null;
        var minutes = L(w, "windowDurationMins");
        var reset = L(w, "resetsAt");
        if (Date(reset) is null) reset = null;
        return new(Math.Clamp(used, 0, 100), minutes is > 0 and <= int.MaxValue ? (int?)minutes : null, reset);
    }
    private static JsonElement P(JsonElement x, string key) => x.ValueKind == JsonValueKind.Object && x.TryGetProperty(key, out var v) ? v : default;
    private static string? S(JsonElement x, string key) => P(x, key).ValueKind == JsonValueKind.String ? P(x, key).GetString() : null;
    private static long? L(JsonElement x, string key) => P(x, key).ValueKind == JsonValueKind.Number && P(x, key).TryGetInt64(out var n) ? n : null;
    private static DateTimeOffset? Date(long? seconds) { try { return seconds is null ? null : DateTimeOffset.FromUnixTimeSeconds(seconds.Value); } catch (ArgumentOutOfRangeException) { return null; } }
    private sealed class RpcUnavailableException : Exception;
}

internal static class JsonSafeExtensions
{
    internal static bool TryGetDoubleSafe(this JsonElement value, out double number) { number = 0; return value.ValueKind == JsonValueKind.Number && value.TryGetDouble(out number); }
}


