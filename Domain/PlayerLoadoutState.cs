using System.Text.Json.Serialization;

namespace DeltaForceMode.Domain;

public sealed class LoadoutEntry
{
    [JsonPropertyName("itemId")]
    public string ItemId { get; set; } = string.Empty;

    [JsonPropertyName("purchaseId")]
    public string PurchaseId { get; set; } = string.Empty;

    [JsonPropertyName("receiptId")]
    public string? ReceiptId { get; set; }

    [JsonPropertyName("purchasedAtUtc")]
    public DateTimeOffset PurchasedAtUtc { get; set; } = DateTimeOffset.UtcNow;

    public LoadoutEntry Clone()
    {
        return new LoadoutEntry
        {
            ItemId = ItemId,
            PurchaseId = PurchaseId,
            ReceiptId = ReceiptId,
            PurchasedAtUtc = PurchasedAtUtc
        };
    }
}

public sealed class PendingPurchase
{
    [JsonPropertyName("purchaseId")]
    public string PurchaseId { get; set; } = string.Empty;

    [JsonPropertyName("itemId")]
    public string ItemId { get; set; } = string.Empty;

    [JsonPropertyName("amountCents")]
    public long AmountCents { get; set; }

    [JsonPropertyName("description")]
    public string Description { get; set; } = string.Empty;

    [JsonPropertyName("createdAtUtc")]
    public DateTimeOffset CreatedAtUtc { get; set; } = DateTimeOffset.UtcNow;

    public PendingPurchase Clone()
    {
        return new PendingPurchase
        {
            PurchaseId = PurchaseId,
            ItemId = ItemId,
            AmountCents = AmountCents,
            Description = Description,
            CreatedAtUtc = CreatedAtUtc
        };
    }
}

public sealed class PlayerLoadoutState
{
    [JsonPropertyName("steamId64")]
    public ulong SteamId64 { get; set; }

    [JsonPropertyName("lifeActive")]
    public bool LifeActive { get; set; }

    [JsonPropertyName("items")]
    public List<LoadoutEntry> Items { get; set; } = [];

    [JsonPropertyName("pendingPurchase")]
    public PendingPurchase? PendingPurchase { get; set; }

    [JsonPropertyName("updatedAtUtc")]
    public DateTimeOffset UpdatedAtUtc { get; set; } = DateTimeOffset.UtcNow;

    public PlayerLoadoutState Clone()
    {
        return new PlayerLoadoutState
        {
            SteamId64 = SteamId64,
            LifeActive = LifeActive,
            Items = Items.Select(item => item.Clone()).ToList(),
            PendingPurchase = PendingPurchase?.Clone(),
            UpdatedAtUtc = UpdatedAtUtc
        };
    }
}

public sealed class LoadoutDatabase
{
    [JsonPropertyName("version")]
    public int Version { get; set; } = 1;

    [JsonPropertyName("players")]
    public Dictionary<string, PlayerLoadoutState> Players { get; set; } = [];
}

