# SSG Gateway Integration Contract

本文件只定义游戏插件与可信后端之间的边界。后端、SSG App API v1 请求和 durable outbox 由你朋友实现。

## 修改位置

1. 新增一个 `IPurchaseGateway` 实现，例如 `SsgPurchaseGateway.cs`。
2. 在该实现中只访问你们的可信后端，不把 SSG secret 写进插件。
3. 修改 `DeltaForceModePlugin.CreatePurchaseGateway()`，返回新实现。

接口文件：

```text
Purchasing/PurchaseGateway.cs
```

## 必要语义

### `IsConfigured`

只有后端 URL 和插件到后端的认证配置都完整时才返回 `true`。默认占位实现返回 `false`，避免没接接口时生成假待确认订单。

### `BeginLinkAsync`

返回：

- `LinkRequestId`
- 恰好四个验证词
- `VerificationUri`
- 轮询间隔秒数
- 过期时间

四个词由 SSG/后端生成，插件只私聊给对应玩家，不写日志。

### `PollLinkAsync`

返回 `Pending`、`Approved`、`Rejected`、`Expired` 或 `Failed`。临时网络错误应设置 `Failure.Retryable=true`，插件会继续轮询。

### `GetBalanceAsync`

`BalanceCents` 使用整数分。`100` 表示 `$1.00`。未绑定时返回 `NotLinked`。

### `DebitAsync`

必须遵守：

1. `PurchaseRequest.PurchaseId` 就是 SSG 扣款和后端 outbox 的唯一幂等键。
2. 只有 SSG 明确确认扣款后才能返回 `PurchaseState.Completed`。
3. 返回 `Completed` 时，`PurchaseId` 必须与请求一致，`DebitedAmountCents` 必须等于请求金额。
4. 超时、连接中断、进程崩溃、5xx 等不确定情况必须返回 `Unknown` 或 `Pending`，不能返回 `Rejected`。
5. 只有明确的 4xx 业务拒绝才能返回 `Rejected`。

建议后端收到请求后先持久化 outbox，再调用 SSG，并始终使用原始 `PurchaseId` 作为 SSG idempotency key。

### `GetPurchaseAsync`

用原始 `PurchaseId` 查询本地 outbox/SSG 结果，不得重新发起一笔新扣款。

## 错误码

业务错误使用：

```text
NotLinked
InsufficientFunds
LimitExceeded
Revoked
InvalidRequest
RateLimited
Timeout
Unavailable
Unknown
```

插件会将这些错误映射成中文游戏提示。

## 金额契约

所有金额字段都是整数分：

```text
$200      -> 20000
$1,000    -> 100000
$3,100    -> 310000
```

## 仍需团队确认

SSG 单次上限 `$1,000` 与当前 `$5,000` AWP / `$5,200` M249 的价格冲突。不要用两个不同 `PurchaseId` 静默拆单；需要先确定业务方案，再修改网关或价格。

