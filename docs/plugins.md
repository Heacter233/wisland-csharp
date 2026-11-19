# 插件开发规范

> 适用宿主版本：**0.2.0+**　｜　契约命名空间：`Wisland.Plugins`　｜　示例：`samples/hello-plugin`

本文档规定插件**必须声明什么、禁止做什么、可以做什么、界面怎么写**。
宿主会校验清单、隔离加载、托管资源；不符合规范的插件会被拒绝加载或功能受限。

---

## 0. 先读：信任模型

**插件与宿主同进程运行，不是沙箱。**

- 权限（`permissions`）是**声明 + 宿主守门**：未声明的宿主 API 会被拒绝并记日志，
  但插件仍然可以直接 `new HttpClient()`、直接 `File.ReadAllText()` —— 宿主拦不住。
- 因此：**装插件 = 信任它的作者**。宿主会在设置页展示每个插件声明的权限，
  让用户自己判断；也提供 `plugins.disabled` 供用户禁用。
- 未来的进程隔离、权限二次确认属于路线图，当前请以"源码可信"为前提。

---

## 1. 目录与安装

```
<程序目录>/
├─ Wisland.exe
└─ plugins/
   └─ hello/                 ← 目录名必须等于 id
      ├─ plugin.json         ← 清单（必需）
      └─ HelloPlugin.dll     ← 程序集（必需）
```

```
<数据目录>/data/plugins/<id>/
├─ config.json               ← 宿主提供，插件通过 IPluginConfig 读写
└─ plugin.log                ← 宿主提供，插件通过 IPluginLogger 写
```

**规则**

- 目录名必须**完全等于** `id`（不匹配直接拒绝加载）。
- 插件目录里**只放自己的程序集与资源**，不要复制 `Wisland.exe`、`System.*.dll`、
  `Microsoft.*.dll`、`WinRT.Runtime.dll` 等宿主已有的依赖（宿主会从默认上下文解析）。
- 插件自己的第三方依赖（如 `Newtonsoft.Json.dll`）放在插件目录即可，加载器会从插件目录解析。
- 修改插件后**需要重启宿主**（热重载未实现）。

---

## 2. 清单 `plugin.json`

```json
{
  "id": "clash",
  "name": "Clash 控制",
  "version": "1.0.0",
  "author": "your-name",
  "description": "在岛内查看与切换 Clash 代理组",
  "entry": "ClashPlugin.ClashPlugin",
  "assembly": "ClashPlugin.dll",
  "minHostVersion": "0.2.0",
  "permissions": ["network.local", "messages.push", "ui.settings"],
  "homepage": "https://github.com/you/clash-plugin"
}
```

| 字段 | 必需 | 说明 |
| --- | --- | --- |
| `id` | ✅ | 唯一标识。小写字母/数字开头，只允许 `a-z 0-9 . _ -`，2~64 字符，**必须等于目录名** |
| `name` | ✅ | 显示名（设置页标题） |
| `version` | 建议 | 语义化版本，如 `1.0.0` |
| `author` | 建议 | 作者名 |
| `description` | 建议 | 一句话说明，会显示在设置页 |
| `entry` | ✅ | 入口类型全名，必须实现 `IWislandPlugin`，且有无参构造函数 |
| `assembly` | ✅ | 程序集**文件名**（不能带路径） |
| `minHostVersion` | 建议 | 要求的最低宿主版本；不满足直接拒绝加载 |
| `permissions` | ✅ | 权限数组，见下表；未声明的能力调用会被拒绝 |
| `homepage` | 可选 | 主页/仓库 |

清单非法（字段缺失、id 不合法、权限拼错、程序集不存在）→ 插件标记为 **加载失败**，
设置页显示具体原因，宿主继续正常运行。

---

## 3. 权限（必须声明）

