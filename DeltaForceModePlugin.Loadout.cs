using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Modules.Entities.Constants;
using CounterStrikeSharp.API.Modules.Utils;
using DeltaForceMode.Domain;
using DeltaForceMode.Purchasing;

namespace DeltaForceMode;

public sealed partial class DeltaForceModePlugin
{
    private static readonly TimeSpan PendingReconcileInterval = TimeSpan.FromSeconds(20);

    private async Task ApplySpawnLoadoutAsync(ulong steamId64)
    {
        if (_unloading)
        {
            return;
        }

        await _roundResetTask.ConfigureAwait(false);
        var state = await _store.GetAsync(steamId64, CancellationToken.None).ConfigureAwait(false);
        if (_config.AutoReconcilePendingPurchases && state.PendingPurchase is not null)
        {
            var now = DateTimeOffset.UtcNow;
            var shouldReconcile = !_lastAutoReconcile.TryGetValue(steamId64, out var previous) ||
                                  now - previous >= PendingReconcileInterval;
            if (shouldReconcile)
            {
                _lastAutoReconcile[steamId64] = now;
                var attempt = await _purchases.ReconcileAsync(steamId64, CancellationToken.None)
                    .ConfigureAwait(false);
                if (attempt is not null)
                {
                    if (attempt.Status == PurchaseAttemptStatus.Completed)
                    {
                        SendToPlayer(steamId64, attempt.Message);
                    }
                    else if (attempt.Status == PurchaseAttemptStatus.Rejected)
                    {
                        SendToPlayer(
                            steamId64,
                            GameErrorMessages.FromGateway(attempt.ErrorCode, attempt.Message));
                    }
                }

                state = await _store.GetAsync(steamId64, CancellationToken.None)
                    .ConfigureAwait(false);
            }
        }

        await Server.NextFrameAsync(() =>
        {
            var player = FindPlayer(steamId64);
            if (player is not null)
            {
                ApplyStateToPlayer(player, state);
            }
        }).ConfigureAwait(false);
    }

    private async Task HandlePlayerDeathAsync(ulong steamId64)
    {
        if (!_config.ClearLoadoutOnDeath)
        {
            return;
        }

        var cleared = await _store.ClearOnDeathAsync(steamId64, CancellationToken.None)
            .ConfigureAwait(false);
        if (cleared)
        {
            SendToPlayer(steamId64, "你已被淘汰，本局购买装备失效。");
        }
    }

    private async Task ShowLoadoutAsync(ulong steamId64)
    {
        var state = await _store.GetAsync(steamId64, CancellationToken.None).ConfigureAwait(false);
        if (!state.LifeActive || state.Items.Count == 0)
        {
            SendToPlayer(steamId64, "当前没有有效的本局付费装备。输入 !shop 购买。");
        }
        else
        {
            var grouped = state.Items
                .Select(entry => _config.Catalog.Find(entry.ItemId))
                .Where(item => item is not null)
                .GroupBy(item => item!.DisplayName)
                .Select(group => group.Count() == 1
                    ? group.Key
                    : $"{group.Key} x{group.Count()}");
            SendToPlayer(steamId64, "本局装备：" + string.Join("、", grouped));
        }

        if (state.PendingPurchase is not null)
        {
            SendToPlayer(
                steamId64,
                $"待确认支付：{state.PendingPurchase.ItemId}，" +
                $"{MoneyFormatter.FormatCents(state.PendingPurchase.AmountCents)}。" +
                " 输入 !pending 查询。");
        }
    }

    private void ApplyStateToPlayer(CCSPlayerController player, PlayerLoadoutState state)
    {
        if (!player.IsValid || !player.PawnIsAlive)
        {
            return;
        }

        var pawn = player.PlayerPawn.Value;
        if (pawn is null || !pawn.IsValid)
        {
            return;
        }

        if (_config.StripUnboughtWeapons)
        {
            player.RemoveWeapons();
            player.GiveNamedItem(player.Team == CsTeam.Terrorist ? CsItem.KnifeT : CsItem.KnifeCT);
            pawn.ArmorValue = 0;
            if (pawn.ItemServices is CCSPlayer_ItemServices itemServices)
            {
                itemServices.HasHelmet = false;
            }
        }

        if (!state.LifeActive)
        {
            return;
        }

        var giveArmor = false;
        var giveHelmet = false;
        foreach (var entry in state.Items)
        {
            var item = _config.Catalog.Find(entry.ItemId);
            if (item is null)
            {
                continue;
            }

            if (item.IsArmor)
            {
                giveArmor = true;
                giveHelmet |= item.GameItem.Equals(
                    "item_assaultsuit",
                    StringComparison.OrdinalIgnoreCase);
                continue;
            }

            var gameItem = item.GetGameItem(player.Team == CsTeam.Terrorist);
            if (!string.IsNullOrWhiteSpace(gameItem))
            {
                player.GiveNamedItem(gameItem);
            }
        }

        if (giveArmor)
        {
            pawn.ArmorValue = 100;
            if (pawn.ItemServices is CCSPlayer_ItemServices itemServices)
            {
                itemServices.HasHelmet = giveHelmet;
            }
        }
    }
}
