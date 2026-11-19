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
            ServiceHub.Audio = new AudioService();
            ServiceHub.Brightness = new BrightnessService();
            _translate = translate;

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

    private ITranslationService? _translate;

    protected override void OnExit(ExitEventArgs e)
    {
        try
        {
            // 先卸载插件：让插件在宿主服务还活着时释放资源
            try { Plugins.PluginLoader.UnloadAll(); } catch { }

            ServiceHub.Clipboard?.Stop();
            ServiceHub.Hotkey?.UnregisterAll();
            ServiceHub.Config?.Save();
            (ServiceHub.Media as IDisposable)?.Dispose();
            (ServiceHub.Weather as IDisposable)?.Dispose();
            (ServiceHub.Translate as IDisposable)?.Dispose();
            (ServiceHub.Live as IDisposable)?.Dispose();
            ServiceHub.Lyrics?.Dispose();
            ServiceHub.Audio?.Dispose();
            ServiceHub.Brightness?.Dispose();
            (ServiceHub.Notify as IDisposable)?.Dispose();
            ServiceHub.Radio?.Dispose();
        }
        catch { }
        base.OnExit(e);
    }

    public static string CrashLogPath => Path.Combine(AppContext.BaseDirectory, "data", "crash.log");

    public static void DebugNote(string note)
    {
        if (Environment.GetEnvironmentVariable("WISLAND_DEBUG") != "1") return;
        try
        {
            var dir = Path.Combine(AppContext.BaseDirectory, "data");
            Directory.CreateDirectory(dir);
            File.AppendAllText(Path.Combine(dir, "debug.log"),
                $"[{DateTime.Now:HH:mm:ss.fff}] {note}`r`n", Encoding.UTF8);
        }
        catch { }
    }

    public static void LogCrash(string where, Exception? ex)
    {
        try
        {
            var dir = Path.Combine(AppContext.BaseDirectory, "data");
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
