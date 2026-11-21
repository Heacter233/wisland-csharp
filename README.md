# Wisland · C# 重写版

用 **C# / .NET 8 + WPF** 重写 [AsamiAkito/wisland](https://github.com/AsamiAkito/wisland)（原版：Python + PySide6，MIT）的 Windows 桌面「液态灵动岛」。

<p align="center">
<img src="docs/compact.png" height="90">&nbsp;&nbsp;
<img src="docs/expanded.png" width="420">
</p>

> **当前版本 v0.2.2**（[Releases0.2.1](https://github.com/Heacter233/wisland-csharp/releases) 安装包）。
>
> 本版是一次**面向稳定性、性能与数据安全**的整修：跨 7 个批次处理了代码审查报告里的高危与中等项——
> 剪贴板设置失效、托盘幽灵图标、实时字幕"幽灵运行"、退出卡 3 秒/闪退、点击磁贴冻结 8 秒、
> 配置写坏后静默丢设置、通知库每 4 秒整库复制、常驻满帧率重绘、插件来源可伪造、模型无哈希校验等，
> 并补上**解决方案 / 单元测试 / GitHub Actions CI**。逐项说明见[当前版本：v0.2.2](#当前版本v022)。

## 下载

到 [Releases](https://github.com/Heacter233/wisland-csharp/releases) 下载 v0.2.1，按「运行库」和「插件版本」两个维度选一个包：

| 包 | 大小 | 运行库 | 附带插件 |
| --- | --- | --- | --- |
| `wisland-csharp-0.2.1-slim-x64.zip` | ~35 MB | 需要 [.NET 8 桌面运行库](https://dotnet.microsoft.com/download/dotnet/8.0) | 普通版：股票、Clash 控制 |
| `wisland-csharp-0.2.1-portable-x64.zip` | ~97 MB | **自带 .NET 8 运行库，解压即用** | 普通版：股票、Clash 控制 |
| `wisland-csharp-0.2.1-ff14-slim-x64.zip` | ~35 MB | 需要 .NET 8 桌面运行库 | FF14 专版：股票、Clash + **FF14 招募监控 / 物品价格 / 挖宝悬浮窗** |
| `wisland-csharp-0.2.1-ff14-portable-x64.zip` | ~97 MB | **自带 .NET 8 运行库，解压即用** | FF14 专版：股票、Clash + **FF14 招募监控 / 物品价格 / 挖宝悬浮窗** |

> 包体从 v0.2.0 的 7.8 MB 涨到 ~35 MB，是因为内置了 Whisper 的 **Vulkan GPU 运行库**（约 55 MB 中按平台裁剪后）
> 与 sherpa-onnx 的 ONNX 运行库；没有可用 GPU 时会自动回退 CPU，不影响功能。

双击 `Wisland.exe` 即可。首次运行会在 exe 同目录生成 `config.json` 与 `data\`；
自带插件在 `plugins\<id>\`，可以在设置页「插件设置」里查看/重载，不需要时直接删掉对应目录。
卸载直接删除文件夹（程序不写注册表，仅「开机自启」开关会登记启动项，可在设置页关闭）。

> **关于「未知发布者」**：本项目没有购买商业代码签名证书，Windows 可能提示「未知发布者」或被 SmartScreen 拦截。
> 程序以普通用户权限运行（`asInvoker`，不会请求管理员权限）。
> 处理方式：SmartScreen 提示时点「更多信息 → 仍要运行」；从浏览器下载的解压后右键 `Wisland.exe` → 属性 →
> 勾选「解除锁定(Unblock)」→ 确定。

## 系统要求

- Windows 10 19041+ / Windows 11，x64
- slim 包需要 [.NET 8 桌面运行库](https://dotnet.microsoft.com/download/dotnet/8.0)；portable 包自带
- 实时字幕：GPU 可选（Vulkan/CUDA 不可用时自动回退 CPU）；模型占用约 0.2~2 GB 磁盘（按下载的模型）
- 常驻内存参考：空闲约 170 MB（含 3 个插件）；空闲 CPU 约 2%~4%（单核百分比）
- 会联网获取天气 / 歌词 / 翻译；系统通知解析只读本机 `wpndatabase.db`，不上传

## 当前版本：v0.2.2

> 发布日期：2026-11-21 · 本版**没有新功能**，全部是修复与工程化：
> 分 7 批处理高危/中等遗留问题项，每批单独构建验证并提交。

### 修了什么（按批次）

| 批次 | 问题 | 修复后的行为 |
| --- | --- | --- |
| 1 功能性 | 剪贴板"历史条数"改了不生效（界面写 `max_items`、服务读 `max`）；退出后任务栏留幽灵图标；托盘右键一次弹两遍菜单；`debug.log` 被写成一行 | 键名统一并**带旧配置迁移**，改完立即裁剪；退出时删除托盘图标并释放 GDI 句柄；托盘结构体按 SDK 修正；日志恢复真实换行 |
| 2 数据安全 | 配置**非原子覆盖**，写到一半被终止就得到半截 JSON → 下次启动回落默认值并覆盖原文件，**设置（含 API Key）无声丢光**；设置页一次改动要全量写盘十几次 | 临时文件 + 原子替换 + 保留 `.bak`；损坏时隔离为 `config.json.corrupt-<时间戳>`、尽力从 `.bak` 恢复；设置页顶部显示红色异常提示条；批量写入合并为一次落盘 |
| 3 生命周期 | 启动中立刻点停止 → UI 显示已停止但音频/识别仍在跑且**再也停不下来**；运行中退出**必卡 3 秒**且可能 use-after-free 闪退；插件 OnLoad 在 UI 线程 wait 8 秒；点消息磁贴冻结约 8.5 秒 | 启动加代次令牌（每个 await 后复查）；退出改同步收尾、原生对象推迟到识别线程退出后释放；插件加载全异步 + 超时撤销上下文（僵尸插件被掐断权限）；启动外部程序移到后台线程 |
| 4 性能 | 渲染循环自激励，**常驻满帧率全窗重绘**；每次画字都新建 Typeface/画刷/FormattedText；文本用固定 1.0 DPI 渲染（高 DPI 发虚）；工作区永远取主屏 | 帧循环**空闲即停**（静止约 0.3 秒后暂停，鼠标/动画/定时器再唤醒）+ 150ms 兜底看门狗；字体/画刷/字宽缓存；文本用真实 DPI；工作区取"窗口所在显示器" |
| 5 服务 | 通知库**每 4 秒整库复制** `wpndatabase.db` 并重解码 200 条 toast；未读计数共享字典跨线程无锁；歌词缓存无上限且非线程安全；热键按住连发、注册失败静默；单实例只是退出不唤起；窗口唤出后永久置顶 | 库没变就整轮跳过 + 增量查询；字典入锁；歌词并发字典 + 200 首上限；热键加 `MOD_NOREPEAT` 并在失败时提示；**二次启动会唤起已有实例**；唤出后还原置顶 |
| 6 插件与模型 | 插件可绕过权限直推消息并**冒充 QQ/微信**；`ServiceHub` 静态服务对插件可写；模型下载**无哈希校验**（镜像投毒/续传拼错文件都能过） | 消息来源白名单 + `ctx.PushMessage` 强制改写为插件自己的 id；服务字段 setter 收为 internal；下载后按 HuggingFace LFS 的 SHA256 校验（取不到则仅校验体积并记日志） |
| 7 工程 | 没有 `.sln` / 测试 / CI / `.editorconfig`，纯逻辑代码零回归保护 | 新增解决方案、**29 个 xUnit 用例**、GitHub Actions CI、分析器与风格约定、日志轮转 |

### 本版的行为变化（升级请注意）

- **配置键名统一**：`clipboard.max_items` 为正式键（旧 `clipboard.max` 自动迁移）、`hover_delay_ms` 为正式键（旧 `hover_expand_ms` 自动迁移）。老配置文件启动时会自动改写一次，用户值不会丢。
- **`ServiceHub` 的 setter 变为 internal**：插件仍可**读**所有服务与回调，但不能再**改写**宿主服务/回调（官方示例不受影响）。任何第三方插件若直接写 `ServiceHub.XXX = ...` 需要改用 `IPluginContext` 提供的能力。
- **`MessageInbox.Push` 校验来源**：来源必须是内置（qq/wechat/mail/other）或已加载插件的 id，否则丢弃并记日志；插件应统一走 `ctx.PushMessage`。
- 退出更快：运行中退出不再固定等待 3 秒。

### 能力说明在哪

本版没有改动功能面，各能力的详细说明直接看对应章节，避免重复：

- 实时字幕 / 人声处理 / GPU 加速 → [§12 实时字幕](#12-实时字幕离线-asr)、[§15 人声处理](#15-人声处理降噪--目标说话人提取)
- 翻译引擎与回退链 → [§14 多引擎翻译](#14-多引擎翻译)
- 语音模型下载与管理 → [§13 模型管理](#13-模型管理翻译页-模型分段)
- 插件系统与权限 → [插件开发](#插件开发)、[docs/plugins.md](docs/plugins.md)
- 常驻性能与可靠性的做法 → [§16 常驻性能与可靠性](#16-常驻性能与可靠性v022)

## 插件开发

> ⚠️ **只安装可信插件**：插件与宿主**同进程**运行，权限是"清单声明 + 宿主守门"
> （`ctx.PushMessage` / `RegisterSettingsSection` / `RegisterPage` 三个入口会被检查，
> 未声明即拒绝并记日志），**不是沙箱**。装插件就等于信任它的作者——
> 它能读写本机文件、访问网络（是否用到这类能力会在设置页的权限列表里显示）。
> 进程隔离、首次加载权限确认在路线图中。

插件放在 `plugins/<id>/`（`plugin.json` + 程序集），宿主启动时校验清单、按插件隔离加载，
并在设置页展示状态、权限与插件自带的设置面板。

插件可选择性地注册**自己的页面**：图标会出现在主导航下方的独立一行（插件多时横向滚动），
不注册页面的插件不会占用这一行，其他元素位置保持不变。

- 完整规范（清单字段、权限表、禁止事项、界面写法）：**[docs/plugins.md](docs/plugins.md)**
- 清单 JSON Schema：`docs/plugin.schema.json`
- 示例插件：[`samples/`](samples) —— 6 个可运行样板 + Clash 测试替身

| 示例 | 能力 |
| --- | --- |
| [`samples/hello-plugin`](samples/hello-plugin) | 最小骨架：设置面板 / 插件页 / 消息推送 / 宿主定时器 |
| [`samples/stock-plugin`](samples/stock-plugin) | 真实第三方数据：自选股增删、大盘指数、分时与日 K 迷你图、涨跌提醒、多域名切换 + 磁盘缓存 |
| [`samples/clash-plugin`](samples/clash-plugin) | 本机服务控制：代理模式切换、策略组节点切换，**HTTP 与命名管道双通道**（自动回退） |
| [`samples/ff14-recruit-plugin`](samples/ff14-recruit-plugin) | FF14 招募监控：关键词轮换搜索、快照差分去重、屏蔽名单/屏蔽词/休息时段、卡片列表、自定义搜索页 |
| [`samples/ff14-price-plugin`](samples/ff14-price-plugin) | FF14 物品价格：离线物品词典（17k 条）、Universalis 市场查询、汇总/明细双表、筛选与常用词 |
| [`samples/ff14-treasure-plugin`](samples/ff14-treasure-plugin) | FF14 挖宝悬浮窗：连 [ACT 挖宝会计](https://github.com/Heacter233/FF14TreasureHunterACT) WebSocket、自绘宝箱成功/失败图案、出货合并通知 |
| [`samples/mock`](samples/mock) | 没有 Clash 时用来验证插件：HTTP mock 与命名管道 mock |

## 关于本项目

- 原项目是贴在屏幕边缘的液态毛玻璃胶囊，悬停弹性展开，功能覆盖媒体控制、天气、控制中心、剪贴板、消息、翻译等。
- 本项目目标是**用 WPF 自绘**在 C# 里复刻同样的交互与观感：无边框窗口 + 透明分层 + 自定义绘制 + 阻尼弹簧动画，而不是套用现成控件库。
- 原版实时字幕依赖 Python sidecar，本项目把整条链路（音频捕获 / VAD / 离线识别 / 翻译）都实装进 C# 进程内，不额外要求 Python 环境。
- 协议：**MIT**（与原项目一致）。

技术栈：`.NET 8` + `WPF`，目标框架 `net8.0-windows10.0.19041.0`（用 WinRT 的 SMTC / 通知 / Radio API）。

## 与原项目的功能对照

图例：✅ 成功复现　⚠️ 复现但不完美　❌ 未实现

| 模块 | 状态 | 说明 |
| --- | --- | --- |
| 灵动岛窗口 / 展开收起 | ✅ | 欠阻尼弹簧回弹、按压挤压、贴边吸附、拖动换边、展开态按住拖动会收起到指针下方 |
| 收起态（时钟 / 小形象 / 音乐） | ✅ | 滚轮切换视图；音乐态显示滚动歌词 |
| 小形象 | ✅ | 发光双眼跟随鼠标、多种表情、眨眼 |
| 播放控制 | ✅ | 走系统 SMTC（WinRT），无 SMTC 时退回媒体键 |
| 歌词 | ✅ | 多源：网易云（含译文行）→ **LiriQo**（11 源聚合）→ **LRCLIB**；都没有时间轴时退回纯文本、按播放进度滚动 |
| 天气 | ✅ | Open-Meteo，多城市切换（**候选列表可选同名地点 / 支持坐标直填 / 区县与县级市可搜**）、逐小时 + 5 天预报 |
| 控制中心 | ✅ | 音量（CoreAudio）、亮度（WMI）、WLAN / 蓝牙（Radio API） |
| 剪贴板 | ✅ | 文本 + 图片缩略图、内容哈希去重、可置顶 / 删除 / 复制 |
| 消息 | ✅ | 解析 Windows 通知数据库 `wpndatabase.db`（QQ / 微信 / 邮件），不读消息正文 |
| 翻译 | ✅ | **多引擎 + 自动回退**：Index-Translate（B站，免 Key）/ 智谱 GLM-4-Flash / DeepSeek / Google、Bing 免密钥接口，流式输出；翻译页 = 文本 / 实时字幕 / 单词本 / 模型 四个分段 |
| 单词本 | ✅ | 本地 JSON 持久化 |
| 设置界面 | ✅ | `config.json` 可视化卡片，分组 + 自动保存 |
| 托盘 / 热键 / 主题 / 单实例 | ✅ | 深色 / 浅色 ⚠️**目前不要使用浅色主题**，目前未做浅色主题底板适配。/ 胭脂 / 天青 + auto |
| 实时字幕 | ✅ | 纯 C# 进程内管线：WASAPI 回环 / 麦克风 → **人声处理（DPDFNet2/GTCRN/VoxLock）** → VAD 切句 → **SenseVoice（默认优先）/ Whisper** → 翻译；Whisper 支持 Vulkan/CUDA 加速，模型管理支持取消与断点续传 |
| 液态形变（液桥 / 拉丝 / 贴边内凹圆角） | ❌ | 当前只有弹簧缩放与按压挤压，逐帧几何建模未移植 |
| Anki 同步 | ❌ | 单词本只存本地，未接入 Anki |
| 股票行情 | ✅ | **通过示例插件实现**（`samples/stock-plugin`）：自选股、大盘指数、分时/日K 迷你图、涨跌提醒 |
| Clash 代理控制 | ✅ | **通过示例插件实现**（`samples/clash-plugin`）：模式切换、策略组节点切换，支持 HTTP 与命名管道 |
| 插件系统 | ✅ | 清单校验 + 独立 AssemblyLoadContext 加载 + 权限守门 + 设置面板与插件页挂载 + 插件页热重载 + 异常隔离；规范见 [docs/plugins.md](docs/plugins.md) |
| FF14 招募监控 | ✅ | **通过示例插件实现**（`samples/ff14-recruit-plugin`）：关键词轮换搜索招募板、快照差分去重、屏蔽/休息时段、卡片结果、自定义搜索页 |
| FF14 物品价格 | ✅ | **通过示例插件实现**（`samples/ff14-price-plugin`）：离线物品词典 + Universalis 价格、汇总/明细双表、筛选与常用词 |
| FF14 挖宝悬浮窗 | ✅ | **通过示例插件实现**（`samples/ff14-treasure-plugin`）：连接 ACT 挖宝会计 OverlayServer，悬浮窗 + 成功/失败宝箱图案通知 |

## 新增功能（原版没有）

### 1. 三种可切换的毛玻璃模式（`island.glass`）

| 模式 | 效果 | 取舍 |
| --- | --- | --- |
| `system` | DWM accent 实时模糊 | 玻璃内容**实时**跟随桌面；但模糊作用域是窗口矩形，四角需靠小圆角（`glass_corner`）规避 |
| `capture` | 抓屏模糊 | 形状完美贴合药丸，但玻璃内容**定格**在最后一次抓屏（视频/游戏场景是旧画面） |
| `solid` | 实色，无模糊 | 近不透明、性能最好 |

### 2. 插件消息推送接口

```csharp
ServiceHub.Inbox.Push(new PluginMessage {
    Source = "clash", SourceName = "Clash",
    Title = "规则已更新", Body = "订阅已同步，共 1284 条规则",
    Level = "info",                    // info / warn / error → 蓝 / 黄 / 红
    IconGlyph = "\uE774", IconColor = "#E8544E",   // 自定义图标
    Uri = "https://example.com",       // 点击打开
    Action = () => DoSomething(),      // 或进程内回调
    NotifyDraw = (dc, rect, t) => { }, // 收起态自定义动画
    NotifyImage = @"C:\a.png",         // 或图片
});
```
线程安全，`Changed` 事件回到 UI 线程刷新；未读数、磁贴、已读、跳转全部自动生效。

### 3. 消息页重做

- **动态磁贴**：内置 QQ / 微信 / 邮件固定在最前，插件来源自动生成磁贴（未读数 + 最近时间 + 自定义图标）
- **两行视口 + 滚轮查找**：来源多了不会撑爆页面
- **右键置顶**（持久化到 `island.msg_pinned`）
- **折叠分页**：默认 5 条 + 「显示全部（共 N 条）」
- **点击跳转**：`Uri` / `Action`；内置类型顺带打开对应程序
- **全部已读**：标记已读（保留历史）而不是删除

### 4. 收起态新消息提示

- **绿点**：有新消息时右上角显示
- **弹出模式**：胶囊加高，最多 3 行，超出顶掉最旧，逐条按时收起
- **滚动模式**：胶囊加宽，在新增区域横向滚动
- 停留时长可配（`island.notify_duration_ms`）
- 插件可用 `NotifyDraw` / `NotifyImage` 自带动画与图片，可用 `island.notify_plugin_anim = false` 一律禁用

### 5. 消息提示音（默认关闭）

- 总开关 + **按来源独立开关**：QQ / 微信 / 邮件、以及每个声明了 `notify.sound` 的插件来源各自一路
- 内置来源**默认全部关闭**，想要哪一路响就单独打开
- 提示音默认用**程序自带的 `Resources/sound.wav`**（设置里可选系统提示音/蜂鸣等，或指定自定义 `.wav`）；
  有全局冷却（默认 3 秒）避免连续轰炸
- 播放统一走 WPF `MediaPlayer`（WASAPI 端点、有独立音频会话）；失败时才降级到 winmm `PlaySound` 与系统提示音
- 设置页「消息提示音 → 试听」可直接验证；调试模式（`WISLAND_DEBUG=1`）会记录
  `MediaPlayer 已打开（x.x 秒）` 与 `播放结束`
- **插件必须声明 `notify.sound` 才可能发声**，未声明的插件来源即使开关打开也不响（宿主记日志）

### 6. 系统通知统一进 Inbox

`island.notify_to_inbox`（默认开）：把 QQ / 微信 / 邮件 的系统通知镜像进 Inbox，
与插件消息共用磁贴、折叠、置顶、点击跳转。

### 7. 快捷启动栏

- 行尾 **`+`**：添加程序 / 文件 / 文件夹（文件夹用 `OpenFolderDialog`）
- **真实图标**：程序与文件取 shell 关联图标，文件夹取文件夹图标并显示**本地化名称**（Desktop → 桌面）
- **右键菜单**：置顶 / 启动参数 / 自定义图标 / 移除
- **拖拽排序**、**拖文件到岛自动展开并添加**、单行容器**滚轮横滑**

### 8. 点击磁贴打开程序

运行中的程序 → **唤出窗口并聚焦**（支持最小化到托盘）；未运行才启动。
按「配置路径 → 运行中进程 → 已知安装路径 → 开始菜单快捷方式 → 卸载注册表 → URI 协议」逐级兜底，
结果记录在 `data/app-launch.log`。

### 9. 插件系统（清单 / 权限 / 页面）

- **清单校验**：`plugin.json` 的 `id` 必须等于目录名、`minHostVersion` 不满足直接拒绝、
  权限名拼错也会拒绝加载，失败原因显示在设置页
- **隔离加载**：每个插件一个可回收 `AssemblyLoadContext`，第三方依赖只在插件目录解析，
  宿主/系统程序集回落到默认上下文（保证契约类型同一份）
- **容错**：`OnLoad` 后台执行 + 8 秒超时，抛异常/超时只标记该插件失败，不影响宿主与其他插件
- **权限守门**：`messages.push` / `ui.settings` / `ui.page` / `ui.compact` / `notify.image`
  未声明则对应调用被拒绝并记日志
- **插件页**：可选注册，图标出现在主导航下方一行（多插件横向滚动），无插件时该行不占高度
- **热重载**：`plugins.hot_reload`（默认开）。`plugins/` 下的 dll / `plugin.json` 变化后
  自动卸载重载（700ms 防抖），插件页与设置分组同步更新；当前页被删则自动退回主页。
  加载走**影子副本**（`data/plugins/.shadow`），源目录文件不被锁定，重新编译后直接覆盖即可
- **手动重载**：设置页「插件设置」顶部有「重新加载全部插件」，
  每个插件卡片的说明右侧有「重载」（只重载该插件）；插件卡片之间用分隔线隔开
- **开发者体验**：宿主托管定时器（卸载自动释放）、插件独立配置与日志、
  设置页可直接打开插件目录/数据目录、失败原因与日志路径一目了然

### 10. 天气城市选择

同名地点极多（搜「朝阳」有 16 个，含国外同名点），而且地级市在地名库里常以「朝阳市」形式存在。
所以添加城市不再是"取第一个结果"：

- 自动补「原名 + 市」再合并去重，按**行政级别 + 人口 + 本国**加权排序
- 多个候选时弹出选择面板：每条显示 **城市 · 省份 · 国家 + 坐标 + 人口**，首条标「推荐」
- 单候选直接添加；也支持直接输入坐标（`30.66,104.06`，容忍全角符号）
- 添加后按坐标 0.05° 去重，chip 显示为 `朝阳市 · 辽宁`，一眼可分辨
- **区县/县级市兜底**：Open-Meteo 的地名库对中文区县覆盖很差（昆山市、常熟市直接 0 条），
  搜不到时自动改用 OSM **Nominatim** 查坐标（免 key，仅在添加城市时请求，遵守 1 次/秒限制）

### 11. 歌词多源

| 顺序 | 源 | 说明 |
| --- | --- | --- |
| 1 | 网易云 | 唯一稳定提供中文翻译行（`tlyric`）的源，优先保住双语歌词 |
| 2 | **LiriQo** | 公开聚合 API（11 源：Apple Music / Musixmatch / Unison / 网易云 / PetitLyrics / 酷狗 / 酷我 / 咪咕 / QQ音乐 / LRCLIB / YouTube Music），按质量排序，响应带原始 LRC / 逐字时间轴 |
| 3 | **LRCLIB** | 开放歌词库，取同步歌词（按标题+歌手搜索、按时长接近度挑选） |
| 4 | 纯文本兜底 | LRCLIB `plainLyrics` / LiriQo `plain`；无时间轴时按播放进度比例估算当前行，界面仍能滚动 |

- 第三方源都带可识别 User-Agent；遇到 `429` 会**冷却 5 分钟**，不影响正常播放
- 切换歌曲只查一次并缓存结果（含"确无歌词"的负缓存）

### 12. 实时字幕（离线 ASR）

翻译页「实时字幕」标签 + 收起态字幕胶囊，整条链路都在 C# 进程内完成，不依赖 Python：

```
音频捕获              人声处理                  电平归一化         VAD 切句              离线识别                    翻译
WASAPI 回环   →   DPDFNet2/GTCRN 降噪    →   -22dBFS       →   自适应噪声底       →   SenseVoice / Whisper   →   多引擎链
或 麦克风        或 VoxLock 目标说话人提取      （增强后放大）      + 静音 450ms           (ONNX / GGML)               (Index/智谱/DeepSeek/Google)
```

| 项 | 说明 |
| --- | --- |
| 音频源 | **系统音频回环**（看视频/游戏）或**麦克风**（会议），设置页切换 |
| 采样 | 统一重采样为 16kHz 单声道（回环原始 48k 立体声 → 下混 + 线性重采样） |
| 引擎选择 | `live.asr.engine`：`auto`（SenseVoice 已下载就优先，否则 Whisper）/ `sensevoice` / `whisper`；SenseVoice 加载失败自动回退 Whisper |
| 模型 | SenseVoice：`sensevoice`（226MB，ONNX；中文/英文快，**日语实测较弱**，日语请用 Whisper）；Whisper：`tiny 75MB` / `base 142MB` / `small 466MB` / `turbo 547MB`（多语，建议 GPU）/ `anime 1.4GB`（仅日语，需 GPU；自动清理句首 S 标记） |
| 模型获取 | **首次点「开始聆听」自动下载**（`hf-mirror.com` 优先、失败回退 HuggingFace）：Range 断点续传、可取消、下载后按 **SHA256**（取不到则按体积）校验；详见 §13 |
| GPU | `live.asr.gpu`：`auto`（Vulkan 优先）/ `off` / `vulkan` / `cuda`；Vulkan 对 AMD/Intel/NVIDIA 都可用，CUDA 需自备运行库（未随包），失败自动回退 CPU |
| 人声处理 | `live.enhance.mode`：`off` / `dpdfnet`（默认，去 BGM/噪声、保留所有人声）/ `gtcrn`（轻量）/ `voxlock`（只保留锁定的第一个说话人，多人对话会丢台词）；`live.enhance.normalize` 把增强后电平拉回 ~-22dBFS；详见 §15 |
| 切句 | 30ms 帧计算 RMS；阈值**自适应环境噪声底**（`max(0.006, 噪声底×2.5)`，可关）；静音 450ms 切句；单句上限 `live.vad.max_segment_ms` 默认 **12000**（可在设置页 2~20s 调） |
| 增益 | 增强后电平归一化（目标约 -22dBFS，上限 12x）；AGC 默认关闭（保留开关）；Whisper 前还有峰值归一化保险 |
| 翻译 | 走「翻译引擎 + 回退链」（见 §14）：默认 Index-Translate 免 Key，可切智谱/DeepSeek/Google/Bing；可整体关闭只显示原文 |
| 界面 | 翻译页「实时字幕」分段：开始 / 停止聆听、实时音量条、清空；收起态为**最近两段字幕卡片**：每段原文在上（小字）、译文在下，上一条半透明 + 当前一条高亮，宽度 640→760、高度 2~4 行，默认常驻到停止聆听（`live.collapsed_hold_s` 可设停留秒数） |
| 延迟参考 | SenseVoice ≈ 0.2~0.5s/句；Whisper turbo + Vulkan ≈ 0.2s/句；纯 CPU Whisper base ≈ 1.3~1.6s/句（从说完到字幕出现，主要花在推理） |

实测（4 核 CPU、系统音频回环播放语音）：
```
[识别] Hello world.
[译文] 你好世界。
```

**识别不准 / 需要很大声时**：设置页 →「实时字幕」可调（改完重启字幕生效）：
`live.enhance.mode` 人声处理（默认 dpdfnet；嘈杂环境可试 gtcrn，单人场景才用 voxlock）、
`live.enhance.normalize` 增强后归一化（默认开）、`live.vad.threshold` 固定阈值下限（默认 0.006，越小越灵敏）、
`live.vad.adaptive` 自适应阈值（默认开）；静音切句阈值（150~1500ms）与最长句时长（2~20s）已做成设置页滑条。旧配置会自动迁移一次（0.012→0.006、切句上限 6s→12s、人声处理默认 DPDFNet）。

### 13. 模型管理（翻译页 →「模型」分段）

模型管理不再占用一个导航页，而是作为翻译页的子分段，与实时字幕放在同一处：

| 功能 | 说明 |
| --- | --- |
| 模型卡片 | 按引擎家族分组：**SenseVoice**（226MB）、**Whisper**（tiny/base/small/turbo/anime）、**人声处理**（DPDFNet2 10MB / GTCRN 0.5MB / VoxLock 74MB）；显示体积、语言、推荐硬件、是否已下载、**使用中** / **已暂停** 徽标 |
| 下载 / 继续 / 重试 | 支持 **HTTP Range 断点续传**：取消、断网或关掉应用后 `.part` 保留，点「继续」从断点接着下，不再从 0 开始 |
| 取消 | 下载中一键中断，已下部分保留（按钮在「取消 / 继续」之间切换） |
| 进度不丢 | 下载任务与进度在 `ModelManager`（进程级服务）里，**切分段 / 切页面 / 重开窗口**都不会丢，回来按真实状态重建进度条 |
| 校验 | 已下载时点「校验」：多文件模型包逐文件检查（GGML 魔数 / ONNX 大小 / tokens 非空）；下载完成后按 **SHA256**（HuggingFace LFS 元数据）比对，不匹配则丢弃重下 |
| 设为默认 | Whisper 模型写入 `live.asr.model` + `live.asr.engine=whisper`；SenseVoice 写入 `live.asr.engine=sensevoice`；下次聆听生效 |
| 删除 / 清除缓存 | **两步确认**（首次点击变「确认删除」）；未下完时按钮是「清除缓存」只删 `.part`；字幕运行中禁用删除并提示先停止聆听 |
| 目录 | 顶部显示 `…\data\models`，可一键打开目录 |
| 自动选择 | 底部说明当前实际引擎与模型：`auto` 引擎优先 SenseVoice；Whisper 的 `auto` 在 ≥8 核用 base、否则 tiny |

下载细节：

- 源：`hf-mirror.com`（优先）→ `huggingface.co`（回退），两源共用同一个 `.part` 续传；
- 断线保护：连续 45s 收不到数据判为超时，保留 `.part`；连接/TLS 握手 20s、响应头 60s 超时（避免代理卡死时永远 0 字节），响应体不设总超时；
- 多文件模型包：SenseVoice 是 `model.int8.onnx` + `tokens.txt`，逐文件续传、全部就位才算下载完成；
- 完整性：下载完成后按 **SHA256** 校验（哈希来自 HuggingFace 仓库 API 的 LFS 元数据，带缓存）；
  取不到哈希时（GitHub Release 资产 / API 不可达）退回"体积校验 + 魔数校验"，并在 `debug.log` 注明未校验哈希；
- 脏文件自愈：哈希 / 魔数 / 大小校验失败时丢弃 `.part` 自动重下一次；
- 实时字幕页的「开始聆听」在下载模型期间会变成「取消下载」，可直接中断。

### 14. 多引擎翻译

翻译不再绑定单一供应商，改成「引擎 + 回退链」：

| 引擎 | Key | 说明 |
| --- | --- | --- |
| `index` | 不需要 | B站 Index-Translate 官方免费 API（35B-A3B，OpenAI 兼容、支持流式），**默认引擎** |
| `zhipu` | 需要 | 智谱 GLM-4-Flash（永久免费模型，手机号注册即可），OpenAI 兼容 |
| `deepseek` | 需要 | 原有的 DeepSeek（保留原有长提示词，流式） |
| `google` | 不需要 | Google 免密钥网页接口，延迟低，适合做兜底 |
| `bing` | 不需要 | Bing 免密钥网页接口（Edge 官方 token 接口已于 2026-07 下线，改用页面抓取） |

- 配置：`translate.engine` 选主引擎，`translate.fallback` 填回退顺序（逗号分隔，默认 `google`）
- 需要 Key 的引擎**没配 Key 会自动跳过**；主引擎失败/超时自动切下一个，全部失败才报错
- 手动翻译页与实时字幕（VAD 切句后逐句翻译）共用同一条引擎链
- 设置 →「翻译设置」里有引擎选择、智谱开通引导、各引擎 Key/模型，以及**测试翻译**按钮（显示耗时与译文）

> 免密钥网页接口（Google / Bing）是非官方通道，可能被限流或随页面改版失效；程序已做失败回退。

### 15. 人声处理（降噪 / 目标说话人提取）

实时字幕链路从「原始音频 → AGC/高通 → VAD」改成「**原始音频 → 人声处理 → 电平归一化 → VAD → ASR**」。

| 后端 | 体积 | 说明 | 适用 |
| --- | --- | --- | --- |
| `dpdfnet` DPDFNet2 | 10 MB | 去背景音乐/噪声，保留**所有人声**；实测强 BGM 下识别明显变好（Whisper base：错误句 → 完全正确） | 影视 / 动画（默认） |
| `gtcrn` | 0.5 MB | 轻量降噪，效果弱于 DPDFNet | 低配设备 |
| `voxlock` | 74 MB | **目标说话人提取**：无音色样本时前 3 秒原样通过，之后只保留锁定的第一个说话人；多人对话会丢其他角色台词 | 单人解说 / 直播 / 通话 |

- 配置：`live.enhance.mode`（off/dpdfnet/gtcrn/voxlock）、`live.enhance.normalize`（增强后电平归一化，默认开）、
  `live.enhance.threads`（默认 2）、`live.enhance.voice_sample`（VoxLock 可选音色样本 wav）
- 降噪会压低电平（实测 -8~18dB），归一化后 VAD 才能正常触发；因此 AGC 默认关闭（链路里保留开关，可手动打开）
- 模型在「翻译 → 模型 → 人声处理」里下载；首次开始聆听也会自动下载当前模式所需模型
- 实测（Win11 / 5800X3D）：DPDFNet RTF ≈ 0.13、GTCRN ≈ 0.07（CPU 2~4 线程），流式处理不拖慢识别

### 16. 常驻性能与可靠性（v0.2.2）

灵动岛是"从开机挂到关机"的程序，这一版把常驻开销与静默失效两类问题集中处理了。

**不由每帧唤醒**

| 做法 | 说明 |
| --- | --- |
| 帧循环空闲即停 | 弹簧全部静止、无悬停/播报动画且不再拖拽时，连续 20 帧后解绑 `CompositionTarget.Rendering`；鼠标进入/离开、展开/收起、开始拖拽、页面重排会重新拉起 |
| 兜底看门狗 | 150ms 检查一次"该动却没醒"的状态（弹簧在动 / 收起态在播字幕歌词 / 有待播提示），避免漏唤醒导致动画停在半路 |
| 只在变化时失效重绘 | 导航栏此前每帧无条件 `InvalidateVisual()`，与主窗口的帧回调构成自激励环，把"静止节流"完全抵消 |
| 绘制缓存 | Typeface 按字重/字体族缓存、颜色画刷按 ARGB 冻结缓存、字宽测量缓存；角标红点不再每帧解析色值字符串 |
| 事件去抖 | 音量电平只在"有订阅者且变化 > 0.4%"时抛事件；亮度只在值变化时通知；通知库没变就整轮跳过（原来是每 4 秒整库复制 + 重解码 200 条 toast） |
| 页面定时器随可见性启停 | 岛收起时主页只是被 `Collapsed`（不触发 `Unloaded`），原来 16ms 宠物动画会一直空转 |
| 文本真实 DPI | 自绘文本从固定 1.0 DIP 改为按窗口 DPI 渲染，高 DPI 屏不再发虚 |

**不会静默失效**

| 做法 | 说明 |
| --- | --- |
| 配置原子写 + 备份 | 临时文件 → 原子替换 → 保留 `.bak`；损坏时保留 `.corrupt-<时间戳>` 并尽力恢复，设置页顶部红条提示 |
| 关键路径不再空 catch | 配置读写、插件加载/热重载、模型下载、退出清理都会写 `data\debug.log`（`WISLAND_DEBUG=1`）或 `crash.log` |
| 退出链逐步隔离 | 每一步单独 try 并记录耗时（>50ms 打印），前一步失败不再跳过后面的保存与释放 |
| 日志轮转 | `debug.log` / `crash.log` / `app-launch.log` 超过 1 MB 自动转成 `*.1.log`，长期运行不再无限增长 |
| 二次启动可唤起 | 命名事件：再次双击图标会唤起已有实例（展开岛），而不是毫无反应地退出 |
| 单元测试与 CI | 29 个 xUnit 用例覆盖配置合并/迁移/损坏恢复、插件清单校验、热键解析、LRC 解析、模型文件校验；GitHub Actions 每次提交跑构建 + 测试 |

## 操作

| 操作 | 效果 |
| --- | --- |
| 悬停 | 展开（回到上次页面） |
| 鼠标离开 | 收起 |
| 展开态按住空白处 0.3 秒拖动 | 收起到指针下方并拖动，松手吸附最近边 |
| 收起态滚轮 | 切换 时钟 / 小形象 / 音乐 视图 |
| 展开态点导航图标 | 切换页面 |
| 翻译页顶部标签 | 在 文本 / 实时字幕 / 单词本 / 模型 之间切换 |
| 实时字幕 → 开始聆听 | 首次会自动下载语音模型（可取消，已下部分可续传） |
| 拖动文件到岛 | 自动展开并加入启动栏 |
| 启动栏内拖动图标 | 排序 |
| 再次双击 `Wisland.exe` | 唤起已有实例并展开岛（不会启动第二个） |
| 热键被占用 | 收起态提示"热键不可用：xxx"，可在设置里换组合键 |

## 配置要点（`config.json`）

| 键 | 说明 |
| --- | --- |
| `island.glass` | `system` / `capture` / `solid` |
| `island.glass_corner` | DWM 模式圆角半径（默认 14，越大越圆） |
| `island.glass_fog` | 四角雾化 0~255（默认 0） |
| `island.notify_style` | `popup` / `ticker` |
| `island.notify_duration_ms` | 每条提示停留时长 |
| `island.notify_to_inbox` | 系统通知是否并入消息页 |
| `island.notify_plugin_anim` | 是否允许插件自定义通知动画 |
| `island.msg_pinned` | 消息磁贴置顶的来源（逗号分隔） |
| `island.hover_delay_ms` | 悬停多久展开（默认 220ms；旧键 `hover_expand_ms` 自动迁移） |
| `island.collapse_delay_ms` | 鼠标离开多久收起（默认 450ms） |
| `clipboard.max_items` | 剪贴板历史保留条数（默认 10，范围 1~200；旧键 `clipboard.max` 自动迁移，改后立即生效） |
| `plugins.enabled` | 是否加载插件（默认 `true`） |
| `plugins.hot_reload` | `plugins/` 变化时自动重载（默认 `true`） |
| `plugins.disabled` | 要禁用的插件 id 列表 |
| `notify.poll_ms` | 通知库轮询间隔（默认 4000；库没变化时会跳过复制与解析） |
| `notify.sound.enabled` | 消息提示音总开关（默认 false） |
| `notify.sound.kind` | `builtin`（自带 sound.wav，默认）/ `asterisk` / `beep` / `exclamation` / `question` / `hand` |
| `notify.sound.file` | 自定义 `.wav` 路径，留空用上面的类型 |
| `notify.sound.cooldown_ms` | 提示音全局冷却（默认 3000） |
| `notify.open.qq` / `wechat` / `mail` | 点磁贴打开的 exe；留空自动探测并回写 |
| `live.models_dir` | 语音模型目录（默认 `models`，相对路径落在 `data\models`） |
| `live.asr.engine` | `auto`（按语言选）/ `sensevoice` / `whisper` |
| `live.asr.model` | `auto` / `sensevoice` / `tiny` / `base` / `small` / `turbo` / `anime` |
| `live.asr.language` | 识别语言：`auto` / `ja` / `en` / `zh` |
| `live.asr.gpu` | `auto` / `vulkan` / `cuda` / `off`（不可用时自动回退 CPU） |
| `live.asr.threads` | 识别线程数（0 = 自动：核心数 - 1） |
| `live.audio.source` | `loopback`（系统音频回环）/ `mic`（麦克风） |
| `live.audio.device` | 麦克风设备序号（`-1` = 系统默认） |
| `live.enhance.mode` | 人声处理：`off` / `dpdfnet`（默认）/ `gtcrn` / `voxlock` |
| `live.enhance.normalize` / `threads` / `voice_sample` | 增强后归一化 / 处理线程数 / VoxLock 音色样本 |
| `live.vad.threshold` / `adaptive` / `silence_ms` / `max_segment_ms` | 切句参数：能量阈值 / 自适应噪声底 / 静音判句（450ms）/ 单句上限（12s） |
| `live.collapsed_hold_s` | 收起态字幕停留秒数（0 = 常驻到停止聆听） |
| `live.translate.enabled` / `target` | 是否翻译字幕、目标语言（默认 `zh-CN`） |
| `translate.engine` / `fallback` | 主翻译引擎 / 回退顺序（默认 `google`，逗号分隔） |

> **配置文件的备份与恢复**：`config.json` 采用原子写（临时文件 + 替换），
> 每次成功落盘都会保留上一版为 `config.json.bak`。
> 如果启动时发现配置损坏，会把坏文件改名保留为 `config.json.corrupt-<时间戳>`，
> 尽力从 `.bak` 恢复，并在设置页顶部显示提示条（含备份路径与「打开配置目录」按钮）。
> 配置目录不可写（例如装在 `Program Files`）时同样会在设置页提示"设置无法保存"。

## 已知问题

1. **毛玻璃的两种取舍都各有一处不完美**
   - DWM（`system`）：实时模糊，但 accent 的作用域是窗口矩形，四角需要用「小圆角矩形」规避（默认 `glass_corner = 14`）；对角形状不是完美胶囊。
   - 抓屏（`capture`）：形状完美，但内容是定格的截图，视频/游戏场景下是几十秒前的画面。
2. **液态形变未移植**：液桥、拉丝、贴边内凹圆角是逐帧几何建模，目前是简化版（弹簧缩放 + 按压挤压）。
3. **实时字幕的延迟取决于 CPU / 引擎 / 模型**：SenseVoice（中文等）约 0.2~0.5s/句；
   Whisper turbo + Vulkan ≈ 0.2s/句；纯 CPU Whisper base ≈ 1.3~1.6s/句。日语建议 `small` 或 `turbo`。
4. **插件为同进程加载**：权限是"声明 + 守门"而非真沙箱，装插件等于信任作者；进程隔离在路线图中。
   v0.2.2 收窄了实现与声明的差距（消息来源白名单、`ServiceHub` 不可写），但**插件订阅的宿主事件无法在卸载时回收**，
   频繁热重载会逐步累积少量内存（已把全量 GC 移到后台线程，不再卡 UI）。
5. **示例插件是示范性质的实用工具**：股票数据来自东方财富公开接口（仅供参考、不构成投资建议，接口有频率限制，
   插件已做多域名切换 + 失败冷却 + 磁盘缓存 + 3 天过期）；Clash 插件需要客户端开启外部控制器（或命名管道）。
6. **收起态提示的自定义动画/图片只承载最新一条**（多条同时到达时，前面的消息只有文字）。
7. 设置页在极少数 DPI 缩放下卡片可能轻微错位（未复现到稳定案例）。
8. ⚠️**目前不要使用浅色主题**，目前未做浅色主题底板适配。
9. **歌词的第三方源是公益服务**：LiriQo（公开聚合 API）与 LRCLIB 都可能限流或调整行为；
   两者也**不保证中文翻译行**（翻译目前只有网易云稳定提供），命中第三方源时歌词会变成单语。
   程序已做 5 分钟冷却与负缓存，失效时只是退回"没有歌词"，不影响播放。
10. **区县/县级市的坐标来自 Nominatim（OSM）**，是行政区中心点，天气与该区县具体位置的
    实际天气可能有几公里偏差（Open-Meteo 原有的地名库对这些地名覆盖率很低）。
11. **语音模型下载受网络影响很大**：`hf-mirror.com` 首次建连有时要 15–20 秒才开始吐数据
    （这段时间进度停在 0%，不是卡死），之后通常 1–2 MB/s；连续 45 秒收不到数据会判为停滞，
    保留 `.part` 供下次续传。首次「开始聆听」会自动下载 75 MB ~ 1.4 GB 的模型（按引擎）。
12. **源站不支持 Range 时会从头下载**（少见）：响应是 200 而不是 206 时 `.part` 会被覆盖重写，
    进度会短暂回退到 0% 再往前，属于正常降级。
13. **模型哈希校验是"尽力而为"**：SHA256 来自 HuggingFace 的 LFS 元数据，取不到时（GitHub Release 资产、
    API 不可达）只做体积校验并在 `debug.log` 注明；不会因此拒绝下载。
14. **跨屏拖动时的 DPI 重排不完整**：v0.2.2 让文本用真实 DPI、贴边取"窗口所在显示器"，
    但把一个已展开的岛从 100% 屏拖到 150% 屏，布局要等一次重新展开才完全正确。
15. **托盘图标**：退出时会主动删除（v0.2.2 修复），若被任务管理器强杀仍可能残留一枚图标，鼠标划过即消失。
16. **anime-whisper 暂不推荐**：幻听较多、偶尔把日语识别成英文；关闭「清理句首 S」也不解决质量问题。
17. **VoxLock 只保留一个说话人**、**SenseVoice 日语较弱**：多人对话/日语场景分别改用 Whisper（`small` / `turbo`）。
18. **部分 AMD 显卡驱动在长时间 Vulkan 场景下可能报错**：把 `live.asr.gpu` 设为 `off`，或改用 `small` 模型。
19. **免密钥翻译（Google / Bing）是非官方接口**：可能随页面改版失效或被限流，程序已做失败回退。

## 版本历史

| 版本 | 重点 |
| --- | --- |
| **v0.2.2** | 稳定性 / 性能 / 数据安全整修（7 个批次，详见[当前版本](#当前版本v022)）：剪贴板设置失效、托盘幽灵图标、字幕幽灵运行、退出卡 3 秒与闪退、磁贴点击冻结、配置原子写与损坏恢复、通知库增量读取、帧循环空闲即停、插件来源白名单、模型哈希校验；新增 `.sln` / 29 个单测 / CI |
| v0.2.1 | 实时字幕落地：纯 C# 离线识别（SenseVoice / Whisper）、人声处理（DPDFNet2 / GTCRN / VoxLock）、Vulkan/CUDA 加速、语音模型按家族管理与断点续传、多引擎翻译与回退 |
| v0.2.0 | 插件系统成熟（清单校验 / 隔离加载 / 权限守门 / 插件页 / 热重载）、消息提示音、示例插件随包可选、FF14 三件套 |
| v0.1.x | 首个可用版本：灵动岛窗口与展开收起、媒体控制、天气、剪贴板、翻译页、托盘与热键 |

> 逐版本变更细节见 [Releases](https://github.com/Heacter233/wisland-csharp/releases)。

## 构建与测试

需要 **.NET 8 SDK**，Windows 10 1809+ / Windows 11。

```bash
dotnet build Wisland.sln          # 整个解决方案：宿主 + 6 个示例插件 + 测试
dotnet build WislandCSharp/Wisland.csproj -c Debug   # 只构建宿主
```

产物在 `WislandCSharp\bin\Debug\net8.0-windows10.0.19041.0\Wisland.exe`。

单元测试（xUnit，覆盖配置合并/迁移/损坏恢复、插件清单校验、热键解析、LRC 解析、模型文件校验）：

```bash
dotnet test Tests/Wisland.Tests.csproj
```

- CI：`.github/workflows/ci.yml`（windows-latest：还原 → 构建整个解决方案 → 跑测试）
- 分析器：宿主工程开了 `EnableNETAnalyzers` + `latest-recommended`，规则级别在 `.editorconfig` 里逐条调整
  （默认只报 suggestion/warning，不阻断构建）

发布（与 Releases 一致）：

```bash
# 1) 轻量版（需装 .NET 8 桌面运行库）
dotnet publish WislandCSharp\Wisland.csproj -c Release -r win-x64 --self-contained false ^
  -p:DebugType=None -p:DebugSymbols=false -o publish\slim

# 2) 自包含单文件版（解压即用）
#    IncludeAllContentForSelfExtract 必须开：否则 Resources\1002.ico / sound.wav 不会随包
dotnet publish WislandCSharp\Wisland.csproj -c Release -r win-x64 --self-contained true ^
  -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true ^
  -p:IncludeAllContentForSelfExtract=true -p:EnableCompressionInSingleFile=true ^
  -p:DebugType=None -p:DebugSymbols=false -o publish\portable

# 3) 组装发行包：把插件拷进 plugins\<id>\，再压缩
#    普通版       = plugins\stock + plugins\clash
#    FF14 专版    = 普通版 + plugins\ff14-recruit + ff14-price + ff14-treasure
#    四个包 = {slim, portable} × {普通版, FF14 专版}
```

> 单文件自包含版会把 `Resources` 解包到临时目录，因此程序内部统一用
> `AppPaths.BaseDir`（取 `Environment.ProcessPath` 的真实目录）来定位 `config.json` / `data\` / `plugins\`，
> 保证配置与插件始终写在 exe 旁边，不会因为临时目录被清理而丢失。

## 目录结构

```
Wisland.sln                  解决方案：宿主 + 示例插件 + 测试
Tests/                       单元测试（xUnit）
.github/workflows/ci.yml      GitHub Actions：构建 + 测试
.editorconfig                代码风格与分析器规则
WislandCSharp/
├─ MainWindow.xaml(.cs)      主窗口：玻璃/窗口几何/展开收起/插件页行/拖放/热键
├─ Controls/
│  ├─ IslandChrome.cs        灵动岛本体：绘制、弹簧、贴边、提示绘制
│  ├─ CompactView.cs         收起态内容（时钟/小形象/音乐）
│  ├─ Backdrop.cs            抓屏模糊（capture 模式）
│  └─ ScrollHost.cs          自定义滚动容器
├─ Plugins/                  插件系统
│  ├─ PluginManifest.cs      plugin.json 模型与校验
│  ├─ PluginApi.cs           契约：IWislandPlugin / IPluginContext / 权限常量
│  ├─ PluginHostContext.cs   单插件上下文（配置、日志、权限守门、托管定时器）
│  └─ PluginLoader.cs        隔离加载、版本校验、异常隔离、卸载
├─ Pages/                    主页 / 消息 / 天气 / 翻译（文本·实时字幕·单词本·模型）/ 剪贴板 / 控制 / 设置
├─ Services/
│  ├─ MessageInbox.cs        插件消息收件箱（Push / 已读 / 置顶）
│  ├─ AppLauncher.cs         点磁贴打开程序的多级兜底
│  ├─ WindowActivator.cs     唤出并聚焦已运行程序的窗口
│  ├─ ShellIcons.cs          程序/文件/文件夹图标 + 本地化名称
│  ├─ NotifyService.cs       系统通知（wpndatabase.db，增量读取）
│  ├─ LiveService.cs         实时字幕管线：捕获 → 人声处理 → VAD → ASR → 翻译
│  ├─ AsrService.cs          识别引擎选择与加载（SenseVoice / Whisper）
│  ├─ AudioCapture.cs        WASAPI 回环 / 麦克风 → 16k 单声道
│  ├─ AudioEnhancer.cs       DPDFNet2 / GTCRN / VoxLock 人声处理
│  ├─ ModelDownloader.cs     Range 续传 + 停滞看门狗 + 哈希校验
│  ├─ ModelHashes.cs         从 HuggingFace LFS 元数据取 SHA256
│  ├─ ModelManager.cs        模型下载状态与任务（跨页面存活）
│  ├─ AtomicFile.cs          原子写 + .bak + 损坏隔离（配置/历史/插件配置共用）
│  ├─ RotatingLog.cs         日志轮转（1 MB 转存 *.1.log）
│  └─ ...
└─ Theme/                    配色与样式

samples/                     插件范例（见 samples/README.md）
├─ hello-plugin/             最小骨架
├─ stock-plugin/             股票行情
├─ clash-plugin/             Clash 控制（HTTP + 命名管道）
├─ ff14-recruit-plugin/      FF14 招募监控（监控 + 搜索页）
├─ ff14-price-plugin/        FF14 物品价格（离线词典 + Universalis）
├─ ff14-treasure-plugin/     FF14 挖宝悬浮窗（ACT WebSocket）
└─ mock/                     Clash 测试替身
```

## 授权

MIT。原项目 [AsamiAkito/wisland](https://github.com/AsamiAkito/wisland) 同为 MIT，本仓库保留同样的授权。
