using System.Text.Json.Serialization;
using DeltaForceMode.Domain;

namespace DeltaForceMode;

public sealed class DeltaForceConfig
{
    [JsonPropertyName("version")]
    public int Version { get; set; } = 1;

    [JsonPropertyName("buyWindowOnly")]
    public bool BuyWindowOnly { get; set; } = true;

    [JsonPropertyName("stripUnboughtWeapons")]
    public bool StripUnboughtWeapons { get; set; } = true;

    [JsonPropertyName("clearLoadoutOnDeath")]
    public bool ClearLoadoutOnDeath { get; set; } = true;

    [JsonPropertyName("utilityLimit")]
    public int UtilityLimit { get; set; } = 4;

    [JsonPropertyName("autoReconcilePendingPurchases")]
    public bool AutoReconcilePendingPurchases { get; set; } = true;

    [JsonPropertyName("applyBattleRoyaleServerRules")]
    public bool ApplyBattleRoyaleServerRules { get; set; } = true;

    [JsonPropertyName("buyTimeSeconds")]
    public int BuyTimeSeconds { get; set; } = 60;

    [JsonPropertyName("roundTimeMinutes")]
    public int RoundTimeMinutes { get; set; } = 25;

    [JsonPropertyName("minimumParticipants")]
    public int MinimumParticipants { get; set; } = 2;

    [JsonPropertyName("extraction")]
    public ExtractionSettings Extraction { get; set; } = new();

    [JsonPropertyName("catalog")]
    public WeaponCatalog Catalog { get; set; } = new();
}

public sealed class ExtractionSettings
{
    [JsonPropertyName("enabled")]
    public bool Enabled { get; set; }

    [JsonPropertyName("siteIds")]
    public List<string> SiteIds { get; set; } = [];
}

