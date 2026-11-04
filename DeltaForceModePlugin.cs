using System.Collections.Concurrent;
using System.Text.Json;
using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Modules.Commands;
using CounterStrikeSharp.API.Modules.Utils;
using DeltaForceMode.Domain;
using DeltaForceMode.Persistence;
using DeltaForceMode.Purchasing;
using Microsoft.Extensions.Logging;

namespace DeltaForceMode;

public sealed partial class DeltaForceModePlugin : BasePlugin
{
    private readonly ConcurrentDictionary<ulong, LinkPollContext> _linkPolls = new();
    private readonly ConcurrentDictionary<ulong, DateTimeOffset> _lastAutoReconcile = new();

    private DeltaForceConfig _config = new();
    private LoadoutStore _store = null!;
    private IPurchaseGateway _gateway = null!;
    private PurchaseCoordinator _purchases = null!;

    private volatile bool _buyWindowOpen = true;
    private volatile bool _combatLive;
    private volatile bool _roundTerminationRequested;
    private volatile bool _unloading;
    private Task _roundResetTask = Task.CompletedTask;
    private int _participantsAtCombatStart;

    public override string ModuleName => "Delta Force Mode";
    public override string ModuleVersion => "0.1.0";
    public override string ModuleAuthor => "Ocean team";
    public override string ModuleDescription =>
        "SSG-funded last-player-standing battle royale mode.";

    public override void Load(bool hotReload)
    {
        _config = LoadConfiguration();
        _store = new LoadoutStore(Path.Combine(
            ModuleDirectory,
            "data",
            "loadouts.json"));
        _gateway = CreatePurchaseGateway();
        _purchases = new PurchaseCoordinator(
            _store,
            _gateway,
            _config.Catalog,
            _config.UtilityLimit);
        ApplyBattleRoyaleServerRules();

        RegisterEventHandler<EventPlayerSpawned>(OnPlayerSpawned);
        RegisterEventHandler<EventPlayerDeath>(OnPlayerDeath);
        RegisterEventHandler<EventPlayerDisconnect>(OnPlayerDisconnect);
        RegisterEventHandler<EventRoundStart>(OnRoundStart);
        RegisterEventHandler<EventRoundFreezeEnd>(OnRoundFreezeEnd);
        RegisterEventHandler<EventRoundEnd>(OnRoundEnd);

        AddCommand("css_shop", "打开 Delta Force 商店", OnShopCommand);
        AddCommand("css_buy", "购买商品：!buy <商品ID>", OnBuyCommand);
        AddCommand("css_link", "绑定 SSG 账户", OnLinkCommand);
        AddCommand("css_balance", "查询 SSG 余额", OnBalanceCommand);
        AddCommand("css_pending", "查询待确认支付", OnPendingCommand);
        AddCommand("css_loadout", "查看当前装备", OnLoadoutCommand);

        AddCommandListener("buy", OnBlockNativeBuy);
        AddCommandListener("autobuy", OnBlockNativeBuy);
        AddCommandListener("rebuy", OnBlockNativeBuy);

        Logger.LogInformation(
            "DeltaForceMode loaded with {ItemCount} items. Gateway: {GatewayType}",
            _config.Catalog.Items.Count,
            _gateway.GetType().Name);
    }

    public override void Unload(bool hotReload)
    {
        _unloading = true;
        foreach (var poll in _linkPolls.Values)
        {
            poll.Cancellation.Cancel();
        }

        _linkPolls.Clear();
        _store.Dispose();
    }

    /// <summary>
    /// ONLY integration point for the trusted SSG backend adapter.
    /// Replace UnavailablePurchaseGateway with the friend's implementation.
    /// No SSG client secret belongs in this assembly or config.
    /// </summary>
    private IPurchaseGateway CreatePurchaseGateway()
    {
        return new UnavailablePurchaseGateway();
    }

    private DeltaForceConfig LoadConfiguration()
    {
        var path = Path.Combine(ModuleDirectory, "config", "DeltaForceMode.json");
        if (!File.Exists(path))
        {
            throw new FileNotFoundException(
                $"Delta Force config not found at {path}. Copy config/DeltaForceMode.json next to the plugin.",
                path);
        }

        var options = new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true,
            ReadCommentHandling = JsonCommentHandling.Skip,
            AllowTrailingCommas = true
        };
        var config = JsonSerializer.Deserialize<DeltaForceConfig>(
            File.ReadAllText(path),
            options) ?? throw new InvalidOperationException("Delta Force config is empty.");

