namespace DeltaForceMode.Purchasing;

public enum GatewayErrorCode
{
    None,
    NotLinked,
    InsufficientFunds,
    LimitExceeded,
    Revoked,
    InvalidRequest,
    RateLimited,
    Timeout,
    Unavailable,
    Unknown
}

public sealed record GatewayFailure(
    GatewayErrorCode Code,
    string Message,
    bool Retryable = false);

public sealed record LinkStartResult(
    string? LinkRequestId,
    IReadOnlyList<string> Words,
    string? VerificationUri,
    int PollIntervalSeconds,
    DateTimeOffset? ExpiresAtUtc,
    GatewayFailure? Failure)
{
    public bool IsSuccess => Failure is null && !string.IsNullOrWhiteSpace(LinkRequestId);
}

public enum LinkPollState
{
    Pending,
    Approved,
    Rejected,
    Expired,
    Failed
}

public sealed record LinkPollResult(
    LinkPollState State,
    GatewayFailure? Failure);

public sealed record BalanceResult(
    bool IsLinked,
    long? BalanceCents,
    GatewayFailure? Failure);

public sealed record PurchaseRequest(
    string PurchaseId,
    ulong SteamId64,
    string ItemId,
    long AmountCents,
    string Description);

public enum PurchaseState
{
    Completed,
    Rejected,
    Pending,
    Unknown
}

public sealed record PurchaseResult(
    PurchaseState State,
    string PurchaseId,
    long? DebitedAmountCents,
    string? ReceiptId,
    long? BalanceAfterCents,
    GatewayFailure? Failure);

public interface IPurchaseGateway
{
    /// <summary>
    /// Returns true only when the trusted backend endpoint and plugin credential
    /// have both been configured by the adapter.
    /// </summary>
    bool IsConfigured { get; }

    Task<LinkStartResult> BeginLinkAsync(ulong steamId64, CancellationToken cancellationToken);

    Task<LinkPollResult> PollLinkAsync(
        ulong steamId64,
        string linkRequestId,
        CancellationToken cancellationToken);

    Task<BalanceResult> GetBalanceAsync(ulong steamId64, CancellationToken cancellationToken);

    /// <summary>
    /// The adapter must use PurchaseRequest.PurchaseId as the SSG idempotency key.
    /// It may return Completed only after SSG confirms the debit.
    /// A timeout or transport failure must return Unknown, never Rejected.
    /// </summary>
    Task<PurchaseResult> DebitAsync(
        PurchaseRequest request,
        CancellationToken cancellationToken);

    /// <summary>
    /// Reconciles an uncertain debit by the original PurchaseId.
    /// This method must not create a second charge.
    /// </summary>
    Task<PurchaseResult> GetPurchaseAsync(
        PurchaseRequest request,
        CancellationToken cancellationToken);
}

public static class GatewayErrorCodes
{
    public const string GatewayNotConfigured = "gateway_not_configured";
    public const string LocalItemNotFound = "local_item_not_found";
    public const string PurchaseAlreadyPending = "purchase_already_pending";
    public const string LocalSlotConflict = "local_slot_conflict";
    public const string PurchaseIdentityMismatch = "purchase_identity_mismatch";
    public const string GatewayException = "gateway_exception";
}

