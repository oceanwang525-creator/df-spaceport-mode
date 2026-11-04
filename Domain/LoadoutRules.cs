namespace DeltaForceMode.Domain;

public sealed record LoadoutRuleResult(
    bool IsAllowed,
    bool ReplacesExisting,
    string ErrorCode,
    string Message)
{
    public static LoadoutRuleResult Allow(bool replacesExisting)
    {
        return new LoadoutRuleResult(true, replacesExisting, string.Empty, string.Empty);
    }

    public static LoadoutRuleResult Reject(string errorCode, string message)
    {
        return new LoadoutRuleResult(false, false, errorCode, message);
    }
}

public static class LoadoutRules
{
    public static LoadoutRuleResult Evaluate(
        PlayerLoadoutState state,
        WeaponItem item,
        WeaponCatalog catalog,
        int utilityLimit)
    {
        if (item.IsUtility)
        {
            var sameGroupCount = CountGroup(state, item, catalog);
            if (sameGroupCount >= item.MaxPerLife)
            {
                return LoadoutRuleResult.Reject(
                    "utility_limit",
                    $"{item.DisplayName} 已买满，本命最多 {item.MaxPerLife} 个。");
            }

            var utilityCount = state.Items.Count(entry => IsUtility(entry, catalog));
            if (utilityCount >= utilityLimit)
            {
                return LoadoutRuleResult.Reject(
                    "utility_limit",
                    $"投掷物已达到 {utilityLimit} 个上限。");
            }

            return LoadoutRuleResult.Allow(false);
        }

        var sameSlotCount = state.Items.Count(entry =>
        {
            var existing = catalog.Find(entry.ItemId);
            return existing is not null &&
                   existing.Slot.Equals(item.Slot, StringComparison.OrdinalIgnoreCase);
        });

        if (item.MaxPerLife < 1)
        {
            return LoadoutRuleResult.Reject("invalid_item", "商品配置无效。");
        }

        if (sameSlotCount >= item.MaxPerLife)
        {
            return sameSlotCount == 1 && item.MaxPerLife == 1
                ? LoadoutRuleResult.Allow(true)
                : LoadoutRuleResult.Reject("slot_limit", "该装备槽已达到上限。");
        }

        return LoadoutRuleResult.Allow(false);
    }

    public static void Grant(
        PlayerLoadoutState state,
        WeaponItem item,
        WeaponCatalog catalog,
        PendingPurchase pending,
        string? receiptId)
    {
        if (!item.IsUtility)
        {
            state.Items.RemoveAll(entry =>
            {
                var existing = catalog.Find(entry.ItemId);
                return existing is not null &&
                       existing.Slot.Equals(item.Slot, StringComparison.OrdinalIgnoreCase);
            });
        }

        state.Items.Add(new LoadoutEntry
        {
            ItemId = item.Id,
            PurchaseId = pending.PurchaseId,
            ReceiptId = receiptId,
            PurchasedAtUtc = DateTimeOffset.UtcNow
        });
        state.LifeActive = true;
        state.PendingPurchase = null;
        state.UpdatedAtUtc = DateTimeOffset.UtcNow;
    }

    private static int CountGroup(
        PlayerLoadoutState state,
        WeaponItem item,
        WeaponCatalog catalog)
    {
        return state.Items.Count(entry =>
        {
            var existing = catalog.Find(entry.ItemId);
            return existing is not null &&
                   existing.IsUtility &&
                   existing.LimitGroup.Equals(item.LimitGroup, StringComparison.OrdinalIgnoreCase);
        });
    }

    private static bool IsUtility(LoadoutEntry entry, WeaponCatalog catalog)
    {
        return catalog.Find(entry.ItemId)?.IsUtility == true;
    }
}