        if (config.UtilityLimit is < 1 or > 8)
        {
            throw new InvalidOperationException("utilityLimit must be between 1 and 8.");
        }

        if (config.BuyTimeSeconds is < 10 or > 300)
        {
            throw new InvalidOperationException("buyTimeSeconds must be between 10 and 300.");
        }

        if (config.RoundTimeMinutes is < 5 or > 60)
        {
            throw new InvalidOperationException("roundTimeMinutes must be between 5 and 60.");
        }

        if (config.MinimumParticipants < 1)
        {
            throw new InvalidOperationException("minimumParticipants must be at least 1.");
        }

        if (config.Extraction.Enabled)
        {
            throw new InvalidOperationException(
                "Extraction points are not implemented yet. Keep extraction.enabled=false.");
        }

        var errors = config.Catalog.Validate();
        if (errors.Count > 0)
        {
            throw new InvalidOperationException(
                "Invalid weapon catalog:\n" + string.Join("\n", errors));
        }

        return config;
    }

    private void OnShopCommand(CCSPlayerController? player, CommandInfo command)
    {
        if (!TryGetSteamId(player, out var steamId64))
        {
            command.ReplyToCommand("该命令只能由已登录的玩家使用。");
            return;
        }

        RunSafely(() => OpenShopAsync(steamId64), nameof(OpenShopAsync));
    }

    private void OnBuyCommand(CCSPlayerController? player, CommandInfo command)
    {
        if (!TryGetSteamId(player, out var steamId64))
        {
            command.ReplyToCommand("该命令只能由已登录的玩家使用。");
            return;
        }

        if (command.ArgCount < 2 || string.IsNullOrWhiteSpace(command.ArgByIndex(1)))
        {
            RunSafely(() => OpenShopAsync(steamId64), nameof(OpenShopAsync));
            return;
        }

        var item = _config.Catalog.Find(command.ArgByIndex(1));
        if (item is null)
        {
            SendToPlayer(steamId64, "没有找到这个商品。");
            return;
        }

        RunSafely(
            () => OpenPurchaseConfirmationAsync(steamId64, item.Id),
            nameof(OpenPurchaseConfirmationAsync));
    }

    private void OnLinkCommand(CCSPlayerController? player, CommandInfo command)
    {
        if (!TryGetSteamId(player, out var steamId64))
        {
            command.ReplyToCommand("该命令只能由已登录的玩家使用。");
            return;
        }

        RunSafely(() => BeginLinkAsync(steamId64), nameof(BeginLinkAsync));
    }

    private void OnBalanceCommand(CCSPlayerController? player, CommandInfo command)
    {
        if (!TryGetSteamId(player, out var steamId64))
        {
            command.ReplyToCommand("该命令只能由已登录的玩家使用。");
            return;
        }

        RunSafely(() => ShowBalanceAsync(steamId64), nameof(ShowBalanceAsync));
    }

    private void OnPendingCommand(CCSPlayerController? player, CommandInfo command)
    {
        if (!TryGetSteamId(player, out var steamId64))
        {
            command.ReplyToCommand("该命令只能由已登录的玩家使用。");
            return;
        }

        RunSafely(() => ReconcilePendingAsync(steamId64, manual: true), nameof(ReconcilePendingAsync));
    }

    private void OnLoadoutCommand(CCSPlayerController? player, CommandInfo command)
    {
        if (!TryGetSteamId(player, out var steamId64))
        {
            command.ReplyToCommand("该命令只能由已登录的玩家使用。");
            return;
        }

        RunSafely(() => ShowLoadoutAsync(steamId64), nameof(ShowLoadoutAsync));
    }

    private HookResult OnBlockNativeBuy(CCSPlayerController? player, CommandInfo command)
    {
        if (player is not null && TryGetSteamId(player, out _))
        {
            player.PrintToChat($"{ChatPrefix} 原生购买菜单已禁用，请使用 !shop。");
            return HookResult.Handled;
        }

        return HookResult.Continue;
    }

    private HookResult OnPlayerSpawned(EventPlayerSpawned gameEvent, GameEventInfo info)
    {
        var player = gameEvent.Userid;
        if (TryGetSteamId(player, out var steamId64))
        {
            RunSafely(() => ApplySpawnLoadoutAsync(steamId64), nameof(ApplySpawnLoadoutAsync));
        }

        return HookResult.Continue;
    }

    private HookResult OnPlayerDeath(EventPlayerDeath gameEvent, GameEventInfo info)
    {
        var player = gameEvent.Userid;
        if (TryGetSteamId(player, out var steamId64))
        {
            RunSafely(() => HandlePlayerDeathAsync(steamId64), nameof(HandlePlayerDeathAsync));
            ScheduleBattleRoyaleOutcomeCheck();
        }

        return HookResult.Continue;
    }

    private HookResult OnPlayerDisconnect(EventPlayerDisconnect gameEvent, GameEventInfo info)
    {
        var player = gameEvent.Userid;
        if (TryGetSteamId(player, out var steamId64) &&
            _linkPolls.TryRemove(steamId64, out var poll))
        {
            poll.Cancellation.Cancel();
        }

        ScheduleBattleRoyaleOutcomeCheck();
        return HookResult.Continue;
    }

    private HookResult OnRoundStart(EventRoundStart gameEvent, GameEventInfo info)
    {
        _buyWindowOpen = false;
        _combatLive = false;
        _roundTerminationRequested = false;
        _participantsAtCombatStart = 0;
        _roundResetTask = ResetForNewBattleRoyaleRoundAsync();
        RunSafely(() => _roundResetTask, nameof(ResetForNewBattleRoyaleRoundAsync));
        return HookResult.Continue;
    }

    private HookResult OnRoundFreezeEnd(EventRoundFreezeEnd gameEvent, GameEventInfo info)
    {
        _buyWindowOpen = false;
        _combatLive = true;
        _participantsAtCombatStart = CountAliveParticipants();
        Server.PrintToChatAll(
            $"{ChatPrefix} 购买结束，交战开始。本局共 {_participantsAtCombatStart} 名参赛者。");
        return HookResult.Continue;
    }

    private HookResult OnRoundEnd(EventRoundEnd gameEvent, GameEventInfo info)
    {
        _buyWindowOpen = false;
        _combatLive = false;
        return HookResult.Continue;
    }

    private bool CanPurchaseNow(ulong steamId64)
    {
        var player = FindPlayer(steamId64);
        if (player is null ||
            !player.PawnIsAlive ||
            player.Team is not (CsTeam.Terrorist or CsTeam.CounterTerrorist))
        {
            SendToPlayer(steamId64, "只有本局仍在场的参赛者可以购买。");
            return false;
        }

        if (!_config.BuyWindowOnly || _buyWindowOpen)
        {
            return true;
        }

        SendToPlayer(steamId64, "当前不在购买阶段。");
        return false;
    }

    private bool TryGetSteamId(
        CCSPlayerController? player,
        out ulong steamId64)
    {
        steamId64 = 0;
        if (player is null || !player.IsValid || player.IsBot)
        {
            return false;
        }

        steamId64 = player.AuthorizedSteamID?.SteamId64 ?? 0;
        return steamId64 != 0;
    }

    private CCSPlayerController? FindPlayer(ulong steamId64)
    {
        var player = Utilities.GetPlayerFromSteamId64(steamId64);
        return player is { IsValid: true, IsBot: false } ? player : null;
    }

    private void SendToPlayer(ulong steamId64, string message)
    {
        if (_unloading)
        {
            return;
        }

        Server.NextFrame(() =>
        {
            FindPlayer(steamId64)?.PrintToChat($"{ChatPrefix} {message}");
        });
    }

    private void RunSafely(Func<Task> operation, string operationName)
    {
        _ = ObserveAsync(operation, operationName);
    }

    private async Task ObserveAsync(Func<Task> operation, string operationName)
    {
        try
        {
            await operation().ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            Logger.LogError(exception, "DeltaForceMode operation {OperationName} failed.", operationName);
        }
    }

    private static string ChatPrefix => "\x04[Delta Force]\x01";
}

internal sealed record LinkPollContext(
    string LinkRequestId,
    CancellationTokenSource Cancellation);
