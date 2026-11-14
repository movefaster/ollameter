using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Win32;

namespace Ollameter;

/// <summary>
/// What the Mac app keeps in its defaults, kept here in <c>%APPDATA%\Ollameter\settings.json</c>. The key names
/// match the Mac's defaults keys.
/// </summary>
internal sealed class Settings
{
    [JsonPropertyName("sources")] public List<string>? Sources { get; set; }
    [JsonPropertyName("sharing")] public bool Sharing { get; set; }
    [JsonPropertyName("compact")] public bool Compact { get; set; }
    [JsonPropertyName("panelVisible")] public bool? PanelVisible { get; set; }
    [JsonPropertyName("alwaysOnTop")] public bool? AlwaysOnTop { get; set; }
    /// <summary>The window's top-left corner in physical pixels on the virtual screen.</summary>
    [JsonPropertyName("windowLeft")] public int? WindowLeft { get; set; }
    [JsonPropertyName("windowTop")] public int? WindowTop { get; set; }

    public static string Directory =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Ollameter");

    private static string FilePath => Path.Combine(Directory, "settings.json");

    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    /// <summary>Set for runs that mustn't change what the user chose, such as a render or a self-test.</summary>
    public static bool ReadOnly { get; set; }

    public static Settings Current { get; } = Load();

    private static Settings Load()
    {
        try
        {
            return JsonSerializer.Deserialize<Settings>(File.ReadAllText(FilePath), Options) ?? new Settings();
        }
        catch (Exception exception) when (exception is IOException or JsonException or UnauthorizedAccessException)
        {
            return new Settings();
        }
    }

    public void Save()
    {
        if (ReadOnly) return;
        try
        {
            System.IO.Directory.CreateDirectory(Directory);
            // A new file moved into place, so a crash mid-write can't leave half a file.
            var temporary = FilePath + ".new";
            File.WriteAllText(temporary, JsonSerializer.Serialize(this, Options));
            File.Move(temporary, FilePath, overwrite: true);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
        }
    }
}

/// <summary>Starting with Windows, through the current user's Run key.</summary>
internal static class Autostart
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "Ollameter";

    private static string Command => $"\"{Environment.ProcessPath}\"";

    public static bool IsEnabled
    {
        get
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKey);
            return key?.GetValue(ValueName) is string value && value.Length > 0;
        }
    }

    /// <summary>True when the entry runs this copy of the app, rather than one that has since moved.</summary>
    public static bool PointsHere
    {
        get
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKey);
            return string.Equals(key?.GetValue(ValueName) as string, Command, StringComparison.OrdinalIgnoreCase);
        }
    }

    public static void Set(bool enabled)
    {
        using var key = Registry.CurrentUser.CreateSubKey(RunKey);
        if (enabled) key.SetValue(ValueName, Command);
        else key.DeleteValue(ValueName, throwOnMissingValue: false);
    }
}

/// <summary>Timestamps of startup milestones, which the self-test writes to its report.</summary>
internal static class Marks
{
    private static readonly System.Diagnostics.Stopwatch Clock = System.Diagnostics.Stopwatch.StartNew();
    private static readonly List<string> List = [];

    public static void Add(string label)
    {
        lock (List)
        {
            if (List.Count < 200) List.Add($"{Clock.Elapsed.TotalSeconds:F3} {label}");
        }
    }

    public static List<string> All
    {
        get
        {
            lock (List) return [.. List];
        }
    }
}
