using System.Collections.Concurrent;
using System.Diagnostics;
using Microsoft.Windows.Widgets;
using Microsoft.Windows.Widgets.Providers;

namespace MC1.Widget;

/// <summary>The "MeshCore" widget: number of new messages and the radio's battery, sent by the running app.</summary>
public sealed partial class WidgetProvider : IWidgetProvider
{
    public const string DefinitionId = "MeshCoreStatus";

    /// <summary>Set when no MeshCore widget is pinned anywhere any more.</summary>
    public static readonly ManualResetEvent NoWidgetsLeft = new(false);

    private static readonly ConcurrentDictionary<string, string> Widgets = new(); // id → size
    private static readonly object Gate = new();
    private static Timer? _timer;
    private static bool _listening;

    public static bool HasWidgets => !Widgets.IsEmpty;

    public WidgetProvider()
    {
        // Widgets pinned before this process started (e.g. after a reboot).
        try
        {
            foreach (var info in WidgetManager.GetDefault().GetWidgetInfos() ?? [])
            {
                var ctx = info.WidgetContext;
                if (ctx.DefinitionId == DefinitionId) Widgets[ctx.Id] = SizeName(ctx.Size);
            }
        }
        catch { /* host not ready yet */ }
        Start();
        PushAll();
    }

    /// <summary>Starts listening to the app (pushes every change) plus a slow re-send in case Windows dropped one.</summary>
    private static void Start()
    {
        lock (Gate)
        {
            if (!_listening)
            {
                _listening = true;
                StatusClient.Changed += _ => PushAll();
                StatusClient.Start();
            }
            _timer ??= new Timer(_ => PushAll(), null, TimeSpan.FromMinutes(1), TimeSpan.FromMinutes(1));
        }
    }

    public void CreateWidget(WidgetContext widgetContext)
    {
        Widgets[widgetContext.Id] = SizeName(widgetContext.Size);
        NoWidgetsLeft.Reset();
        Start();
        Push(widgetContext.Id);
    }

    public void DeleteWidget(string widgetId, string customState)
    {
        Widgets.TryRemove(widgetId, out _);
        if (Widgets.IsEmpty) NoWidgetsLeft.Set();
    }

    public void OnActionInvoked(WidgetActionInvokedArgs actionInvokedArgs)
    {
        if (actionInvokedArgs.Verb == "open")
        {
            try { Process.Start(new ProcessStartInfo("meshcore://open") { UseShellExecute = true }); }
            catch { /* app not installed next to the widget */ }
        }
        else PushAll();
    }

    public void OnWidgetContextChanged(WidgetContextChangedArgs contextChangedArgs)
    {
        var ctx = contextChangedArgs.WidgetContext;
        Widgets[ctx.Id] = SizeName(ctx.Size);
        Push(ctx.Id);
    }

    public void Activate(WidgetContext widgetContext)
    {
        Widgets[widgetContext.Id] = SizeName(widgetContext.Size);
        Push(widgetContext.Id);
    }

    public void Deactivate(string widgetId)
    {
        // Keep sending updates: the lock screen may show the widget without activating it.
    }

    private static string SizeName(WidgetSize size) => size switch
    {
        WidgetSize.Small => "small",
        WidgetSize.Large => "large",
        _ => "medium",
    };

    private static void PushAll()
    {
        foreach (var id in Widgets.Keys) Push(id);
    }

    private static void Push(string widgetId)
    {
        try
        {
            var size = Widgets.TryGetValue(widgetId, out var s) ? s : "small";
            var options = new WidgetUpdateRequestOptions(widgetId)
            {
                Template = Templates.Card(size),
                Data = Templates.Data(StatusClient.Current),
                CustomState = "",
            };
            WidgetManager.GetDefault().UpdateWidget(options);
        }
        catch { /* widget removed meanwhile */ }
    }
}
