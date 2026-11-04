using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Modules.Menu;
using DeltaForceMode.Domain;
using DeltaForceMode.Purchasing;

namespace DeltaForceMode;

public sealed partial class DeltaForceModePlugin
{
    private static readonly TimeSpan GatewayRequestTimeout = TimeSpan.FromSeconds(20);

    private async Task OpenShopAsync(ulong steamId64)
    {
        var balance = await GetBalanceWithTimeoutAsync(steamId64).ConfigureAwait(false);
        await Server.NextFrameAsync(() =>
        {
            var player = FindPlayer(steamId64);
            if (player is null)
            {
                return;
            }

            var balanceText = balance.BalanceCents is not null
                ? MoneyFormatter.FormatCents(balance.BalanceCents.Value)
                : balance.IsLinked
                    ? "读取失败"
                    : "未绑定";

            var menu = new ChatMenu($"Delta Force | SSG {balanceText}");
            foreach (var category in _config.Catalog.OrderedCategories)
            {
                var count = _config.Catalog.InCategory(category.Id).Count();
                var categoryId = category.Id;
                menu.AddMenuOption(
                    $"\x04{category.Name}\x01 ({count})",
                    (selectedPlayer, _) => ShowCategoryMenu(steamId64, categoryId));
            }

            menu.AddMenuOption(
                "\x04绑定 SSG 账户\x01",
                (selectedPlayer, _) => RunSafely(
                    () => BeginLinkAsync(steamId64),
                    nameof(BeginLinkAsync)));
            menu.AddMenuOption(
                "\x04查询待确认支付\x01",
                (selectedPlayer, _) => RunSafely(
                    () => ReconcilePendingAsync(steamId64, manual: true),
                    nameof(ReconcilePendingAsync)));

            MenuManager.OpenChatMenu(player, menu);
        }).ConfigureAwait(false);

        if (balance.Failure is not null)
        {
            SendToPlayer(
                steamId64,
                GameErrorMessages.FromGateway(
                    balance.Failure.Code.ToString(),
                    balance.Failure.Message));
        }
    }

    private void ShowCategoryMenu(ulong steamId64, string categoryId)
    {
        var player = FindPlayer(steamId64);
        var category = _config.Catalog.OrderedCategories.FirstOrDefault(item =>
            item.Id.Equals(categoryId, StringComparison.OrdinalIgnoreCase));
        if (player is null || category is null)
        {
            return;
        }

        var menu = new ChatMenu($"{category.Name} | 选择商品");
        foreach (var item in _config.Catalog.InCategory(categoryId))
        {
            var itemId = item.Id;
            menu.AddMenuOption(
                $"\x04{MoneyFormatter.FormatCents(item.PriceCents)}\x01 {item.DisplayName}",
                (selectedPlayer, _) => RunSafely(
                    () => OpenPurchaseConfirmationAsync(steamId64, itemId),
                    nameof(OpenPurchaseConfirmationAsync)));
        }

        menu.AddMenuOption(
            "\x04返回商店分类\x01",
            (selectedPlayer, _) => RunSafely(() => OpenShopAsync(steamId64), nameof(OpenShopAsync)));
        MenuManager.OpenChatMenu(player, menu);
    }

    private async Task OpenPurchaseConfirmationAsync(ulong steamId64, string itemId)
    {
        var item = _config.Catalog.Find(itemId);
        if (item is null)
        {
            return;
        }

        var state = await _store.GetAsync(steamId64, CancellationToken.None).ConfigureAwait(false);
        await Server.NextFrameAsync(() =>
        {
            var player = FindPlayer(steamId64);
            if (player is null)
            {
                return;
            }

            if (state.PendingPurchase is not null)
            {
                SendToPlayer(steamId64, "有一笔支付尚未确认，请先用 !pending 查询。");
                return;
            }

            var rule = LoadoutRules.Evaluate(
                state,
                item,
                _config.Catalog,
                _config.UtilityLimit);
            if (!rule.IsAllowed)
            {
                SendToPlayer(steamId64, GameErrorMessages.FromGateway(rule.ErrorCode, rule.Message));
                return;
            }

            var menu = new ChatMenu($"确认购买 {item.DisplayName}");
            menu.AddMenuOption(
                $"\x04确认支付 {MoneyFormatter.FormatCents(item.PriceCents)}\x01",
                (selectedPlayer, _) => RunSafely(
                    () => PurchaseAsync(steamId64, item.Id),
                    nameof(PurchaseAsync)));
            menu.AddMenuOption(
                "\x04取消\x01",
                (selectedPlayer, _) => MenuManager.CloseActiveMenu(selectedPlayer));

            MenuManager.OpenChatMenu(player, menu);

            if (rule.ReplacesExisting)
            {
                SendToPlayer(
                    steamId64,
                    $"该购买会替换当前 {SlotDisplayName(item.Slot)}，旧装备不会退款。");
            }
            else if (item.IsUtility)
            {
                SendToPlayer(
                    steamId64,
                    $"本局已购买 {state.Items.Count(entry => _config.Catalog.Find(entry.ItemId)?.IsUtility == true)}/{_config.UtilityLimit} 个投掷物。");
            }
        }).ConfigureAwait(false);
    }

