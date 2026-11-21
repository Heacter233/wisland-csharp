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
    private static System.Threading.EventWaitHandle? _activateEvent;

    /// <summary>第二个实例用来"唤起"已有实例的命名事件。</summary>
    private const string ActivateEventName = "wisland-activate-instance";

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        // 单实例：重复启动时唤起已有窗口，避免出现多个灵动岛。
        // 旧实现只是 Shutdown(0)——用户双击第二次毫无反馈（注释写"只唤起已有窗口"但没有任何唤起手段）。
        try
        {
            _singleInstance = new System.Threading.Mutex(true, "wisland-single-instance", out var isNew);
            if (!isNew)
            {
                try
                {
                    using var existing = System.Threading.EventWaitHandle.OpenExisting(ActivateEventName);
                    existing.Set();          // 通知已有实例：展开岛
                }
                catch (Exception ex) { DebugNote("single-instance: 唤起失败 " + ex.Message); }
                Shutdown(0);
                return;
            }
        }
        catch { }

        // 首个实例：起一个后台等待，收到信号就展开岛（回到上次页面）
        try
        {
            _activateEvent = new System.Threading.EventWaitHandle(
                false, System.Threading.EventResetMode.AutoReset, ActivateEventName);
            var ev = _activateEvent;
            _ = Task.Run(() =>
            {
                while (true)
                {
                    try
                    {
                        if (!ev.WaitOne()) break;
                        Dispatcher.BeginInvoke(new Action(() =>
                        {
                            try
                            {
                                MainWindow?.Activate();
                                ServiceHub.RequestExpand?.Invoke("");
                            }
                            catch (Exception ex) { DebugNote("single-instance: 展开失败 " + ex.Message); }
                        }));
                    }
                    catch { break; }
                }
            });
        }
        catch (Exception ex) { DebugNote("single-instance: 事件创建失败 " + ex.Message); }

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
        Step("activate-event", () => { try { _activateEvent?.Set(); _activateEvent?.Dispose(); } catch { } });
        base.OnExit(e);
    }

    /// <summary>退出链的单个步骤：失败只记日志，不阻断后续释放；慢步骤也会记录耗时。</summary>
    private static void Step(string name, Action action)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        try { action(); }
        catch (Exception ex) { DebugNote($"exit[{name}]: {ex.GetType().Name} {ex.Message}"); }
        finally
        {
            sw.Stop();
            // 超过 50ms 的收尾步骤值得记一笔：退出卡顿基本都能在这里定位到
            if (sw.ElapsedMilliseconds > 50) DebugNote($"exit[{name}]: {sw.ElapsedMilliseconds}ms");
        }
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
