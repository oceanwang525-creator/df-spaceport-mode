using System.Text.Json.Serialization;

namespace DeltaForceMode.Domain;

public sealed class CatalogCategory
{
    [JsonPropertyName("id")]
    public string Id { get; set; } = string.Empty;

    [JsonPropertyName("name")]
    public string Name { get; set; } = string.Empty;
}

public sealed class WeaponItem
{
    [JsonPropertyName("id")]
    public string Id { get; set; } = string.Empty;

    [JsonPropertyName("displayName")]
    public string DisplayName { get; set; } = string.Empty;

    [JsonPropertyName("category")]
    public string Category { get; set; } = string.Empty;

    [JsonPropertyName("slot")]
    public string Slot { get; set; } = string.Empty;

    [JsonPropertyName("limitGroup")]
    public string LimitGroup { get; set; } = string.Empty;

    [JsonPropertyName("priceCents")]
    public long PriceCents { get; set; }

    [JsonPropertyName("maxPerLife")]
    public int MaxPerLife { get; set; } = 1;

    [JsonPropertyName("gameItem")]
    public string GameItem { get; set; } = string.Empty;

    [JsonPropertyName("terroristGameItem")]
    public string? TerroristGameItem { get; set; }

    [JsonPropertyName("counterTerroristGameItem")]
    public string? CounterTerroristGameItem { get; set; }

    [JsonPropertyName("aliases")]
    public List<string> Aliases { get; set; } = [];

    [JsonIgnore]
    public bool IsArmor => Slot.Equals("armor", StringComparison.OrdinalIgnoreCase);

    [JsonIgnore]
    public bool IsUtility => Slot.Equals("utility", StringComparison.OrdinalIgnoreCase);

    public string GetGameItem(bool terrorist)
    {
        if (terrorist && !string.IsNullOrWhiteSpace(TerroristGameItem))
        {
            return TerroristGameItem;
        }

        if (!terrorist && !string.IsNullOrWhiteSpace(CounterTerroristGameItem))
        {
            return CounterTerroristGameItem;
        }

        return GameItem;
    }
}

public sealed class WeaponCatalog
{
    [JsonPropertyName("categories")]
    public List<CatalogCategory> Categories { get; set; } = [];

    [JsonPropertyName("items")]
    public List<WeaponItem> Items { get; set; } = [];

    [JsonIgnore]
    public IReadOnlyList<CatalogCategory> OrderedCategories => Categories;

    public WeaponItem? Find(string idOrAlias)
    {
        if (string.IsNullOrWhiteSpace(idOrAlias))
        {
            return null;
        }

        return Items.FirstOrDefault(item =>
            item.Id.Equals(idOrAlias, StringComparison.OrdinalIgnoreCase) ||
            item.Aliases.Any(alias => alias.Equals(idOrAlias, StringComparison.OrdinalIgnoreCase)));
    }

    public IEnumerable<WeaponItem> InCategory(string categoryId)
    {
        return Items.Where(item => item.Category.Equals(categoryId, StringComparison.OrdinalIgnoreCase));
    }

    public IReadOnlyList<string> Validate()
    {
        var errors = new List<string>();
        var categoryIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var itemIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var aliases = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var category in Categories)
        {
            if (string.IsNullOrWhiteSpace(category.Id) || string.IsNullOrWhiteSpace(category.Name))
            {
                errors.Add("Every category needs a non-empty id and name.");
                continue;
            }

            if (!categoryIds.Add(category.Id))
            {
                errors.Add($"Duplicate category id: {category.Id}");
            }
        }

        foreach (var item in Items)
        {
            if (string.IsNullOrWhiteSpace(item.Id) || string.IsNullOrWhiteSpace(item.DisplayName))
            {
                errors.Add("Every item needs a non-empty id and displayName.");
                continue;
            }

            if (!itemIds.Add(item.Id))
            {
                errors.Add($"Duplicate item id: {item.Id}");
            }

            if (!categoryIds.Contains(item.Category))
            {
                errors.Add($"Item {item.Id} references unknown category {item.Category}.");
            }

            if (!IsKnownSlot(item.Slot))
            {
                errors.Add($"Item {item.Id} has unsupported slot {item.Slot}.");
            }

            if (item.PriceCents <= 0)
            {
                errors.Add($"Item {item.Id} must have a positive priceCents value.");
            }

            if (item.MaxPerLife <= 0)
            {
                errors.Add($"Item {item.Id} must allow at least one copy per life.");
            }

            if (string.IsNullOrWhiteSpace(item.GameItem) &&
                string.IsNullOrWhiteSpace(item.TerroristGameItem) &&
                string.IsNullOrWhiteSpace(item.CounterTerroristGameItem))
            {
                errors.Add($"Item {item.Id} has no CS2 gameItem.");
            }

            foreach (var alias in item.Aliases)
            {
                if (!aliases.Add(alias) || itemIds.Contains(alias))
                {
                    errors.Add($"Duplicate item alias: {alias}");
                }
            }
        }

        return errors;
    }

    private static bool IsKnownSlot(string slot)
    {
        return slot.Equals("primary", StringComparison.OrdinalIgnoreCase)
               || slot.Equals("secondary", StringComparison.OrdinalIgnoreCase)
               || slot.Equals("armor", StringComparison.OrdinalIgnoreCase)
               || slot.Equals("utility", StringComparison.OrdinalIgnoreCase)
               || slot.Equals("taser", StringComparison.OrdinalIgnoreCase);
    }
}

