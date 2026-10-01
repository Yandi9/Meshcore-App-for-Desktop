using System.Text;
using System.Text.Json;

namespace MC1.Widget;

/// <summary>Adaptive Card templates for the widget. The small size is the one Windows shows on the lock screen.</summary>
public static class Templates
{
    public static string Data(AppStatus s)
    {
        using var ms = new MemoryStream();
        using (var w = new Utf8JsonWriter(ms))
        {
            w.WriteStartObject();
            w.WriteString("count", s.AppRunning ? s.Unread.ToString() : "–");
            w.WriteString("label", !s.AppRunning ? Labels.Get("notRunning", "MeshCore isn't running")
                : s.Labels?.GetValueOrDefault("unread") ?? (s.Unread == 1 ? "new message" : "new messages"));
            w.WriteString("battery", s.BatteryPercent is { } p ? $"🔋 {p}%" : !s.AppRunning ? "" : s.Connected ? "🔋 —" : Labels.Get("notConnected", "Radio not connected"));
            w.WriteString("radio", s.RadioName);
            w.WriteString("state", s.Connected || !s.AppRunning ? "" : s.State);
            w.WriteEndObject();
        }
        return Encoding.UTF8.GetString(ms.ToArray());
    }

    public static string Card(string size) => size == "small" ? Small : Medium;

    private const string Small = """
        {
          "type": "AdaptiveCard",
          "$schema": "http://adaptivecards.io/schemas/adaptive-card.json",
          "version": "1.5",
          "selectAction": { "type": "Action.Execute", "verb": "open" },
          "body": [
            { "type": "TextBlock", "text": "${count}", "size": "extraLarge", "weight": "bolder", "spacing": "none" },
            { "type": "TextBlock", "text": "${label}", "size": "small", "wrap": true, "spacing": "none" },
            { "type": "TextBlock", "text": "${battery}", "size": "small", "isSubtle": true, "spacing": "small" }
          ]
        }
        """;

    private const string Medium = """
        {
          "type": "AdaptiveCard",
          "$schema": "http://adaptivecards.io/schemas/adaptive-card.json",
          "version": "1.5",
          "selectAction": { "type": "Action.Execute", "verb": "open" },
          "body": [
            {
              "type": "ColumnSet",
              "columns": [
                {
                  "type": "Column", "width": "auto",
                  "items": [ { "type": "TextBlock", "text": "${count}", "size": "extraLarge", "weight": "bolder" } ]
                },
                {
                  "type": "Column", "width": "stretch", "verticalContentAlignment": "center",
                  "items": [
                    { "type": "TextBlock", "text": "${label}", "wrap": true },
                    { "type": "TextBlock", "text": "${radio}", "size": "small", "isSubtle": true, "spacing": "none" }
                  ]
                }
              ]
            },
            { "type": "TextBlock", "text": "${battery}", "spacing": "medium" },
            { "type": "TextBlock", "text": "${state}", "size": "small", "isSubtle": true, "spacing": "none" }
          ]
        }
        """;
}
