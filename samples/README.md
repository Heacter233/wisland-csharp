# 示例插件

这些插件既是**能直接用的功能**，也是**插件开发规范的活样板**。
规范全文见 [`docs/plugins.md`](../docs/plugins.md)，清单 Schema 见 [`docs/plugin.schema.json`](../docs/plugin.schema.json)。

| 目录 | 插件 | 演示的内容 |
| --- | --- | --- |
| [`hello-plugin`](hello-plugin) | 示例插件 | 最小骨架：注册设置面板、注册页面、推送消息、宿主托管定时器 |
| [`stock-plugin`](stock-plugin) | 股票行情 | 定时拉取网络数据、磁盘缓存、涨跌提醒、自绘走势图（`OnRender`）、列表增删 |
| [`clash-plugin`](clash-plugin) | Clash 控制 | 只访问本机（`network.local`）、HTTP + 命名管道双通道、分段控件、可点击标签 |
| [`mock`](mock) | 测试替身 | 无 Clash 环境下验证 Clash 插件：HTTP mock 与命名管道 mock |

## 构建与安装

```bash
# 1) 构建插件（会引用宿主工程以拿到契约类型）
cd samples/stock-plugin
dotnet build -c Debug

# 2) 复制到宿主的 plugins/<id>/ 目录
#    <程序目录>/plugins/stock/StockPlugin.dll
#    <程序目录>/plugins/stock/plugin.json
```

`csproj` 里的 `ProjectReference ... Private="false"` 很关键：
它让插件编译时能引用 `Wisland.Plugins` 契约，但**不会**把 `Wisland.exe` 复制进插件目录
（运行时由宿主提供，保证契约类型是同一份）。

> 目录名必须等于 `plugin.json` 里的 `id`，否则宿主会拒绝加载并给出原因。

## 验证 Clash 插件（无需真的跑 Clash）

```bash
# HTTP 控制器 mock
python samples/mock/mock_clash.py 19090
# 然后在插件设置里把端口改成 19090

# 或命名管道 mock
cd samples/mock/PipeMockClash && dotnet run -- verge-mihomo-test
# 插件设置里把连接方式切到「命名管道」，管道名填 verge-mihomo-test
```

## 注意

这两个插件是**示例性质的实用工具**，不是精雕细琢的产品：

- 股票数据来自东方财富公开接口，**仅供参考，不构成投资建议**；接口对高频请求有限流，
  插件已做多域名切换 + 失败冷却 + 磁盘缓存，但长时间高频轮询仍可能被拒。
- Clash 插件通过外部控制器（`external-controller`）操作，需要客户端开启该功能；
  Clash Verge 默认 `127.0.0.1:9097`，也可能只开命名管道 `\\.\pipe\verge-mihomo`。
- 两者都遵循同一套约束：不碰宿主窗口/配置、不自建 Window、定时器 ≥100ms、
  改完视觉树调用 `ctx.RequestRepaint()`。