    private async Task PurchaseAsync(ulong steamId64, string itemId)
    {
        if (!CanPurchaseNow(steamId64))
        {
            return;
        }

        SendToPlayer(steamId64, "正在向 SSG 确认扣款，请稍候...");
        var attempt = await _purchases.PurchaseAsync(
            steamId64,
            itemId,
            CancellationToken.None).ConfigureAwait(false);

        await HandlePurchaseAttemptAsync(steamId64, attempt, manualReconcile: false)
            .ConfigureAwait(false);
    }

    private async Task ReconcilePendingAsync(ulong steamId64, bool manual)
    {
        var state = await _store.GetAsync(steamId64, CancellationToken.None).ConfigureAwait(false);
        if (state.PendingPurchase is null)
        {
            if (manual)
            {
                SendToPlayer(steamId64, "没有待确认的支付。");
            }

            return;
        }

        if (manual)
        {
            SendToPlayer(steamId64, "正在查询待确认支付...");
        }

        var attempt = await _purchases.ReconcileAsync(steamId64, CancellationToken.None)
            .ConfigureAwait(false);
        if (attempt is null)
        {
            if (manual)
            {
                SendToPlayer(steamId64, "没有待确认的支付。");
            }

            return;
        }

        await HandlePurchaseAttemptAsync(steamId64, attempt, manual)
            .ConfigureAwait(false);
    }

    private async Task HandlePurchaseAttemptAsync(
        ulong steamId64,
        PurchaseAttempt attempt,
        bool manualReconcile)
    {
        switch (attempt.Status)
        {
            case PurchaseAttemptStatus.Completed:
            {
                var state = await _store.GetAsync(steamId64, CancellationToken.None)
                    .ConfigureAwait(false);
                await Server.NextFrameAsync(() =>
                {
                    var player = FindPlayer(steamId64);
                    if (player is not null)
                    {
                        MenuManager.CloseActiveMenu(player);
                        ApplyStateToPlayer(player, state);
                    }
                }).ConfigureAwait(false);

                var balanceSuffix = attempt.BalanceAfterCents is null
                    ? string.Empty
                    : $" 余额 {MoneyFormatter.FormatCents(attempt.BalanceAfterCents.Value)}。";
                SendToPlayer(steamId64, attempt.Message + balanceSuffix);
                break;
            }
            case PurchaseAttemptStatus.Rejected:
                SendToPlayer(
                    steamId64,
                    GameErrorMessages.FromGateway(attempt.ErrorCode, attempt.Message));
                break;
            case PurchaseAttemptStatus.PendingReconciliation:
                SendToPlayer(
                    steamId64,
                    manualReconcile
                        ? attempt.Message
                        : GameErrorMessages.FromGateway(attempt.ErrorCode, attempt.Message));
                break;
        }
    }

    private async Task ShowBalanceAsync(ulong steamId64)
    {
        var balance = await GetBalanceWithTimeoutAsync(steamId64).ConfigureAwait(false);
        if (balance.Failure is not null)
        {
            SendToPlayer(
                steamId64,
                GameErrorMessages.FromGateway(
                    balance.Failure.Code.ToString(),
                    balance.Failure.Message));
            return;
        }

        if (!balance.IsLinked || balance.BalanceCents is null)
        {
            SendToPlayer(steamId64, "SSG 账户尚未绑定，请先输入 !link。");
            return;
        }

        SendToPlayer(
            steamId64,
            $"SSG 余额：{MoneyFormatter.FormatCents(balance.BalanceCents.Value)}");
    }

    private async Task<BalanceResult> GetBalanceWithTimeoutAsync(ulong steamId64)
    {
        using var timeout = new CancellationTokenSource(GatewayRequestTimeout);
        try
        {
            return await _gateway.GetBalanceAsync(steamId64, timeout.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return new BalanceResult(
                IsLinked: false,
                BalanceCents: null,
                new GatewayFailure(GatewayErrorCode.Timeout, "Balance request timed out."));
        }
        catch (Exception exception)
        {
            return new BalanceResult(
                IsLinked: false,
                BalanceCents: null,
                new GatewayFailure(
                    GatewayErrorCode.Unknown,
                    $"Balance request failed: {exception.GetType().Name}"));
        }
    }

    private static string SlotDisplayName(string slot)
    {
        return slot.ToLowerInvariant() switch
        {
            "primary" => "主武器",
            "secondary" => "手枪",
            "armor" => "护甲",
            "taser" => "电击枪",
            _ => "装备"
        };
    }
}
