# 示例插件

这些插件既是**能直接用的功能**，也是**插件开发规范的活样板**。
规范全文见 [`docs/plugins.md`](../docs/plugins.md)，清单 Schema 见 [`docs/plugin.schema.json`](../docs/plugin.schema.json)。

| 目录 | 插件 | 演示的内容 |
| --- | --- | --- |
| [`hello-plugin`](hello-plugin) | 示例插件 | 最小骨架：注册设置面板、注册页面、推送消息、宿主托管定时器 |
| [`stock-plugin`](stock-plugin) | 股票行情 | 定时拉取网络数据、磁盘缓存、涨跌提醒、自绘走势图（`OnRender`）、列表增删 |
| [`clash-plugin`](clash-plugin) | Clash 控制 | 只访问本机（`network.local`）、HTTP + 命名管道双通道、分段控件、可点击标签 |
| [`ff14-recruit-plugin`](ff14-recruit-plugin) | FF14 招募监控 | 关键词轮换轮询、快照差分去重、屏蔽名单/屏蔽词/休息时段、本地静态表（CSV/JSON）、卡片式结果列表、第二个自定义搜索页、`notify.sound` 提示音权限 |
| [`ff14-price-plugin`](ff14-price-plugin) | FF14 物品价格 | 离线物品词典（CSV→TSV 生成）、Universalis 市场查询、汇总/明细双 DataGrid、筛选与常用词 |
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

**只复制插件需要的东西**：插件自己的 dll + `plugin.json` +（如有）`data/` 与自己的第三方依赖。
构建输出里出现 `Wisland.exe` / `System.*.dll` / `Microsoft.*.dll` / `WinRT.Runtime.dll` 时**不要**拷进插件目录
（宿主已有，重复拷贝会让加载器解析到错误副本）。

## FF14 招募监控插件

```bash
cd samples/ff14-recruit-plugin
dotnet build -c Debug

# 安装（目录名必须等于 id：ff14-recruit）
<程序目录>/plugins/ff14-recruit/plugin.json
<程序目录>/plugins/ff14-recruit/Ff14RecruitPlugin.dll
<程序目录>/plugins/ff14-recruit/data/*.json,*.csv
```

- 静态表随插件安装：`data/category_map.json`、`worlds.json`、`ContentFinderCondition.csv`、`duty_override.csv`
  （后两个原本是 GB2312 编码，已转成 UTF-8；改完可在插件设置里点「重载数据表」即时生效）
- 运行时数据在 `data/plugins/ff14-recruit/`：`config.json`、`snapshot.json`、`banlist.json`、`hits.json`
- 迁移自 NapCat 插件 `napcat-plugin-ff14-recruit`（同一作者），招募数据来自 `xivpf.littlenightmare.top` 公开接口
- 推送的消息**默认静音**：要发声需在 设置页 →「消息提示音」打开总开关 +「插件来源：FF14 招募监控」

## FF14 物品价格插件

```bash
cd samples/ff14-price-plugin
dotnet build -c Debug

# 安装（目录名必须等于 id：ff14-price）
<程序目录>/plugins/ff14-price/plugin.json
<程序目录>/plugins/ff14-price/Ff14PricePlugin.dll
<程序目录>/plugins/ff14-price/data/items.tsv
```

- 物品词典 `data/items.tsv` 由 ffxiv-datamining-cn 的 `Item.csv` 生成（17,372 条，457KB），随插件安装
- 价格来自 Universalis v2；同一物品+大区缓存 60 秒，「刷新」强制重查
- 汇总表（按服务器最低价）/ 明细表（每条挂单）可切换，两张表都支持点表头排序
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

这些插件是**示例性质的实用工具**，不是精雕细琢的产品：

- 股票数据来自东方财富公开接口，**仅供参考，不构成投资建议**；接口对高频请求有限流，
  插件已做多域名切换 + 失败冷却 + 磁盘缓存，但长时间高频轮询仍可能被拒。
- Clash 插件通过外部控制器（`external-controller`）操作，需要客户端开启该功能；
  Clash Verge 默认 `127.0.0.1:9097`，也可能只开命名管道 `\\.\pipe\verge-mihomo`。
- FF14 招募数据来自第三方公开接口，可能存在延迟或缺失；插件对未知副本/服务器会用编号兜底显示。
- 全部插件遵循同一套约束：不碰宿主窗口/配置、不自建 Window、定时器 ≥100ms、
  改完视觉树调用 `ctx.RequestRepaint()`。