| 权限 | 授予什么 | 不声明会怎样 |
| --- | --- | --- |
| `messages.push` | `ctx.PushMessage(...)` 推进消息页 | 调用被拒绝并记 WRN 日志 |
| `ui.settings` | `ctx.RegisterSettingsSection(...)` 提供设置面板 | 设置面板不出现 |
| `ui.page` | `ctx.RegisterPage(...)` 注册插件页面 | 页面不出现（图标行也不会显示） |
| `ui.compact` | 通知里用 `NotifyDraw` 在收起态自绘 | 自绘被剥离，只显示文字 |
| `notify.image` | 通知里用 `NotifyImage` 显示图片 | 图片被剥离 |
| `network` | 访问外网（http/https） | 声明性质，宿主不做拦截 |
| `network.local` | 只访问 `127.0.0.1` / `localhost`（如 Clash 外部控制器） | 声明性质，粒度更小、更易被用户接受 |
| `process.start` | 启动进程 / 打开文件/链接 | 声明性质 |
| `clipboard.read` | 读取剪贴板 | 声明性质 |
| `files.user` | 读写用户任意文件 | 默认只应读写自己的插件/数据目录 |
| `system.info` | 读取系统信息（CPU/内存/电池等） | 声明性质 |

> 只申请你真正用到的权限。**申请越多，用户越不会装。**

---

## 4. 契约 API

### 4.1 入口

```csharp
public interface IWislandPlugin
{
    void OnLoad(IPluginContext ctx);   // 后台线程调用，超时 8 秒
    void OnUnload();                   // 宿主退出/卸载时调用
}
```

### 4.2 上下文

```csharp
public interface IPluginContext
{
    PluginManifest Manifest { get; }
    IPluginConfig Config { get; }              // 持久化到 data/plugins/<id>/config.json
    IPluginLogger Log { get; }                 // 写 data/plugins/<id>/plugin.log
    string DataDirectory { get; }              // 可读写
    string PluginDirectory { get; }            // 只读
    Dispatcher Ui { get; }                     // 宿主 UI 线程
    CancellationToken ShutdownToken { get; }    // 卸载时取消
    string HostVersion { get; }                 // 用于能力探测，如 "0.2.0"

    bool Has(string permission);

    void PushMessage(PluginMessage message);      // 需 messages.push
    void RequestExpand(string pageKey = "");      // 展开到指定页（home/msg/weather/ctl/clip/trans/set 或插件页 key）
    void RequestCollapse();
    void RequestRetargetHeight();                 // 页面高度变了，让宿主重算
    void RequestRepaint();                        // 改了视觉树后必须调用，否则不重绘

    IDisposable RegisterTimer(TimeSpan interval, Action tick, bool runOnUi = true);
    void RegisterSettingsSection(string title, string subtitle, Func<FrameworkElement> build);

    /// 注册插件页面（需 ui.page）：图标出现在主导航下方的一行
    void RegisterPage(PluginPageDescriptor page);
}

public sealed class PluginPageDescriptor
{
    public string Key { get; init; }        // 页面 key，用于 RequestExpand("你的key")
    public string Title { get; init; }      // 悬停提示
    public string Glyph { get; init; }      // Segoe MDL2 Assets 码位
    public double Height { get; init; }     // 内容高度（DIP，建议 200~400）
    public Func<FrameworkElement> Build { get; init; }  // 延迟构建页面内容（UI 线程）
}
```

### 4.3 配置与日志

```csharp
public interface IPluginConfig
{
    T Get<T>(string key, T fallback = default!);
    void Set<T>(string key, T value, bool save = true);
    void Save();
    event EventHandler? Changed;
}

public interface IPluginLogger
{
    void Debug(string message);   // 只在 WISLAND_DEBUG=1 时有意义（宿主自身调试）
    void Info(string message);
    void Warn(string message);
    void Error(string message, Exception? exception = null);
}
```

配置键空间完全独立（插件自己的 `config.json`），**不要**去读写宿主的 `config.json`。

### 4.4 消息推送

