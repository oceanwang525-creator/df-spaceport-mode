using CounterStrikeSharp.API;
using DeltaForceMode.Purchasing;
using Microsoft.Extensions.Logging;

namespace DeltaForceMode;

public sealed partial class DeltaForceModePlugin
{
    private static readonly TimeSpan MaximumLinkLifetime = TimeSpan.FromMinutes(15);

    private async Task BeginLinkAsync(ulong steamId64)
    {
        using var timeout = new CancellationTokenSource(GatewayRequestTimeout);
        LinkStartResult result;
        try
        {
            result = await _gateway.BeginLinkAsync(steamId64, timeout.Token).ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            Logger.LogError(exception, "Failed to create link request for {SteamId64}", steamId64);
            SendToPlayer(steamId64, "创建绑定请求失败，请稍后重试。");
            return;
        }

        if (!result.IsSuccess || result.Words.Count != 4)
        {
            SendToPlayer(
                steamId64,
                result.Failure is null
                    ? "绑定服务没有返回四个验证词。"
                    : GameErrorMessages.FromGateway(
                        result.Failure.Code.ToString(),
                        result.Failure.Message));
            return;
        }

        SendToPlayer(steamId64, "请在 SSG 网站登录并确认绑定：");
        SendToPlayer(steamId64, result.VerificationUri ?? "未返回 verification_uri");
        SendToPlayer(steamId64, "你的四个验证词（不要发给别人）：");
        SendToPlayer(steamId64, string.Join("   ", result.Words));

        var pollInterval = Math.Clamp(result.PollIntervalSeconds, 2, 300);
        var maximumDeadline = DateTimeOffset.UtcNow.Add(MaximumLinkLifetime);
        var deadline = result.ExpiresAtUtc is { } expiresAtUtc && expiresAtUtc < maximumDeadline
            ? expiresAtUtc
            : maximumDeadline;
        StartLinkPolling(steamId64, result.LinkRequestId!, pollInterval, deadline);
    }

    private void StartLinkPolling(
        ulong steamId64,
        string linkRequestId,
        int pollIntervalSeconds,
        DateTimeOffset deadline)
    {
        var cancellation = new CancellationTokenSource();
        var context = new LinkPollContext(linkRequestId, cancellation);
        _linkPolls.AddOrUpdate(
            steamId64,
            context,
            (_, previous) =>
            {
                previous.Cancellation.Cancel();
                return context;
            });

        RunSafely(
            () => PollLinkUntilResolvedAsync(
                steamId64,
                context,
                TimeSpan.FromSeconds(Math.Clamp(pollIntervalSeconds, 2, 300)),
                deadline),
            nameof(PollLinkUntilResolvedAsync));
    }

    private async Task PollLinkUntilResolvedAsync(
        ulong steamId64,
        LinkPollContext context,
        TimeSpan pollInterval,
        DateTimeOffset deadline)
    {
        try
        {
            while (!context.Cancellation.IsCancellationRequested &&
                   DateTimeOffset.UtcNow < deadline)
            {
                await Task.Delay(pollInterval, context.Cancellation.Token).ConfigureAwait(false);
                var isOnline = await Server.NextFrameAsync(() => FindPlayer(steamId64) is not null)
                    .ConfigureAwait(false);
                if (!isOnline)
                {
                    return;
                }

                using var requestTimeout = CancellationTokenSource.CreateLinkedTokenSource(
                    context.Cancellation.Token);
                requestTimeout.CancelAfter(GatewayRequestTimeout);

                LinkPollResult poll;
                try
                {
                    poll = await _gateway.PollLinkAsync(
                            steamId64,
                            context.LinkRequestId,
                            requestTimeout.Token)
                        .ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (!context.Cancellation.IsCancellationRequested)
                {
                    continue;
                }
                catch (Exception exception)
                {
                    Logger.LogWarning(
                        exception,
                        "Link polling failed for {SteamId64}",
                        steamId64);
                    continue;
                }

                switch (poll.State)
                {
                    case LinkPollState.Approved:
                        SendToPlayer(steamId64, "SSG 账户绑定成功。输入 !shop 开始购买。");
                        return;
                    case LinkPollState.Rejected:
                        SendToPlayer(steamId64, "SSG 账户绑定被拒绝。");
                        return;
                    case LinkPollState.Expired:
                        SendToPlayer(steamId64, "SSG 绑定请求已过期，请重新输入 !link。");
                        return;
                    case LinkPollState.Failed:
                        if (poll.Failure?.Retryable == true)
                        {
                            continue;
                        }

                        SendToPlayer(
                            steamId64,
                            poll.Failure is null
                                ? "SSG 绑定查询失败。"
                                : GameErrorMessages.FromGateway(
                                    poll.Failure.Code.ToString(),
                                    poll.Failure.Message));
                        return;
                    case LinkPollState.Pending:
                        break;
                }
            }

            SendToPlayer(steamId64, "SSG 绑定等待超时，请重新输入 !link。");
        }
        catch (OperationCanceledException)
        {
            // Expected on disconnect, unload, or starting a replacement link.
        }
        finally
        {
            if (_linkPolls.TryGetValue(steamId64, out var current) &&
                ReferenceEquals(current, context))
            {
                _linkPolls.TryRemove(steamId64, out _);
            }

            context.Cancellation.Dispose();
        }
    }
}
