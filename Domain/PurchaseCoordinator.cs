using System.Collections.Concurrent;
using DeltaForceMode.Purchasing;

namespace DeltaForceMode.Domain;

public enum PurchaseAttemptStatus
{
    Completed,
    Rejected,
    PendingReconciliation
}

public sealed record PurchaseAttempt(
    PurchaseAttemptStatus Status,
    string PurchaseId,
    string? ItemId,
    string? ErrorCode,
    string Message,
    bool ReplacesExisting,
    long? BalanceAfterCents)
{
    public bool IsCompleted => Status == PurchaseAttemptStatus.Completed;
}

public sealed class PurchaseCoordinator
{
    private readonly ILoadoutRepository _repository;
    private readonly IPurchaseGateway _gateway;
    private readonly WeaponCatalog _catalog;
    private readonly int _utilityLimit;
    private readonly ConcurrentDictionary<ulong, SemaphoreSlim> _playerLocks = new();

    public PurchaseCoordinator(
        ILoadoutRepository repository,
        IPurchaseGateway gateway,
        WeaponCatalog catalog,
        int utilityLimit)
    {
        _repository = repository;
        _gateway = gateway;
        _catalog = catalog;
        _utilityLimit = utilityLimit;
    }

    public async Task<PurchaseAttempt> PurchaseAsync(
        ulong steamId64,
        string itemId,
        CancellationToken cancellationToken)
    {
        var playerLock = _playerLocks.GetOrAdd(steamId64, _ => new SemaphoreSlim(1, 1));
        await playerLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var state = await _repository.GetAsync(steamId64, cancellationToken).ConfigureAwait(false);
            if (state.PendingPurchase is not null)
            {
                return new PurchaseAttempt(
                    PurchaseAttemptStatus.PendingReconciliation,
                    state.PendingPurchase.PurchaseId,
                    state.PendingPurchase.ItemId,
                    GatewayErrorCodes.PurchaseAlreadyPending,
                    "有一笔支付尚未确认，请先用 !pending 查询。",
                    ReplacesExisting: false,
                    BalanceAfterCents: null);
            }

            var item = _catalog.Find(itemId);
            if (item is null)
            {
                return new PurchaseAttempt(
                    PurchaseAttemptStatus.Rejected,
                    string.Empty,
                    itemId,
                    GatewayErrorCodes.LocalItemNotFound,
                    "没有找到这个商品。",
                    ReplacesExisting: false,
                    BalanceAfterCents: null);
            }

            if (!_gateway.IsConfigured)
            {
                return new PurchaseAttempt(
                    PurchaseAttemptStatus.Rejected,
                    string.Empty,
                    item.Id,
                    GatewayErrorCodes.GatewayNotConfigured,
                    "支付接口尚未接入。",
                    ReplacesExisting: false,
                    BalanceAfterCents: null);
            }

            var rule = LoadoutRules.Evaluate(state, item, _catalog, _utilityLimit);
            if (!rule.IsAllowed)
            {
                return new PurchaseAttempt(
                    PurchaseAttemptStatus.Rejected,
                    string.Empty,
                    item.Id,
                    rule.ErrorCode,
                    rule.Message,
                    rule.ReplacesExisting,
                    BalanceAfterCents: null);
            }

            var purchaseId = Guid.NewGuid().ToString("N");
            var pending = new PendingPurchase
            {
                PurchaseId = purchaseId,
                ItemId = item.Id,
                AmountCents = item.PriceCents,
                Description = $"Delta Force CS2: {item.DisplayName} / {steamId64}",
                CreatedAtUtc = DateTimeOffset.UtcNow
            };

            // Persist intent before the network call. If the server crashes after
            // SSG accepts the debit, reconciliation still has the original key.
            state.PendingPurchase = pending;
            state.UpdatedAtUtc = DateTimeOffset.UtcNow;
            await _repository.SaveAsync(state, cancellationToken).ConfigureAwait(false);

            var request = ToRequest(steamId64, pending);
            PurchaseResult result;
            try
            {
                result = await _gateway.DebitAsync(request, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception exception) when (exception is not OperationCanceledException ||
                                              !cancellationToken.IsCancellationRequested)
            {
                return Pending(
                    pending,
                    GatewayErrorCodes.GatewayException,
                    $"支付结果暂不可确认（{exception.GetType().Name}），请用 !pending 查询。",
                    rule.ReplacesExisting);
            }
            catch (OperationCanceledException)
            {
                return Pending(
                    pending,
                    GatewayErrorCode.Timeout.ToString(),
                    "支付请求已中断，结果未知，请用 !pending 查询。",
                    rule.ReplacesExisting);
            }

            return await ApplyGatewayResultAsync(
                steamId64,
                item,
                pending,
                result,
                rule.ReplacesExisting,
                cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            playerLock.Release();
        }
    }

    public async Task<PurchaseAttempt?> ReconcileAsync(
        ulong steamId64,
        CancellationToken cancellationToken)
    {
        var playerLock = _playerLocks.GetOrAdd(steamId64, _ => new SemaphoreSlim(1, 1));
        await playerLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var state = await _repository.GetAsync(steamId64, cancellationToken).ConfigureAwait(false);
            var pending = state.PendingPurchase;
            if (pending is null)
            {
                return null;
            }

            var item = _catalog.Find(pending.ItemId);
            if (item is null)
            {
                return new PurchaseAttempt(
                    PurchaseAttemptStatus.PendingReconciliation,
                    pending.PurchaseId,
                    pending.ItemId,
                    GatewayErrorCodes.LocalItemNotFound,
                    "待确认商品已从配置中移除，请联系管理员处理，勿重复购买。",
                    ReplacesExisting: false,
                    BalanceAfterCents: null);
            }

            var request = ToRequest(steamId64, pending);
            PurchaseResult result;
            try
            {
                result = await _gateway.GetPurchaseAsync(request, cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (Exception exception)
            {
                return Pending(
                    pending,
                    GatewayErrorCodes.GatewayException,
                    $"仍无法确认支付结果（{exception.GetType().Name}）。",
                    replacesExisting: false);
            }

            return await ApplyGatewayResultAsync(
                steamId64,
                item,
                pending,
                result,
                replacesExisting: false,
                cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            playerLock.Release();
        }
    }

    private async Task<PurchaseAttempt> ApplyGatewayResultAsync(
        ulong steamId64,
        WeaponItem item,
        PendingPurchase pending,
        PurchaseResult result,
        bool replacesExisting,
        CancellationToken cancellationToken)
    {
        switch (result.State)
        {
            case PurchaseState.Completed:
            {
                if (!result.PurchaseId.Equals(pending.PurchaseId, StringComparison.Ordinal) ||
                    result.DebitedAmountCents != pending.AmountCents)
                {
                    return Pending(
                        pending,
                        GatewayErrorCodes.PurchaseIdentityMismatch,
                        "支付回执与本地订单不一致，已禁止发货，请联系管理员。",
                        replacesExisting);
                }

                var state = await _repository.GetAsync(steamId64, cancellationToken)
                    .ConfigureAwait(false);
                LoadoutRules.Grant(state, item, _catalog, pending, result.ReceiptId);
                await _repository.SaveAsync(state, cancellationToken).ConfigureAwait(false);

                return new PurchaseAttempt(
                    PurchaseAttemptStatus.Completed,
                    pending.PurchaseId,
                    item.Id,
                    ErrorCode: null,
                    $"购买成功：{item.DisplayName}。",
                    replacesExisting,
                    result.BalanceAfterCents);
            }
            case PurchaseState.Rejected:
            {
                var state = await _repository.GetAsync(steamId64, cancellationToken)
                    .ConfigureAwait(false);
                state.PendingPurchase = null;
                state.UpdatedAtUtc = DateTimeOffset.UtcNow;
                await _repository.SaveAsync(state, cancellationToken).ConfigureAwait(false);

                return new PurchaseAttempt(
                    PurchaseAttemptStatus.Rejected,
                    pending.PurchaseId,
                    item.Id,
                    result.Failure?.Code.ToString(),
                    result.Failure?.Message ?? "支付被拒绝，未扣款。",
                    replacesExisting,
                    result.BalanceAfterCents);
            }
            case PurchaseState.Pending:
                return Pending(
                    pending,
                    result.Failure?.Code.ToString() ?? "purchase_pending",
                    "支付仍在确认中，请稍后用 !pending 查询。",
                    replacesExisting);
            case PurchaseState.Unknown:
            default:
                return Pending(
                    pending,
                    result.Failure?.Code.ToString() ?? "purchase_unknown",
                    "支付结果未知，请用 !pending 查询，不要重复购买。",
                    replacesExisting);
        }
    }

    private static PurchaseAttempt Pending(
        PendingPurchase pending,
        string errorCode,
        string message,
        bool replacesExisting)
    {
        return new PurchaseAttempt(
            PurchaseAttemptStatus.PendingReconciliation,
            pending.PurchaseId,
            pending.ItemId,
            errorCode,
            message,
            replacesExisting,
            BalanceAfterCents: null);
    }

    private static PurchaseRequest ToRequest(ulong steamId64, PendingPurchase pending)
    {
        return new PurchaseRequest(
            pending.PurchaseId,
            steamId64,
            pending.ItemId,
            pending.AmountCents,
            pending.Description);
    }
}