```csharp
ctx.PushMessage(new PluginMessage
{
    Source = "clash",              // 必填：分组/磁贴标识，建议与插件 id 一致
    SourceName = "Clash",          // 磁贴显示名
    Title = "规则已更新",
    Body = "订阅已同步，共 1284 条规则",
    Level = "info",                // info / warn / error
    IconGlyph = "\uE774",          // Segoe MDL2 Assets 码位（可选）
    IconColor = "#E8544E",         // 磁贴底色（可选）
    Uri = "https://example.com",   // 点击打开链接（可选）
    Action = () => { },            // 点击执行回调（可选，进程内）
    NotifyImage = @"C:\a.png",     // 收起态通知图片（需 notify.image）
    NotifyDraw = (dc, rect, t) => { }, // 收起态自绘（需 ui.compact）
});
```

`PushMessage` 线程安全，可在任意线程调用。

---

## 5. 生命周期

1. 宿主启动时扫描 `plugins/*/plugin.json`
2. 校验清单 → 校验 `minHostVersion` → 检查是否被 `plugins.disabled` 禁用
3. 每个插件在**独立的可回收 AssemblyLoadContext** 中加载程序集
4. 反射找到 `entry` 类型 → 校验实现 `IWislandPlugin` → 无参构造
5. **在后台线程调用 `OnLoad`，超时 8 秒**；超时/抛异常 → 标记加载失败（含原因）
6. 插件注册的设置分组进入设置页（**延迟构建**，用户打开设置页时才调用 `Build`）
7. 宿主退出时：`OnUnload` → 释放托管定时器与配置 → `Unload()` 上下文

**因此 `OnLoad` 必须快**（建议 < 200ms）：只做注册与初始化，
网络请求、文件扫描放到定时器或 `Task.Run` 里，并且**用 `ShutdownToken` 做取消**。

---

## 6. 界面规范（设置面板）

插件只能通过 `RegisterSettingsSection` 提供**一个**设置面板，宿主把它放进设置页的卡片里。

### 6.1 必须

- 用 **宿主主题资源**做颜色/字体，切换主题才不会错色：
  - 文本：`AppText`、`AppText2`、`AppText3`
  - 背景：`AppCard`、`AppHover`、`AppPill`、`AppTrack`、`AppFill`、`AppOutline`
  - 强调：`AppOn`、`AppOnFg`、`AppUp`、`AppDown`
  - 样式：`T1` / `T2` / `T3`（文本）、`Card`（卡片）、`Row`、`PillBtn`、`IconBtn`、`SegBtn`、`AppBox`（输入框）
  - 字体：`UiFont`、`NumFont`、`IconFont`
- 用 `SetResourceReference(...)` 绑定（动态资源），**不要** `FindResource` 一次就固定下来
- 面板宽度自适应（宿主已给 590px 容器），**不要**写死 `Width` 超过 590
- 面板要能承载"没有内容"的状态（例如接口未配置时显示提示文字）
- `Build()` 内部异常必须自己 try/catch，否则该卡片显示"设置面板构建失败"

### 6.2 禁止

| 禁止 | 原因 |
| --- | --- |
| 自建 `Window` / `MessageBox` / 独立 `Popup` 去抢焦点 | 宿主是无边框置顶窗口，独立窗口会盖住岛、破坏置顶与拖拽 |
| 硬编码颜色（`Brushes.White`、`#FF0000` 等） | 切主题/切玻璃模式后会错色 |
| 在 `OnLoad` 里创建 WPF 控件 | `OnLoad` 在后台线程，且会拖慢宿主启动 |
| 对大面积元素用 `BlurEffect` / 多层 `DropShadowEffect` | 与宿主毛玻璃叠加后掉帧明显 |
| 面板里放巨大列表（>50 项一次全建） | 设置页是单个滚动容器，会卡；请分页或折叠 |
| 阻塞 UI 线程（`Thread.Sleep`、`.Result`、`.Wait()`） | 会让整个岛卡住 |
| 高频定时器（< 100ms） | 宿主强制夹到 100ms；更高频会明显耗电 |

