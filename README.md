# Wisland · C# 重写版

用 **C# / .NET 8 + WPF** 重写 [AsamiAkito/wisland](https://github.com/AsamiAkito/wisland)（原版：Python + PySide6，MIT）的 Windows 桌面「液态灵动岛」。

<p align="center">
<img src="docs/compact.png" height="90">&nbsp;&nbsp;
<img src="docs/expanded.png" width="420">
</p>

> **当前版本 v0.2.1**（[Releases](https://github.com/Heacter233/wisland-csharp/releases/tag/v0.2.1) 安装包）。
> 作者 **Heacter**；程序属性中的公司/作者信息见 exe 属性页。
>
> 本版重点：**实时字幕**（纯 C# 离线识别 + 人声处理 + 多引擎翻译）、**翻译多引擎与自动回退**、
> **Vulkan GPU 加速**、**语音模型按需下载与管理**，以及长时间运行的稳定性修复。完整说明见[当前版本](#当前版本v021)。

## 下载

到 [Releases](https://github.com/Heacter233/wisland-csharp/releases/tag/v0.2.1) 下载 v0.2.1，按「运行库」和「插件版本」两个维度选一个包：

| 包 | 大小 | 运行库 | 附带插件 |
| --- | --- | --- | --- |
| `wisland-csharp-0.2.1-slim-x64.zip` | ~34.7 MB | 需要 [.NET 8 桌面运行时](https://dotnet.microsoft.com/download/dotnet/8.0) | 普通版：股票、Clash 控制 |
| `wisland-csharp-0.2.1-portable-x64.zip` | ~97.3 MB | **自带 .NET 8 运行库，解压即用** | 普通版：股票、Clash 控制 |
| `wisland-csharp-0.2.1-ff14-slim-x64.zip` | ~34.9 MB | 需要 .NET 8 桌面运行时 | FF14 专版：股票、Clash + **FF14 招募监控 / 物品价格 / 挖宝悬浮窗** |
| `wisland-csharp-0.2.1-ff14-portable-x64.zip` | ~97.5 MB | **自带 .NET 8 运行库，解压即用** | FF14 专版：股票、Clash + **FF14 招募监控 / 物品价格 / 挖宝悬浮窗** |

> 包体比 v0.2.0 大（slim 约 7.8 MB → 34.7 MB），因为内置了 Whisper 的 **Vulkan GPU 运行时**（约 55 MB）
> 与 sherpa-onnx 的 ONNX 运行时；没有可用 GPU 时会自动回退 CPU，不影响功能。

双击 `Wisland.exe` 即可。首次运行会在 exe 同目录生成 `config.json` 与 `data\`；
自带插件在 `plugins\<id>\`，可以在设置页「插件设置」里查看/重载，不需要时直接删掉对应目录。
卸载直接删除文件夹（程序不写注册表，仅「开机自启」开关会登记启动项，可在设置页关闭）。

> **关于「未知发布者」**：本项目没有购买商业代码签名证书，Windows 可能提示「未知发布者」或被 SmartScreen 拦截。
> 程序以普通用户权限运行（`asInvoker`，不会请求管理员权限），exe 属性里的作者信息为 **Heacter**。
> 处理方式：SmartScreen 提示时点「更多信息 → 仍要运行」；从浏览器下载的解压后右键 `Wisland.exe` → 属性 →
> 勾选「解除锁定(Unblock)」→ 确定。

## 当前版本：v0.2.1

> 发布日期：2026-11-20 · 对应 tag `v0.2.1` · 相对 v0.2.0 的变化见 [Releases 说明](https://github.com/Heacter233/wisland-csharp/releases/tag/v0.2.1)

这一版把「实时字幕 / 语音识别 / 人声处理 / 翻译」做成了完整能力，并补上 GPU 加速、模型管理和一批长跑稳定性修复。
更早版本（v0.2.0 及以前）的变化请直接看 Releases 页面，这里只描述**当前版本能做什么**。

### 实时字幕（翻译页 →「实时字幕」）

整条链路都在 C# 进程内完成，不依赖 Python：

```
WASAPI 回环/麦克风 → 人声处理（DPDFNet2 / GTCRN / VoxLock）→ 电平归一化 → VAD 切句 → 离线 ASR（SenseVoice / Whisper）→ 多引擎翻译
```

| 能力 | 说明 |
| --- | --- |
| 音频源 | 系统音频回环（看视频/游戏）或麦克风，设置页切换 |
| 人声处理 | 默认 **DPDFNet2 降噪**（去 BGM/噪声、保留所有人声）；可选 GTCRN（轻量）/ VoxLock（目标说话人提取，仅单人场景） |
| VAD 切句 | 自适应噪声底阈值；静音阈值与最长句时长都可在设置页用滑条调整（默认 450ms / 12s） |
| 识别引擎 | `auto` 按语言选择：中文/英文/韩语/粤语 → **SenseVoice-Small**（CPU 约 0.2~0.5s/句）；日语/自动 → **Whisper** |
| Whisper 模型 | `tiny` / `base` / `small` / `turbo`（多语，需 GPU 体验更佳）/ `anime`（日语动画特化，**暂不推荐**） |
| GPU 加速 | Whisper 支持 **Vulkan**（AMD/Intel/NVIDIA，实测约 7 倍加速）与 **CUDA**（需自备运行库，缺库自动回退）；失败自动回退 CPU |
| 翻译 | 与文本翻译共用多引擎链（见下节），可整体关闭只显示原文 |
| 收起态显示 | 收起后在岛的下方显示**最近两段字幕**（上一条半透明 + 当前一条高亮，每段原文在上/译文在下），默认常驻到停止聆听（`live.collapsed_hold_s` 可设停留秒数） |
| 延迟参考 | SenseVoice ≈ 0.2~0.5s/句；Whisper turbo + Vulkan ≈ 0.2s/句；纯 CPU Whisper base ≈ 1.3~1.6s/句 |

### 翻译引擎（文本翻译 + 实时字幕共用）

| 引擎 | Key | 说明 |
| --- | --- | --- |
| Index-Translate（默认） | 不需要 | B站官方免费 API，35B-A3B，OpenAI 兼容、流式 |
| 智谱 GLM-4-Flash | 需要（免费模型） | OpenAI 兼容；手机号注册即可，设置页有开通引导 |
| DeepSeek | 需要 | 保留原有长提示词 |
| Google / Bing | 不需要 | 免密钥网页接口（非官方，可能限流/失效，已做失败回退） |

- 配置：`translate.engine` 选主引擎，`translate.fallback` 填回退顺序（默认 `google`）；没配 Key 的引擎自动跳过
- GLM 偶尔会输出「名词解释/例句」：提示词已收紧，且检测到解释性内容会自动换下一个引擎
- 流式输出；设置页有「测试翻译」按钮（显示耗时与译文）

### 语音模型（翻译页 →「模型」，按家族分组）

| 家族 | 模型 | 体积 | 说明 |
| --- | --- | --- | --- |
| SenseVoice | `sensevoice` | 226 MB | CPU 极快，中文/英文/韩语/粤语强；**日语实测较弱**，日语请用 Whisper |
| Whisper | `tiny` / `base` / `small` | 75 / 142 / 466 MB | 通用多语；日语推荐 `small` 或 `turbo` |
| Whisper | `turbo` | 547 MB | large-v3-turbo 量化版，多语质量好，建议 GPU |
| Whisper | `anime` | 1.4 GB | 日语动画特化；**实测幻听严重、偶把日语识别成英文，暂不推荐** |
| 人声处理 | `dpdfnet2` / `gtcrn` / `voxlock` | 10 / 0.5 / 74 MB | 降噪 / 轻量降噪 / 目标说话人提取（单人） |

- 全部支持：**按需下载、HTTP Range 断点续传、取消、校验、设为默认、删除**
- 模型下载状态放在服务层，切分段/切页面/重开窗口不丢进度；首次开始聆听会自动下载当前引擎所需模型
- 已加载的模型在停止聆听后**保留复用**（再次开始约 0.3s，不再反复创建/销毁 GPU 设备）

### 系统要求

- Windows 10 19041+ / Windows 11，x64
- slim 包需要 [.NET 8 桌面运行时](https://dotnet.microsoft.com/download/dotnet/8.0)；portable 包自带
- 实时字幕：GPU 可选（Vulkan/CUDA 不可用时自动 CPU）；模型占用约 0.2~2 GB 磁盘（按下载的模型）
- 首次运行会下载 WebView 无关的字体（使用系统字体）；会联网获取天气/歌词/翻译

### 已知限制

- **anime-whisper 暂不推荐**：幻听多、偶尔把日语识别成英文；可关闭「清理句首 S」仍不解决质量问题
- **VoxLock 只保留一个说话人**：多人对话会丢其他角色台词，仅适合单人解说/直播/通话
- **SenseVoice 日语弱**：日语场景请选 Whisper（small/turbo）
- 免密钥翻译（Google/Bing）是非官方接口，可能随页面改版失效
- 未购买代码签名证书：可能提示「未知发布者」或被 SmartScreen 拦截（处理方式见下载说明）
- 部分 AMD 显卡驱动在长时间 Vulkan 场景下可能报错；可把 `live.asr.gpu` 设为 `off` 或改用 `small` 模型

### 更早版本

- **v0.2.0**：插件系统成熟（清单校验 / 隔离加载 / 权限守门 / 插件页 / 热重载）、消息提示音、5 个示例插件随包可选，外加 FF14 三件套
- **v0.1.x**：首个可用版本（基础岛、媒体、天气、剪贴板、翻译页）

## 插件开发

插件放在 `plugins/<id>/`（`plugin.json` + 程序集），宿主启动时校验清单、按插件隔离加载，
并在设置页展示状态、权限与插件自带的设置面板。

插件可选择性地注册**自己的页面**：图标会出现在主导航下方的独立一行（插件多时横向滚动），
不注册页面的插件不会占用这一行，其他元素位置保持不变。

- 完整规范（清单字段、权限表、禁止事项、界面写法）：**[docs/plugins.md](docs/plugins.md)**
- 清单 JSON Schema：`docs/plugin.schema.json`
- 示例插件：[`samples/`](samples) —— 三个可运行样板 + Clash 测试替身

| 示例 | 能力 |
| --- | --- |
| [`samples/hello-plugin`](samples/hello-plugin) | 最小骨架：设置面板 / 插件页 / 消息推送 / 宿主定时器 |
| [`samples/stock-plugin`](samples/stock-plugin) | 真实第三方数据：自选股增删、大盘指数、分时与日 K 迷你图、涨跌提醒、多域名切换 + 磁盘缓存 |
| [`samples/clash-plugin`](samples/clash-plugin) | 本机服务控制：代理模式切换、策略组节点切换，**HTTP 与命名管道双通道**（自动回退） |
| [`samples/ff14-recruit-plugin`](samples/ff14-recruit-plugin) | FF14 招募监控：关键词轮换搜索、快照差分去重、屏蔽名单/屏蔽词/休息时段、卡片列表、自定义搜索页 |
| [`samples/ff14-price-plugin`](samples/ff14-price-plugin) | FF14 物品价格：离线物品词典（17k 条）、Universalis 市场查询、汇总/明细双表、筛选与常用词 |
| [`samples/ff14-treasure-plugin`](samples/ff14-treasure-plugin) | FF14 挖宝悬浮窗：连 ACT 挖宝会计 WebSocket、自绘宝箱成功/失败图案、出货合并通知 |
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
| 实时字幕 | ✅ | 纯 C# 进程内管线：WASAPI 回环 / 麦克风 → AGC → VAD 切句 → **SenseVoice（默认优先）/ Whisper** → 翻译；Whisper 支持 Vulkan/CUDA 加速，模型管理支持取消与断点续传 |
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
| **引擎选择** | `live.asr.engine`：`auto`（SenseVoice 已下载就优先，否则 Whisper）/ `sensevoice` / `whisper`；SenseVoice 加载失败自动回退 Whisper |
| **模型** | SenseVoice：`sensevoice`（226MB，ONNX；中文/英文快，**日语实测较弱**，日语请用 Whisper）；Whisper：`tiny 75MB` / `base 142MB` / `small 466MB` / `turbo 547MB`（多语，需 GPU）/ `anime 1.4GB`（仅日语，需 GPU；自动清理句首 S 标记） |
| **GPU** | `live.asr.gpu`：`auto`（Vulkan 优先）/ `off` / `vulkan` / `cuda`；Vulkan 对 AMD/Intel/NVIDIA 都可用，CUDA 需自备运行库（未随包），失败自动回退 CPU |
| **人声处理** | `live.enhance.mode`：`off` / `dpdfnet`（推荐，去 BGM/噪声、保留所有人声）/ `gtcrn`（轻量）/ `voxlock`（只保留锁定的第一个说话人，多人对话会丢台词）；`live.enhance.normalize` 把增强后电平拉回 ~-22dBFS |
| **切句上限** | `live.vad.max_segment_ms` 默认 **12000**（原 6000；长句不再被截断） |
| **人声处理** | 默认 **DPDFNet2 降噪**（去 BGM/噪声、保留所有人声），可选 GTCRN / VoxLock；见 §15 |
| **增益** | 增强后 **电平归一化**（目标约 -22dBFS，上限 12x）；AGC 默认关闭（保留开关）；Whisper 前还有峰值归一化保险 |
| 切句 | 30ms 帧计算 RMS；**阈值自适应环境噪声底**（`max(0.006, 噪声底×2.5)`），静音 450ms 即切句，单句最长 6s |
| 识别 | Whisper.net + GGML 模型，可选 `tiny 75MB / base 142MB / small 466MB`，「自动」按 CPU 核心数选择 |
| 模型获取 | **首次点「开始聆听」自动下载**（优先 `hf-mirror.com`，失败回退 HuggingFace），带进度与 GGML 魔数校验；支持取消与断点续传，详见 §13 |
| 翻译 | 走「翻译引擎 + 回退链」（见 §14）：默认 Index-Translate 免 Key，可切智谱/DeepSeek/Google/Bing；可整体关闭只显示原文 |
| 界面 | 翻译页「实时字幕」分段：开始 / 停止聆听、实时音量条、清空；收起态为**最近两段字幕卡片**：每段原文在上（小字）、译文在下，上一条半透明 + 当前一条高亮，宽度 640→760、高度 2~4 行，默认常驻到停止聆听（`live.collapsed_hold_s` 可设停留秒数） |

实测（4 核 CPU、tiny 模型、系统音频回环播放语音）：
```
[识别] Hello world.
[译文] 你好世界。
```
延迟参考：tiny ≈ 2s / base ≈ 4s（从句子说完到字幕出现，主要花在 Whisper 推理上）。

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
| 校验 | 已下载时点「校验」：多文件模型包逐文件检查（GGML 魔数 / ONNX 大小 / tokens 非空） |
| 设为默认 | Whisper 模型写入 `live.asr.model` + `live.asr.engine=whisper`；SenseVoice 写入 `live.asr.engine=sensevoice`；下次聆听生效 |
| 删除 / 清除缓存 | **两步确认**（首次点击变「确认删除」）；未下完时按钮是「清除缓存」只删 `.part`；字幕运行中禁用删除并提示先停止聆听 |
| 目录 | 顶部显示 `…\data\models`，可一键打开目录 |
| 自动选择 | 底部说明当前实际引擎与模型：`auto` 引擎优先 SenseVoice；Whisper 的 `auto` 在 ≥8 核用 base、否则 tiny |

下载细节：

- 源：`hf-mirror.com`（优先）→ `huggingface.co`（回退），两源共用同一个 `.part` 续传；
- 断线保护：连续 45s 收不到数据判为超时，保留 `.part`；连接/TLS 握手 20s、响应头 60s 超时（避免代理卡死时永远 0 字节），响应体不设总超时；
- 多文件模型包：SenseVoice 是 `model.int8.onnx` + `tokens.txt`，逐文件续传、全部就位才算下载完成；
- 脏文件自愈：魔数/大小校验失败时丢弃 `.part` 自动重下一次；
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
| `plugins.enabled` | 是否加载插件（默认 `true`） |
| `plugins.hot_reload` | `plugins/` 变化时自动重载（默认 `true`） |
| `plugins.disabled` | 要禁用的插件 id 列表 |
| `notify.sound.enabled` | 消息提示音总开关（默认 false） |
| `notify.sound.kind` | `builtin`（自带 sound.wav，默认）/ `asterisk` / `beep` / `exclamation` / `question` / `hand` |
| `notify.sound.file` | 自定义 `.wav` 路径，留空用上面的类型 |
| `notify.sound.cooldown_ms` | 提示音全局冷却（默认 3000） |
| `notify.open.qq` / `wechat` / `mail` | 点磁贴打开的 exe；留空自动探测并回写 |
| `live.models_dir` | 语音模型目录（默认 `models`，相对路径落在 `data\models`） |
| `live.asr.model` | `auto` / `tiny` / `base` / `small`；`auto` 在 ≥8 核用 base、否则 tiny |
| `live.asr.language` | 识别语言：`auto` / `ja` / `en` / `zh` |
| `live.audio.source` | `loopback`（系统音频回环）/ `mic`（麦克风） |
| `live.audio.device` | 麦克风设备序号（`-1` = 系统默认） |
| `live.vad.threshold` / `silence_ms` / `min_speech_ms` / `max_segment_ms` | 切句参数：能量阈值（0.012）/ 静音判句（450ms）/ 最短语音（300ms）/ 单句上限（6000ms） |
| `live.translate.enabled` / `target` | 是否翻译字幕、目标语言（默认 `zh-CN`） |

## 已知问题

1. **毛玻璃的两种取舍都各有一处不完美**
   - DWM（`system`）：实时模糊，但 accent 的作用域是窗口矩形，四角需要用「小圆角矩形」规避（默认 `glass_corner = 14`）；对角形状不是完美胶囊。
   - 抓屏（`capture`）：形状完美，但内容是定格的截图，视频/游戏场景下是几十秒前的画面。
2. **液态形变未移植**：液桥、拉丝、贴边内凹圆角是逐帧几何建模，目前是简化版（弹簧缩放 + 按压挤压）。
3. **实时字幕的延迟取决于 CPU 与模型**：4 核实测 tiny 约 2s / base 约 4s（每句切完到出字幕），
   模型名选「自动」会按核心数挑选（≥8 核用 base，否则 tiny）；日语建议 base 以上。
4. **插件为同进程加载**：权限是"声明 + 守门"而非真沙箱，装插件等于信任作者；进程隔离在路线图中。
5. **示例插件是示范性质的实用工具**：股票数据来自东方财富公开接口（仅供参考、不构成投资建议，接口有频率限制，
   插件已做多域名切换 + 失败冷却 + 磁盘缓存）；Clash 插件需要客户端开启外部控制器（或命名管道）。
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
    保留 `.part` 供下次续传。首次「开始聆听」会自动下载 75–466 MB 的模型。
12. **源站不支持 Range 时会从头下载**（少见）：响应是 200 而不是 206 时 `.part` 会被覆盖重写，
    进度会短暂回退到 0% 再往前，属于正常降级。

## 构建

需要 **.NET 8 SDK**，Windows 10 1809+ / Windows 11。

```bash
cd WislandCSharp
dotnet build -c Debug
```

产物在 `WislandCSharp\bin\Debug\net8.0-windows10.0.19041.0\Wisland.exe`。

发布（与 Releases 一致）：

```bash
# 1) 轻量版（需装 .NET 8 桌面运行时）
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
│  ├─ NotifyService.cs       系统通知（wpndatabase.db）
│  ├─ LiveService.cs         实时字幕管线：捕获 → VAD → Whisper → 翻译
│  ├─ AudioCapture.cs        WASAPI 回环 / 麦克风 → 16k 单声道
│  ├─ WhisperAsr.cs          离线识别 + 模型下载（Range 续传 / 校验）
│  ├─ ModelManager.cs        模型下载状态与任务（跨页面存活）
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
