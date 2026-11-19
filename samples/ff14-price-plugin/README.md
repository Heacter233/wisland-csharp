# FF14 物品价格插件

查询 FF14 市场布告板价格。移植自「缪丝的挖宝会计」的 `src/utils/ff14_price_tool.py`，
把原程序的在线物品名解析换成了**随插件打包的离线词典**，并加上了汇总/明细双表格与筛选。

## 功能

- **物品名 → 物品ID**：离线词典（17,372 条，来自 ffxiv-datamining-cn 的 `Item.csv`），
  支持精确 / 忽略空格标点 / 前缀 / 包含匹配；也可直接输入物品 ID；
  词典未命中时才尝试 cafemaker / xivapi 在线兜底
- **价格查询**：Universalis v2 市场布告板接口，按大区分批并发查询（陆行鸟/莫古力/猫小胖/豆豆柴）
- **汇总表**（默认）：每个服务器一行 —— 最低价 / 数量 / 品质 / 挂单数 / 总库存 / 更新时间，
  按最低价升序
- **明细表**：每条挂单一行 —— 单价 / 服务器 / 大区 / 数量 / 品质 / 雇员 / 更新时间，
  按单价升序（最多绑定 2000 行，行/列虚拟化滚动）
- **筛选**：大区（全/鸟/猪/猫/狗）、品质（全/HQ/NQ）、只看有货、价格区间、最少数量
- **常用词**：★ 常用物品（收藏）+ 最近 5 条搜索历史，点一下即查
- **缓存**：同一物品+大区的结果缓存 60 秒（可配），「刷新」按钮强制重查

## 权限

```
network      访问 Universalis 市场接口（词典未命中时也会访问 cafemaker/xivapi）
ui.page      注册插件页（图标在主导航下方一行）
ui.settings  提供设置面板
```

## 数据布局

```
plugins/ff14-price/
├─ plugin.json
├─ Ff14PricePlugin.dll
└─ data/items.tsv          ← 物品词典（随插件安装，TSV：id \t 名称 \t 标记）

data/plugins/ff14-price/
└─ config.json             ← 筛选默认值、缓存时间、常用物品、搜索历史
```

`items.tsv` 标记位：`m` = 可上市场板，`h` = 可 HQ。

## 配置项（插件独立 config.json）

| 键 | 默认 | 说明 |
| --- | --- | --- |
| `dc` | `全` | 默认大区：`全`/`鸟`/`猪`/`猫`/`狗` |
| `quality` | `全` | 默认品质：`全`/`HQ`/`NQ` |
| `only_in_stock` | `true` | 只看有货（数量 > 0） |
| `min_price` / `max_price` | `0` | 价格区间，0 表示不限 |
| `min_quantity` | `0` | 最少数量，0 表示不限 |
| `cache_seconds` | `60` | 价格缓存秒数（0 = 不缓存） |
| `view_mode` | `summary` | `summary` 汇总 / `detail` 明细 |
| `last_item` | 空 | 上次查询的物品 |
| `favorites` | 空 | 常用物品（换行分隔，最多 12 条） |
| `history` | 空 | 搜索历史（换行分隔，最多 5 条） |

## 已知限制

- 市场数据来自 Universalis，可能存在几分钟延迟；大量查询时注意缓存时间别设太小
- 词典是打包时的快照：游戏更新新增物品后需要重新生成 `items.tsv`
- 明细表最多绑定 2000 行（再多也看不完），汇总表始终完整
- 原程序的灰机 wiki 爬取方案未移植（本机上 403，且依赖 HTML 解析，太脆）

## 重新生成词典

```bash
# 1) 下载 CN 客户端物品表（约 16MB）
curl -L -o Item.csv https://raw.githubusercontent.com/thewakingsands/ffxiv-datamining-cn/master/Item.csv
# 2) 用能正确处理引号/多行字段的 CSV 解析器，导出 id \t 名称 \t 标记 到 data/items.tsv
```

字段位置参考：`#`(物品ID)、`Name`(名称)、`ItemSearchCategory`(>0 可上板)、`CanBeHq`(可HQ)、`IsUntradable`(跳过)。
