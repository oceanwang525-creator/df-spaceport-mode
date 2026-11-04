using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Modules.Entities.Constants;
using CounterStrikeSharp.API.Modules.Utils;
using Microsoft.Extensions.Logging;

namespace DeltaForceMode;

public sealed partial class DeltaForceModePlugin
{
    private void ApplyBattleRoyaleServerRules()
    {
        if (!_config.ApplyBattleRoyaleServerRules)
        {
            Logger.LogWarning(
                "Battle royale server cvars are disabled by config; verify the server launch config.");
            return;
        }

        Server.ExecuteCommand("mp_ignore_round_win_conditions 1");
        Server.ExecuteCommand("mp_teammates_are_enemies 1");
        Server.ExecuteCommand("mp_friendlyfire 1");
        Server.ExecuteCommand("mp_autoteambalance 0");
        Server.ExecuteCommand("mp_limitteams 0");
        Server.ExecuteCommand("mp_respawn_on_death_ct 0");
        Server.ExecuteCommand("mp_respawn_on_death_t 0");
        Server.ExecuteCommand($"mp_freezetime {_config.BuyTimeSeconds}");
        Server.ExecuteCommand($"mp_roundtime {_config.RoundTimeMinutes}");
        Server.ExecuteCommand($"mp_roundtime_defuse {_config.RoundTimeMinutes}");
        Server.ExecuteCommand($"mp_roundtime_hostage {_config.RoundTimeMinutes}");
        Server.ExecuteCommand("mp_round_restart_delay 3");
    }

    private async Task ResetForNewBattleRoyaleRoundAsync()
    {
        try
        {
            await _store.ResetAllLivesAsync(CancellationToken.None).ConfigureAwait(false);
            await Server.NextFrameAsync(() =>
            {
                if (!_combatLive)
                {
                    _buyWindowOpen = true;
                }

                Server.PrintToChatAll(
                    $"{ChatPrefix} 大逃杀开始。限时购买后进入战斗，最后存活者获胜。");
                Server.PrintToChatAll(
                    $"{ChatPrefix} 输入 !shop 用 SSG 余额购买本命装备。");
            }).ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            Logger.LogError(exception, "Failed to reset battle royale round state.");
            _buyWindowOpen = false;
            Server.NextFrame(() =>
            {
                Server.PrintToChatAll($"{ChatPrefix} 回合初始化失败，购买暂时关闭，请联系管理员。");
            });
        }
    }

    private int CountAliveParticipants()
    {
        return Utilities.GetPlayers().Count(player =>
            player is { IsValid: true, PawnIsAlive: true } &&
            player.Team is CsTeam.Terrorist or CsTeam.CounterTerrorist);
    }

    private void ScheduleBattleRoyaleOutcomeCheck()
    {
        if (_unloading)
        {
            return;
        }

        Server.NextFrame(CheckBattleRoyaleOutcome);
    }

    private void CheckBattleRoyaleOutcome()
    {
        if (_unloading ||
            !_combatLive ||
            _roundTerminationRequested ||
            _participantsAtCombatStart < _config.MinimumParticipants)
        {
            return;
        }

        var alive = Utilities.GetPlayers()
            .Where(player =>
                player is { IsValid: true, PawnIsAlive: true } &&
                player.Team is CsTeam.Terrorist or CsTeam.CounterTerrorist)
            .ToList();

        if (alive.Count > 1)
        {
            return;
        }

        _roundTerminationRequested = true;
        var rules = Utilities
            .FindAllEntitiesByDesignerName<CCSGameRulesProxy>("cs_gamerules")
            .FirstOrDefault()
            ?.GameRules;

        if (alive.Count == 1)
        {
            var winner = alive[0];
            Server.PrintToChatAll(
                $"{ChatPrefix} \x04{winner.PlayerName}\x01 是本局最后存活者，获胜！");
            rules?.TerminateRound(3.0f, RoundEndReason.SurvivalWin);
        }
        else
        {
            Server.PrintToChatAll($"{ChatPrefix} 本局无人生还。");
            rules?.TerminateRound(3.0f, RoundEndReason.SurvivalDraw);
        }

        if (rules is null)
        {
            Logger.LogError("Could not resolve CCSGameRules to terminate the battle royale round.");
        }
    }
}
