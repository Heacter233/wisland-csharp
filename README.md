# Wisland · C# 重写版（WIP）

用 **C# / .NET 8 + WPF** 重写 [AsamiAkito/wisland](https://github.com/AsamiAkito/wisland)（原版：Python + PySide6，MIT）的 Windows 桌面「液态灵动岛」。

> **当前状态：未完成，持续推进中。** 主分支 `main` 为主线（重写完成后的正式版本），当前开发工作在 `csharp-rewrite` 分支。

<p align="center">
<img src="docs/compact.png" height="90">&nbsp;&nbsp;
<img src="docs/expanded.png" width="420">
</p>

## 关于本项目

- 原项目是一个贴在屏幕边缘的液态毛玻璃胶囊，悬停弹性展开，功能包括媒体控制、天气、控制中心、剪贴板、消息、翻译等。
- 本项目目标是用 **WPF 自绘**的方式在 C# 里复刻同样的交互与观感：窗口无边框 + 透明分层 + 自定义绘制 + 阻尼弹簧动画，而不是套用现成控件库。
- 协议：**MIT**。原项目同为 MIT，本仓库保留同样的授权。

技术栈：`.NET 8` + `WPF`，目标框架 `net8.0-windows10.0.19041.0`（用到 WinRT 的 SMTC / 通知 / Radio API）。

## 构建与运行

需要 **.NET 8 SDK**，Windows 10 1809+ / Windows 11。

```bash
cd WislandCSharp
dotnet build -c Debug
```

产物在 `WislandCSharp\bin\Debug\net8.0-windows10.0.19041.0\Wisland.exe`，双击运行即可（首次启动会在同目录生成 `config.json` 与 `data\`）。

也可以直接 `dotnet run --project WislandCSharp`。

## 功能对照（与原版）

| 模块 | 状态 | 说明 |
| --- | --- | --- |
| 灵动岛窗口 / 展开收起 | ✅ | 阻尼弹簧、按压挤压、贴边吸附、拖动换边 |
| 收起态（时钟 / 小形象 / 音乐） | ✅ | 滚轮切换视图；音乐态显示滚动歌词 |
| 小形象 | ✅ | 发光双眼跟随鼠标、多种表情、眨眼 |
| 播放控制 | ✅ | 走系统 SMTC，无 SMTC 时退回媒体键 |
| 歌词 | ✅ | 网易云公开接口，含译文行；多候选择优 |
| 天气 | ✅ | Open-Meteo，多城市切换、逐小时 + 5 天预报 |
| 控制中心 | ✅ | 音量（CoreAudio）、亮度（WMI）、WLAN / 蓝牙（Radio API） |
| 剪贴板 | ✅ | 文本 + 图片缩略图，内容哈希去重，可置顶 / 删除 / 复制 |
| 消息 | ✅ | 解析 Windows 通知数据库 `wpndatabase.db`（QQ / 微信 / 邮件） |
| 翻译 | ✅ | DeepSeek 流式接口，文本翻译 + 实时字幕接线 |
| 单词本 | ✅ | 本地 JSON 持久化；Anki 同步未实现 |
| 设置界面 | ✅ | `config.json` 可视化，分组卡片 + 自动保存 |
| 托盘 / 热键 / 主题 / 单实例 | ✅ | 深色 / 浅色 / 胭脂 / 天青 + auto |
| 实时字幕 | ⚠️ | 依赖外部 Python ASR sidecar，C# 侧未完全自足 |
| 插件系统 | ◐ | 接口已预留：`Services/PluginRegistry.cs`（注册 + 设置面板挂载点） |
| 液态形变（液桥 / 拉丝 / 贴边内凹） | ❌ | 待实现；当前只有弹簧缩放与按压挤压 |
| 股票行情 | ❌ | 未实现 |
| Clash 代理控制 | ❌ | 未接入 |

## 已知问题

1. **毛玻璃残影（重要）**
   展开态下滚动或切换内容时，玻璃层可能残留上一帧的部分像素。原因是 WPF 分层窗口（`AllowsTransparency`）对脏矩形的处理：子树移动后腾出的区域不会被父层完整重绘，而玻璃本身是半透明的，旧内容会透出来。目前的对策是滚动时逐级重绘祖先、展开态每秒强制重新抓取背景，但**尚未彻底解决**。
2. **部分功能未完全实现**：见上表的 ⚠️ / ◐ / ❌ 项；它们不是"看起来能用其实没做"，设置页里有「扩展与实验」分组如实标注状态。
3. **液态形变未移植**：原版的液桥、拉丝、贴边内凹圆角是逐帧几何建模，目前只有简化版（弹簧缩放 + 按压压扁）。

## 后续计划

- **插件系统**：`PluginRegistry` 已提供注册与设置面板挂载点，下一步做加载器（从 `plugins/` 目录加载、隔离异常、生命周期管理）。
- 液态形变（液桥 / 拉丝）的几何实现。
- 股票行情、Clash 集成、Anki 同步。
- 修复毛玻璃残影（考虑自绘背景快照 + 整层重绘的替代方案）。

## 目录结构

```
WislandCSharp/
├─ App.xaml(.cs)          应用入口、服务装配、全局异常
├─ MainWindow.xaml(.cs)   无边框分层窗口、托盘、热键、窗口定位、动画主循环
├─ Theme/                 调色板（4 套 + auto）与 XAML 样式
├─ Controls/              自绘控件：岛体、导航、收起态、小形象、弹簧、滚动容器、背景模糊
├─ Pages/                 6 个页面 + 设置页（XAML + 代码）
└─ Services/              媒体、歌词、天气、翻译、剪贴板、通知、无线电、音量、亮度、单词本、插件注册表
```

## 授权

MIT License，详见 [LICENSE](LICENSE)。

原项目：[AsamiAkito/wisland](https://github.com/AsamiAkito/wisland)（MIT）。实时字幕与单词本流水线的设计思路来自该项目移植自的 [YishengTranslator-White](https://github.com/kayson-39/YishengTranslator-White)（MIT）。