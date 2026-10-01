using Avalonia;
using Avalonia.Media;

namespace MC1.Windows;

public static class Program
{
    private static Mutex? _singleInstance;

    [STAThread]
    public static int Main(string[] args)
    {
        _singleInstance = new Mutex(true, "MeshCoreOne.Windows.SingleInstance", out var isFirst);
        if (!isFirst && !args.Contains("--allow-multiple"))
        {
            SingleInstance.SignalExistingInstance(args);
            return 0;
        }
        MC1.Core.Services.AppPaths.MigrateLegacyFolder();
        try
        {
            return BuildAvaloniaApp().StartWithClassicDesktopLifetime(args, Avalonia.Controls.ShutdownMode.OnExplicitShutdown);
        }
        catch (Exception ex)
        {
            try
            {
                var dir = MC1.Core.Services.AppPaths.Logs;
                Directory.CreateDirectory(dir);
                File.AppendAllText(Path.Combine(dir, "crash.log"), $"{DateTime.Now:O} {ex}\n");
            }
            catch { /* ignore */ }
            throw;
        }
    }

    public static AppBuilder BuildAvaloniaApp() =>
        AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .WithInterFont()
            .With(FontOptions)
            .With(new Win32PlatformOptions { OverlayPopups = true })
            .LogToTrace();

    /// <summary>Emoji in names and messages come from the colour emoji font rather than a monochrome symbol font.</summary>
    public static FontManagerOptions FontOptions => new()
    {
        DefaultFamilyName = "fonts:Inter#Inter",
        FontFallbacks =
        [
            new FontFallback { FontFamily = new FontFamily("Segoe UI Emoji") },
            new FontFallback { FontFamily = new FontFamily("Noto Color Emoji") },
            new FontFallback { FontFamily = new FontFamily("Segoe UI Symbol") },
        ],
    };
}

/// <summary>Brings the running instance to the front when the app is launched a second time.</summary>
internal static class SingleInstance
{
    private const string PipeName = "MeshCoreOne.Windows.Activate";
    public static event Action<string[]>? ActivationRequested;

    public static void SignalExistingInstance(string[] args)
    {
        try
        {
            using var client = new System.IO.Pipes.NamedPipeClientStream(".", PipeName, System.IO.Pipes.PipeDirection.Out);
            client.Connect(1000);
            var bytes = System.Text.Encoding.UTF8.GetBytes(string.Join("\n", args));
            client.Write(bytes.Length == 0 ? [0] : bytes);
        }
        catch { /* the other instance may be starting up */ }
    }

    public static void Listen()
    {
        _ = Task.Run(async () =>
        {
            while (true)
            {
                try
                {
                    using var server = new System.IO.Pipes.NamedPipeServerStream(PipeName, System.IO.Pipes.PipeDirection.In, 1,
                        System.IO.Pipes.PipeTransmissionMode.Byte, System.IO.Pipes.PipeOptions.Asynchronous);
                    await server.WaitForConnectionAsync().ConfigureAwait(false);
                    using var ms = new MemoryStream();
                    await server.CopyToAsync(ms).ConfigureAwait(false);
                    var text = System.Text.Encoding.UTF8.GetString(ms.ToArray()).Trim('\0');
                    ActivationRequested?.Invoke(text.Split('\n', StringSplitOptions.RemoveEmptyEntries));
                }
                catch { await Task.Delay(1000).ConfigureAwait(false); }
            }
        });
    }
}
