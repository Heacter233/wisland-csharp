# FF14 挖宝悬浮窗插件

把 ACT 插件 **FF14TreasureHunterACT** 的悬浮窗搬到灵动岛里，并接管它的消息通知。

- 原始项目：`E:\Dev\FF14TreasureHunterACT`（ACT 插件，.NET Framework 4.7.2）
- **不需要改动 ACT 项目**：它自带的 `OverlayServer` 已经在本机 `overlay_port`（默认 **8765**）上
  同时提供 HTTP 与 **WebSocket `/ws`**，本机回环免鉴权；本插件直接连 WS 读数据。

## 功能

- **灵动岛版悬浮窗**（插件页）：统计卡（地图 / 封印 / 系统金 / 收入 / 成本 / 盈亏）
  + 物品出货（按地图分组）+ 层级 / 其他记录，滚动查看；顶栏显示连接状态与轮次，可手动「重连」
- **鉴定成功 / 失败通知**：用 `NotifyDraw` 自绘两个图案推到消息通知栏——
  - 成功：**打开的宝箱 + 金光 + 光晕**（呼吸动画，随通知动画时间脉动）
  - 失败：**关着的宝箱 + 红色叉号**（轻微抖动）
- **出货合并通知**：新获得的物品先攒着（默认 4 秒无新增后合并），推一条
  「本轮出货 · 共 N 件」；明细在悬浮窗里按地图 → 物品分层查看
- **断线自动重连**（默认 5 秒），连接状态在插件页与设置面板可见

## 权限

```
network.local   连接本机 127.0.0.1:8765 的 WebSocket
messages.push   推送成功/失败/出货通知到消息页
ui.compact      自绘通知图案（NotifyDraw）
ui.page         注册插件页（导航下方图标）
ui.settings     提供设置面板
```

## 数据来源（ACT 侧 summary JSON）

```
run_label / maps / magic_seals / system_gold / total_income / total_cost / profit / segments
player / world / map / party_count
items[{map,name,qty}] / layers[{name,count}] / customs[{name,total}]
notifications[{type:"success"|"fail"|"info", text, timestamp}]   ← 仅 WebSocket 附带
```

显示格式对齐原 `overlay.html`：`系统金 ÷10000` 显示「万」，成本 / 收入 / 盈亏保留两位小数。

## 配置项（插件独立 config.json）

| 键 | 默认 | 说明 |
| --- | --- | --- |
| `host` | `127.0.0.1` | ACT OverlayServer 地址 |
| `port` | `8765` | 端口（对应 ACT 配置 `overlay_port`） |
| `notify_predictions` | `true` | 鉴定成功/失败通知 |
| `notify_items` | `true` | 出货合并通知 |
| `item_merge_seconds` | `4` | 出货合并等待秒数（1~30） |
| `reconnect_seconds` | `5` | 断线重连间隔（1~60） |

## 前置条件

1. ACT 里加载并启用 FF14TreasureHunterACT 插件
2. 该插件的「悬浮窗服务」已开启（配置项 `overlay_enabled = true`，端口 `overlay_port = 8765`）
3. 想响铃的话：设置页 →「消息提示音」打开总开关与「插件来源：FF14 挖宝悬浮窗」

## 已知限制

- 一次只展示**最新一条**自定义动画：宿主在收起态只有一个自绘区域，多条通知排队时显示最后一条的图案（宿主的展示机制，不是插件的 bug）
- ACT 悬浮窗服务是 localhost HTTP/WS，不要暴露到公网；插件本身不发送任何外网请求
- 悬浮窗页面高度 500，物品特别多时请滚动查看（明细在 ACT 侧聚合，插件不做二次筛选）
