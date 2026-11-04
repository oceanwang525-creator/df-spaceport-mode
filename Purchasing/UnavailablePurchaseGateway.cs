namespace DeltaForceMode.Purchasing;

/// <summary>
/// Intentional integration point. Replace this class with the adapter that calls
/// the trusted backend service. Never put an SSG token or HMAC secret in the plugin.
/// </summary>
public sealed class UnavailablePurchaseGateway : IPurchaseGateway
{
    private static readonly GatewayFailure NotConfigured = new(
        GatewayErrorCode.Unavailable,
        "Purchase gateway is not configured.",
        Retryable: false);

    public bool IsConfigured => false;

    public Task<LinkStartResult> BeginLinkAsync(
        ulong steamId64,
        CancellationToken cancellationToken)
    {
        return Task.FromResult(new LinkStartResult(
            LinkRequestId: null,
            Words: [],
            VerificationUri: null,
            PollIntervalSeconds: 0,
            ExpiresAtUtc: null,
            Failure: NotConfigured));
    }

    public Task<LinkPollResult> PollLinkAsync(
        ulong steamId64,
        string linkRequestId,
        CancellationToken cancellationToken)
    {
        return Task.FromResult(new LinkPollResult(LinkPollState.Failed, NotConfigured));
    }

    public Task<BalanceResult> GetBalanceAsync(
        ulong steamId64,
        CancellationToken cancellationToken)
    {
        return Task.FromResult(new BalanceResult(false, null, NotConfigured));
    }

    public Task<PurchaseResult> DebitAsync(
        PurchaseRequest request,
        CancellationToken cancellationToken)
    {
        return Task.FromResult(new PurchaseResult(
            PurchaseState.Unknown,
            request.PurchaseId,
            DebitedAmountCents: null,
            ReceiptId: null,
            BalanceAfterCents: null,
            Failure: NotConfigured));
    }

    public Task<PurchaseResult> GetPurchaseAsync(
        PurchaseRequest request,
        CancellationToken cancellationToken)
    {
        return Task.FromResult(new PurchaseResult(
            PurchaseState.Unknown,
            request.PurchaseId,
            DebitedAmountCents: null,
            ReceiptId: null,
            BalanceAfterCents: null,
            Failure: NotConfigured));
    }
}

