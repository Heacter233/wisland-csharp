using System;
using System.IO;
using System.Text;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Threading;
using Wisland.Controls;
using Wisland.Services;
using Wisland.Theme;

namespace Wisland;

/// <summary>
/// 应用入口：填充 ServiceHub、应用主题、创建主窗口。
/// 所有异常写入 data/crash.log，避免"闪退且无提示"。
/// </summary>
public partial class App : Application
{
    public App()
    {
        AppDomain.CurrentDomain.UnhandledException += (_, ev) =>
            LogCrash("AppDomain.UnhandledException", ev.ExceptionObject as Exception);
        DispatcherUnhandledException += (_, ev) =>
        {
            LogCrash("DispatcherUnhandledException", ev.Exception);
            ev.Handled = true;
        };
        TaskScheduler.UnobservedTaskException += (_, ev) =>
        {
            LogCrash("UnobservedTaskException", ev.Exception);
            ev.SetObserved();
        };
    }

    private static System.Threading.Mutex? _singleInstance;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        // 单实例：重复启动只唤起已有窗口，避免出现多个灵动岛
        try
        {
            _singleInstance = new System.Threading.Mutex(true, "wisland-single-instance", out var isNew);
            if (!isNew)
            {
                Shutdown(0);
                return;
            }
        }
        catch { }

        try
        {
            // 服务装配
            var config = new ConfigService();
            var clipboard = new ClipboardService(config);
            var hotkey = new HotkeyService();
            var media = new MediaService();
            var weather = new WeatherService();
            var lyrics = new LyricsService();
            var translate = new TranslationService(config,
                Path.Combine(config.DataDir, "translate_history.json"));
            var live = new LiveService();
            var notify = new NotifyService(config);
            var radio = new RadioService();
            var wordbook = new WordbookService(config);
            var models = new ModelManager(config);

            ServiceHub.Config = config;
            ServiceHub.Clipboard = clipboard;
            ServiceHub.Hotkey = hotkey;
            ServiceHub.Media = media;
            ServiceHub.Weather = weather;
            ServiceHub.Lyrics = lyrics;
            ServiceHub.Translate = translate;
            ServiceHub.Live = live;
            ServiceHub.Notify = notify;
            ServiceHub.Radio = radio;
            ServiceHub.Wordbook = wordbook;
            ServiceHub.Models = models;
            ServiceHub.Audio = new AudioService();
            ServiceHub.Brightness = new BrightnessService();

            // 应用主题
            ThemeApplier.ApplyMode(Resources, config.Get("island.theme", "auto"));

            // 主窗口
            var mainWindow = new MainWindow();
            MainWindow = mainWindow;
            mainWindow.Show();
        }
        catch (Exception ex)
        {
            LogCrash("OnStartup", ex);
            MessageBox.Show($"wisland 启动失败：\n{ex.Message}\n\n日志：{CrashLogPath}",
                "wisland", MessageBoxButton.OK, MessageBoxImage.Error);
            Shutdown(1);
        }
    }

    protected override void OnExit(ExitEventArgs e)
    {
        // 每一步单独 try：早期整段包在一个 try 里，前面任何一步抛异常就会跳过
        // Config.Save() 与后面的释放，表现为"设置没保存 / 设备没释放"。
        Step("plugins", () => Plugins.PluginLoader.UnloadAll());
        Step("clipboard", () => (ServiceHub.Clipboard as IDisposable)?.Dispose());
        Step("hotkey", () => ServiceHub.Hotkey?.UnregisterAll());
        Step("config-save", () => ServiceHub.Config?.Save());
        Step("media", () => (ServiceHub.Media as IDisposable)?.Dispose());
        Step("weather", () => (ServiceHub.Weather as IDisposable)?.Dispose());
        Step("translate", () => (ServiceHub.Translate as IDisposable)?.Dispose());
        Step("live", () => (ServiceHub.Live as IDisposable)?.Dispose());
        Step("lyrics", () => ServiceHub.Lyrics?.Dispose());
        Step("audio", () => ServiceHub.Audio?.Dispose());
        Step("brightness", () => ServiceHub.Brightness?.Dispose());
        Step("notify", () => (ServiceHub.Notify as IDisposable)?.Dispose());
        Step("radio", () => ServiceHub.Radio?.Dispose());
        base.OnExit(e);
    }

    /// <summary>退出链的单个步骤：失败只记日志，不阻断后续释放。</summary>
    private static void Step(string name, Action action)
    {
        try { action(); }
        catch (Exception ex) { DebugNote($"exit[{name}]: {ex.GetType().Name} {ex.Message}"); }
    }

    public static string CrashLogPath => AppPaths.CrashLog;

    public static void DebugNote(string note)
    {
        if (Environment.GetEnvironmentVariable("WISLAND_DEBUG") != "1") return;
        try
        {
            var dir = AppPaths.DataDir;
            Directory.CreateDirectory(dir);
            // 注意用 "\r\n" 转义；早期写成 `r`n（反引号），日志被写成字面量且全程一行。
            File.AppendAllText(Path.Combine(dir, "debug.log"),
                $"[{DateTime.Now:HH:mm:ss.fff}] {note}\r\n", Encoding.UTF8);
        }
        catch { }
    }

    public static void LogCrash(string where, Exception? ex)
    {
        try
        {
            var dir = AppPaths.DataDir;
            Directory.CreateDirectory(dir);
            var sb = new StringBuilder();
            sb.AppendLine($"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {where}");
            sb.AppendLine(ex?.ToString() ?? "(no exception object)");
            sb.AppendLine(new string('-', 60));
            File.AppendAllText(Path.Combine(dir, "crash.log"), sb.ToString(), Encoding.UTF8);
        }
        catch { }
    }
}