### 6.3 推荐写法

```csharp
private FrameworkElement BuildPanel()
{
    var root = new StackPanel();

    var text = new TextBlock { Text = "说明", TextWrapping = TextWrapping.Wrap };
    text.SetResourceReference(FrameworkElement.StyleProperty, "T3");   // 跟随主题
    root.Children.Add(text);

    var input = new TextBox { Height = 34, Margin = new Thickness(0, 10, 0, 0) };
    input.SetResourceReference(FrameworkElement.StyleProperty, "AppBox");
    root.Children.Add(input);

    var btn = new Button { Content = "执行", Height = 28, HorizontalAlignment = HorizontalAlignment.Left };
    btn.SetResourceReference(FrameworkElement.StyleProperty, "PillBtn");
    root.Children.Add(btn);

    return root;
}
```

---

## 6.5 插件页面（ui.page）

插件可以注册自己的页面，**也可以不注册**：

- 注册了 → 图标出现在**主导航下方单独一行**；插件多时该行**横向滚动**（滚轮可横向滑）
- 没注册 → 该行完全不出现，其他元素位置不变（不占高度）

```csharp
context.RegisterPage(new PluginPageDescriptor
{
    Key = "clash",                       // 也可用于 ctx.RequestExpand("clash")
    Title = "Clash 控制",
    Glyph = "\uE774",
    Height = 320,                        // 内容区高度
    Build = BuildClashPage,              // 延迟构建，UI 线程
});
```

**页面内容规范**

| 项 | 要求 |
| --- | --- |
| 宽度 | 宿主给约 **616**（与内置页一致），不要写死更宽 |
| 高度 | 由 `Height` 决定（不含导航与内边距）；内容超出请自己放滚动容器 |
| 选中态 | 插件页与内置页**互斥**：点插件页时主导航全部收起高亮，反之亦然 |
| 主题 | 同设置面板：只用 `AppText*` / `AppCard` / `T1|T2|T3` / `Card` 等资源 |
| 自建滚动 | 用宿主的 `ScrollHost` 或普通 `ScrollViewer`（注意别套两层抢滚轮） |
| 焦点 | 不要把键盘焦点抢到页面外；需要输入时用 `AppBox` 样式 |
| 异常 | `Build()` 抛异常会被宿主捕获并显示错误占位，**但请自己 try/catch 并记日志** |
| 重绘 | **直接改视觉树（换文字/增删子元素）后必须调用 `ctx.RequestRepaint()`** —— 宿主空闲时不会自动重绘，否则改动要等下次动画才出现 |
| 生命周期 | 页面可能被反复切走/切回；`Build()` 只调用一次，返回的元素会被复用 |

> 多插件时图标行最多显示约 15 个（30px 宽 + 间距），超出横向滚动；
> 建议一个插件只注册 1 个页面，把内部切换做成页面里的分段控件。

---

## 7. 禁止事项（硬性）

违反以下任一条，宿主可以拒绝加载或在评审中打回：

1. **不要操作宿主窗口**：不要 `FindWindow` / `SetWindowLong` / 改 `WindowStyle` /
   调 `DwmSetWindowAttribute` / 调 `SystemBlur` 等宿主内部类型。
2. **不要改宿主配置**：只允许 `ctx.Config`（自己的 config.json）；
   不要直接读写宿主 `config.json`，不要改 `island.*`、`hotkeys.*` 等键。
3. **不要碰其他插件**：不要读别的插件的数据目录、不要反射别的插件的程序集。
4. **不要自我更新**：不要下载并执行 exe/脚本、不要写注册表启动项、不要静默改全局热键。
5. **不要后台常驻线程不退出**：所有线程/定时器必须响应 `ShutdownToken` 并在 `OnUnload` 停止。
6. **不要 `unsafe` / P/Invoke 加载外部原生 DLL**，除非在文档中说明并在清单里申请相应权限。
7. **不要做隐私相关采集**（键盘记录、剪贴板轮询上传、截屏上传）；
   需要剪贴板内容必须声明 `clipboard.read`，且只在用户触发时读取。
