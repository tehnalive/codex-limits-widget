using System.IO;
using System.Text.Json;

namespace LimitsWidget.Windows;
public sealed class Settings
{
    public double? Left { get; set; }
    public double? Top { get; set; }
    public bool Topmost { get; set; } = true;
    public bool Notifications { get; set; } = true;
    public string? Bucket { get; set; }
    public HashSet<string> Delivered { get; set; } = [];
    public Dictionary<string, long> NotifiedWindows { get; set; } = [];
    private static readonly string FilePath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "CodexLimitsWidget", "settings.json");
    public static Settings Load()
    {
        try
        {
            var settings = JsonSerializer.Deserialize<Settings>(File.ReadAllText(FilePath)) ?? new();
            settings.Delivered ??= [];
            settings.NotifiedWindows ??= [];
            // Retain warnings sent by previous releases, including reset timestamps with jitter.
            foreach (string key in settings.Delivered)
            {
                int split = key.LastIndexOf('|');
                if (split < 0 || !long.TryParse(key[(split + 1)..], out long reset) || reset is < 0 or > 253402300799) continue;
                string identity = key[..split];
                if (!settings.NotifiedWindows.TryGetValue(identity, out long previous) || reset > previous)
                    settings.NotifiedWindows[identity] = reset;
            }
            return settings;
        }
        catch { return new(); }
    }
    public bool Save()
    {
        try { Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!); string temp = FilePath + ".tmp"; File.WriteAllText(temp, JsonSerializer.Serialize(this)); File.Move(temp, FilePath, true); return true; }
        catch (IOException) { return false; } catch (UnauthorizedAccessException) { return false; }
    }
}
