using System.IO.Pipes;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace MC1.Widget;

/// <summary>Status published by the MeshCore app (see MC1.Windows/Services/LiveStatus.cs).</summary>
public sealed record AppStatus(
    [property: JsonPropertyName("unread")] int Unread,
    [property: JsonPropertyName("mentions")] int Mentions,
    [property: JsonPropertyName("battery")] int? BatteryPercent,
    [property: JsonPropertyName("volts")] double? BatteryVolts,
    [property: JsonPropertyName("radio")] string RadioName,
    [property: JsonPropertyName("connected")] bool Connected,
    [property: JsonPropertyName("state")] string State)
{
    [JsonIgnore] public bool AppRunning { get; init; } = true;

    /// <summary>The widget's words in the app's language ("unread", "notConnected", "notRunning", "lang").</summary>
    [JsonPropertyName("labels")] public Dictionary<string, string>? Labels { get; init; }

    public static AppStatus NotRunning => new(0, 0, null, null, "", false, "") { AppRunning = false };
}

/// <summary>
/// The widget's words: as the app last sent them (in its language), remembered on disk for when the app isn't
/// running; English before the app has ever sent any.
/// </summary>
public static class Labels
{
    private static Dictionary<string, string> _last = Load();

    private static string FilePath => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "MeshCoreWidget", "labels.json");

    private static Dictionary<string, string> Load()
    {
        try
        {
            if (File.Exists(FilePath) && JsonSerializer.Deserialize(File.ReadAllText(FilePath), StatusJson.Default.DictionaryStringString) is { } d) return d;
        }
        catch { /* first run or unreadable */ }
        return [];
    }

    public static void Remember(Dictionary<string, string>? labels)
    {
        if (labels is null || labels.Count == 0) return;
        var same = labels.Count == _last.Count && labels.All(kv => _last.TryGetValue(kv.Key, out var v) && v == kv.Value);
        _last = labels;
        if (same) return;
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
            File.WriteAllText(FilePath, JsonSerializer.Serialize(labels, StatusJson.Default.DictionaryStringString));
        }
        catch { /* only a cache */ }
    }

    public static string Get(string key, string english) => _last.TryGetValue(key, out var v) && !string.IsNullOrEmpty(v) ? v : english;
}

/// <summary>
/// Listens to the running MeshCore app over a named pipe: the app sends its status when we connect and again every
/// time it changes, so the widget updates as soon as a message arrives.
/// </summary>
public static class StatusClient
{
    private static string PipeName => "MeshCore.Status." + Environment.UserName;
    private static readonly object Gate = new();
    private static Thread? _thread;
    private static volatile AppStatus _current = AppStatus.NotRunning;

    public static AppStatus Current => _current;

    /// <summary>Raised on the listener thread with each status the app sends (and when the app goes away).</summary>
    public static event Action<AppStatus>? Changed;

    public static void Start()
    {
        lock (Gate)
        {
            if (_thread is not null) return;
            _thread = new Thread(Listen) { IsBackground = true, Name = "MeshCore status" };
            _thread.Start();
        }
    }

    private static void Listen()
    {
        while (true)
        {
            try
            {
                using var client = new NamedPipeClientStream(".", PipeName, PipeDirection.In);
                client.Connect(2000);
                using var reader = new StreamReader(client, Encoding.UTF8);
                while (reader.ReadLine() is { } line)
                {
                    if (JsonSerializer.Deserialize(line, StatusJson.Default.AppStatus) is { } status)
                    {
                        Labels.Remember(status.Labels);
                        Set(status);
                    }
                }
            }
            catch { /* app not running, or it quit */ }
            if (_current.AppRunning) Set(AppStatus.NotRunning);
            Thread.Sleep(3000);
        }
    }

    private static void Set(AppStatus status)
    {
        _current = status;
        try { Changed?.Invoke(status); } catch { /* a widget failing mustn't stop the listener */ }
    }
}

[JsonSerializable(typeof(AppStatus))]
[JsonSerializable(typeof(Dictionary<string, string>))]
internal sealed partial class StatusJson : JsonSerializerContext;