8. **不要伪造来源**：`PluginMessage.Source` 必须是自己的 id，不要冒充 `qq` / `wechat` / `mail`。
9. **不要静默失败**：捕获异常要 `ctx.Log.Error(...)`，方便用户排查。
10. **不要依赖未公开字段**：只使用 `Wisland.Plugins` 命名空间下的契约，
    其他宿主类型（除了 `PluginMessage`、`Themes` 这类公开 API）随时可能变。

---

## 8. 线程与性能

- UI 更新：`ctx.Ui.BeginInvoke(...)` 或 `Dispatcher.Invoke`；不要跨线程直接改控件。
- 网络：`HttpClient` + `async/await`，超时 ≤ 10s，失败要有用户可读的提示。
- 定时器：用 `ctx.RegisterTimer`（宿主托管，卸载自动释放），间隔 ≥ 100ms；
  一次性任务用 `Task.Run` + `ShutdownToken`。
- 内存：消息不要无限累积（`MessageInbox` 上限 50 条，宿主会裁剪）。

---

## 9. 打包

```
clash-1.0.0.zip
└─ clash/
   ├─ plugin.json
   ├─ ClashPlugin.dll
   └─ (可选) 自己的依赖 dll / 资源
```

- 压缩包解压后目录名必须等于 `id`
- **不要**包含 `Wisland.exe`、`System.*.dll`、`Microsoft.*.dll`
- 建议在 `homepage` 里写清楚插件做什么、需要哪些权限、为什么需要

---

## 10. 调试与排错

| 现象 | 先看哪里 |
| --- | --- |
| 设置页显示"加载失败" | 卡片上的「原因」+ `data/plugins/<id>/plugin.log` |
| 消息推不出来 | 检查是否声明 `messages.push`；`plugin.log` 会有"权限不足"WRN |
| 设置面板不出现 | 是否声明 `ui.settings`；`Build()` 是否抛异常 |
| 插件代码改动没生效 | 需要重启宿主（无热重载） |
| 加载超时 | `OnLoad` 里有阻塞操作；改到定时器/`Task.Run` |

调试模式：设置环境变量 `WISLAND_DEBUG=1` 后启动宿主，
`data/debug.log` 会记录插件加载数量与失败原因。

---

## 11. 版本与兼容

- 插件用 `minHostVersion` 声明最低宿主版本；宿主低于该版本时拒绝加载。
- 宿主版本可通过 `ctx.HostVersion` 在运行时探测，做能力降级：
  ```csharp
  if (Version.TryParse(ctx.HostVersion, out var v) && v >= new Version(0, 3, 0))
      /* 用新能力 */;
  ```
- 契约只增不改：新增接口成员会提供默认实现，避免老插件编译失败。

---

## 12. 示例

完整可运行示例见 [`samples/hello-plugin`](../samples/hello-plugin)：

- `plugin.json` —— 清单与权限声明
- `HelloPlugin.cs` —— 实现 `IWislandPlugin`、注册设置面板、推送消息、使用宿主定时器
- `HelloPlugin.csproj` —— `ProjectReference ... Private="false"`（关键：不要把宿主复制进插件目录）

构建并安装：

```bash
cd samples/hello-plugin
dotnet build -c Debug
# 把两样东西复制到 <程序目录>/plugins/hello/
#   HelloPlugin.dll
#   plugin.json
```

---

## 13. 路线图

- [x] 插件可注册导航页（主导航下方一行，横向滚动）
- [ ] 进程隔离（真沙箱，崩溃不影响宿主）
- [ ] 首次加载时的权限确认弹窗
- [ ] 插件热重载
- [ ] 插件市场 / 索引