# DF Spaceport Mode Plugin

CounterStrikeSharp 服务端插件，实现“用 SSG200x 余额购买 CS2 原生武器”的大逃杀 MVP。仓库当前只包含游戏侧代码；SSG 网络请求由你朋友的适配器实现。

## 当前玩法

- 单人淘汰制：一局一次购买，死亡即淘汰，不会自动重生。
- 最后一名存活玩家获胜；所有玩家死亡则平局。
- 原生 CS2 队伍胜负条件被关闭，插件自行结束大逃杀回合。
- 默认购买时间 60 秒、回合时间 25 分钟，可在 JSON 中调整。
- 购买装备只在当前一局有效。新回合会清除上一局装备，未确认支付会保留。
- 原生购买菜单被禁用，统一使用 `!shop`。
- 撤离点尚未实现。配置中保留了 `extraction` 段，启用后会拒绝启动，避免半成品误上线。

当前大逃杀仍需要一张有足够独立出生点的 CS2 地图/地图层。标准 5v5 地图的出生点数量可能不足以支撑大规模 BR。

## 前置条件

- CS2 dedicated server
- CounterStrikeSharp 1.0.379 或兼容版本
- .NET 10 SDK
- 插件 NuGet 依赖：`CounterStrikeSharp.API` 1.0.379

CounterStrikeSharp 1.0.379 的 NuGet 包目标框架是 `net10.0`，因此插件项目也使用 `net10.0`。

## 构建与测试

```powershell
dotnet restore .\DeltaForceMode.csproj
dotnet build .\DeltaForceMode.csproj -c Release
dotnet run --project .\tests\DeltaForceMode.Tests\DeltaForceMode.Tests.csproj -c Release
```

测试是可执行的游戏侧测试套件，不需要启动 CS2。覆盖：

- 价格配置加载和整数分金额
- 支付成功后才发放装备
- 超时未知状态不发放装备
- 待确认支付跨重启持久化
- 对账复用同一个 `PurchaseId`
- 主武器替换和投掷物上限
- 未接入支付网关时不制造假待确认订单

## 安装

1. 构建 Release 版本。
2. 创建目录：

```text
game/csgo/addons/counterstrikesharp/plugins/DeltaForceMode/
```

3. 将以下文件放入该目录：

```text
DeltaForceMode.dll
config/DeltaForceMode.json
```

4. 启动服务器。插件会在同级目录创建：

```text
data/loadouts.json
```

该文件保存本局装备和待确认购买意图，不包含 SSG 密钥。

## 玩家命令

| 命令 | 作用 |
| --- | --- |
| `!shop` | 打开分类商店 |
| `!buy` | 等同于 `!shop` |
| `!buy ak47` | 直接打开 AK-47 确认页 |
| `!link` | 创建 SSG 绑定请求，四个验证词只发给该玩家 |
| `!balance` | 查询 SSG 余额 |
| `!pending` | 查询并处理结果未知的支付 |
| `!loadout` | 查看本局已购买装备 |

## 价格配置

价格在 `config/DeltaForceMode.json` 的 `catalog.items` 中。`priceCents` 是整数分：

```json
{
  "id": "ak47",
  "displayName": "AK-47",
  "priceCents": 310000
}
```

`310000` 表示 `$3,100.00`。修改价格只需改 JSON，重启或重新加载插件后生效。

插件支持以下装备槽：

- `primary`
- `secondary`
- `armor`
- `utility`
- `taser`

火类投掷物通过 `terroristGameItem` 和 `counterTerroristGameItem` 分别发放燃烧瓶和燃烧弹。

## 自动设置的服务器规则

`applyBattleRoyaleServerRules=true` 时，插件会设置：

```text
mp_ignore_round_win_conditions 1
mp_teammates_are_enemies 1
mp_friendlyfire 1
mp_autoteambalance 0
mp_limitteams 0
mp_respawn_on_death_ct 0
mp_respawn_on_death_t 0
mp_freezetime <buyTimeSeconds>
mp_roundtime <roundTimeMinutes>
mp_roundtime_defuse <roundTimeMinutes>
mp_roundtime_hostage <roundTimeMinutes>
mp_round_restart_delay 3
```

如果服务器启动配置已经统一管理这些 cvar，可将该项改为 `false`，但必须自行保证等价规则。

## 支付接口接入口

唯一需要你朋友修改的位置：

```text
DeltaForceModePlugin.cs
  CreatePurchaseGateway()
```

接口和模型：

```text
Purchasing/PurchaseGateway.cs
```

默认返回：

```csharp
return new UnavailablePurchaseGateway();
```

朋友需要新增自己的 `IPurchaseGateway` 实现，并在 `CreatePurchaseGateway()` 中替换掉占位实现。插件不包含 SSG `client_id`、secret、bot token 或任何 SSG 请求代码。

完整字段语义和错误映射见 [SSG_INTEGRATION.md](SSG_INTEGRATION.md)。

## 关键安全约束

- 插件只调用可信后端，不直接调用 SSG。
- 插件不保存任何 SSG 密钥。
- `PurchaseId` 必须同时作为后端 outbox 的幂等键，网络超时不能换新 ID 重试。
- 只有网关返回 `PurchaseState.Completed` 且回执金额与本地订单一致时才发装备。
- `PurchaseState.Unknown` 会保留本地待确认单，必须通过 `GetPurchaseAsync` 用原 ID 对账。
- 后端 durable outbox 和 SSG 实际扣款逻辑由朋友负责。

## 已知待确认项

原始约束中的 SSG 单次上限是 `$1,000`，但价格表包含 `$5,000` 的 AWP、`$5,200` 的 M249 等商品。插件不会擅自拆单，因为部分成功会造成非原子扣款。上线前需要确认：

- SSG 的实际单次上限是否允许这些商品；
- 如果不允许，是调整价格、提高限额，还是由后端实现可回滚的分段扣款；
- 撤离点的触发区域、倒计时、胜负判定和 UI。

