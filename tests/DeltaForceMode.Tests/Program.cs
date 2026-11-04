using System.Text.Json;
using DeltaForceMode;
using DeltaForceMode.Domain;
using DeltaForceMode.Persistence;
using DeltaForceMode.Purchasing;

var failures = 0;

await Run("catalog loads martin prices as integer cents", async () =>
{
    var (config, _) = LoadConfig();
    Assert.Equal(42, config.Catalog.Items.Count);
    Assert.Equal(310_000L, config.Catalog.Find("ak47")!.PriceCents);
    Assert.Equal(500_000L, config.Catalog.Find("awp")!.PriceCents);
    Assert.Equal(5_000L, config.Catalog.Find("decoy")!.PriceCents);
    Assert.Equal(40_000L, config.Catalog.Find("fire")!.PriceCents);

    var expectedPrices = new Dictionary<string, long>
    {
        ["glock"] = 20_000,
        ["p2000"] = 25_000,
        ["usp_s"] = 25_000,
        ["p250"] = 30_000,
        ["dual_berettas"] = 30_000,
        ["five_seven"] = 50_000,
        ["cz75"] = 50_000,
        ["tec9"] = 60_000,
        ["r8"] = 60_000,
        ["deagle"] = 70_000,
        ["mac10"] = 75_000,
        ["ump45"] = 90_000,
        ["mp9"] = 95_000,
        ["bizon"] = 100_000,
        ["mp7"] = 110_000,
        ["mp5sd"] = 110_000,
        ["p90"] = 235_000,
        ["nova"] = 75_000,
        ["sawedoff"] = 80_000,
        ["mag7"] = 100_000,
        ["xm1014"] = 170_000,
        ["galil"] = 190_000,
        ["famas"] = 190_000,
        ["m4a4"] = 290_000,
        ["m4a1_s"] = 290_000,
        ["aug"] = 300_000,
        ["ak47"] = 310_000,
        ["sg553"] = 330_000,
        ["negev"] = 170_000,
        ["m249"] = 520_000,
        ["ssg08"] = 170_000,
        ["awp"] = 500_000,
        ["g3sg1"] = 500_000,
        ["scar20"] = 500_000,
        ["kevlar"] = 65_000,
        ["assaultsuit"] = 100_000,
        ["taser"] = 20_000,
        ["decoy"] = 5_000,
        ["flash"] = 20_000,
        ["he"] = 30_000,
        ["smoke"] = 30_000,
        ["fire"] = 40_000
    };

    Assert.Equal(expectedPrices.Count, config.Catalog.Items.Count);
    foreach (var (itemId, priceCents) in expectedPrices)
    {
        Assert.Equal(priceCents, config.Catalog.Find(itemId)?.PriceCents);
    }

    await Task.CompletedTask;
});

await Run("completed debit grants exactly one loadout item", async () =>
{
    var (config, catalog) = LoadConfig();
    var repository = new MemoryRepository();
    var gateway = new FakeGateway
    {
        DebitHandler = request => Completed(request)
    };
    var coordinator = new PurchaseCoordinator(repository, gateway, catalog, config.UtilityLimit);

    var attempt = await coordinator.PurchaseAsync(76561198000000001, "ak47", CancellationToken.None);

    Assert.Equal(PurchaseAttemptStatus.Completed, attempt.Status);
    Assert.Equal(1, gateway.DebitCallCount);
    var state = await repository.GetAsync(76561198000000001, CancellationToken.None);
    Assert.Equal(1, state.Items.Count);
    Assert.Equal("ak47", state.Items[0].ItemId);
    Assert.Equal(attempt.PurchaseId, state.Items[0].PurchaseId);
    Assert.True(state.LifeActive);
    Assert.Null(state.PendingPurchase);
});

await Run("unknown debit does not grant item and keeps original key", async () =>
{
    var (config, catalog) = LoadConfig();
    var repository = new MemoryRepository();
    var gateway = new FakeGateway
    {
        DebitHandler = request => new PurchaseResult(
            PurchaseState.Unknown,
            request.PurchaseId,
            DebitedAmountCents: null,
            ReceiptId: null,
            BalanceAfterCents: null,
            new GatewayFailure(GatewayErrorCode.Timeout, "timeout"))
    };
    var coordinator = new PurchaseCoordinator(repository, gateway, catalog, config.UtilityLimit);

    var attempt = await coordinator.PurchaseAsync(76561198000000002, "awp", CancellationToken.None);

    Assert.Equal(PurchaseAttemptStatus.PendingReconciliation, attempt.Status);
    var state = await repository.GetAsync(76561198000000002, CancellationToken.None);
    Assert.Equal(0, state.Items.Count);
    Assert.Equal(attempt.PurchaseId, state.PendingPurchase?.PurchaseId);
    Assert.Equal(500_000L, state.PendingPurchase?.AmountCents);
});

await Run("unconfigured gateway creates no fake pending charge", async () =>
{
    var (config, catalog) = LoadConfig();
    var repository = new MemoryRepository();
    var gateway = new FakeGateway { IsConfigured = false };
    var coordinator = new PurchaseCoordinator(repository, gateway, catalog, config.UtilityLimit);

    var attempt = await coordinator.PurchaseAsync(
        76561198000000008,
        "ak47",
        CancellationToken.None);

    Assert.Equal(PurchaseAttemptStatus.Rejected, attempt.Status);
    Assert.Equal(GatewayErrorCodes.GatewayNotConfigured, attempt.ErrorCode);
    Assert.Equal(0, gateway.DebitCallCount);
    var state = await repository.GetAsync(76561198000000008, CancellationToken.None);
    Assert.Null(state.PendingPurchase);
});

await Run("reconcile uses the same idempotency key and grants once", async () =>
{
    var (config, catalog) = LoadConfig();
    var repository = new MemoryRepository();
    var gateway = new FakeGateway
    {
        DebitHandler = request => new PurchaseResult(
            PurchaseState.Unknown,
            request.PurchaseId,
            null,
            null,
            null,
            new GatewayFailure(GatewayErrorCode.Timeout, "timeout")),
        GetPurchaseHandler = request => Completed(request)
    };
    var coordinator = new PurchaseCoordinator(repository, gateway, catalog, config.UtilityLimit);

    var first = await coordinator.PurchaseAsync(76561198000000003, "m4a1_s", CancellationToken.None);
    var reconciled = await coordinator.ReconcileAsync(76561198000000003, CancellationToken.None);

    Assert.Equal(PurchaseAttemptStatus.PendingReconciliation, first.Status);
    Assert.Equal(PurchaseAttemptStatus.Completed, reconciled?.Status);
    Assert.Equal(1, gateway.DebitCallCount);
    Assert.Equal(1, gateway.GetPurchaseCallCount);
    Assert.Equal(
        gateway.DebitRequests[0].PurchaseId,
        gateway.GetPurchaseRequests[0].PurchaseId);
    var state = await repository.GetAsync(76561198000000003, CancellationToken.None);
    Assert.Equal(1, state.Items.Count);
    Assert.Null(state.PendingPurchase);
});

await Run("gateway exception is treated as uncertain, not rejected", async () =>
{
    var (config, catalog) = LoadConfig();
    var repository = new MemoryRepository();
    var gateway = new FakeGateway
    {
        DebitHandler = _ => throw new TimeoutException("network timeout")
    };
    var coordinator = new PurchaseCoordinator(repository, gateway, catalog, config.UtilityLimit);

    var attempt = await coordinator.PurchaseAsync(76561198000000004, "p250", CancellationToken.None);

    Assert.Equal(PurchaseAttemptStatus.PendingReconciliation, attempt.Status);
    var state = await repository.GetAsync(76561198000000004, CancellationToken.None);
    Assert.Equal(0, state.Items.Count);
    Assert.NotNull(state.PendingPurchase);
});

await Run("new primary replaces old primary only after confirmation", async () =>
{
    var (config, catalog) = LoadConfig();
    var repository = new MemoryRepository();
    var seed = new PlayerLoadoutState
    {
        SteamId64 = 76561198000000005,
        LifeActive = true,
        Items =
        [
            new LoadoutEntry
            {
                ItemId = "ak47",
                PurchaseId = "old"
            }
        ]
    };
    await repository.SaveAsync(seed, CancellationToken.None);
    var gateway = new FakeGateway
    {
        DebitHandler = request => Completed(request)
    };
    var coordinator = new PurchaseCoordinator(repository, gateway, catalog, config.UtilityLimit);

    var attempt = await coordinator.PurchaseAsync(76561198000000005, "m4a4", CancellationToken.None);

    Assert.Equal(PurchaseAttemptStatus.Completed, attempt.Status);
    Assert.True(attempt.ReplacesExisting);
    var state = await repository.GetAsync(76561198000000005, CancellationToken.None);
    Assert.Equal(1, state.Items.Count);
    Assert.Equal("m4a4", state.Items[0].ItemId);
});

await Run("utility limit is checked before any debit", async () =>
{
    var (config, catalog) = LoadConfig();
    var repository = new MemoryRepository();
    var seed = new PlayerLoadoutState
    {
        SteamId64 = 76561198000000006,
        LifeActive = true,
        Items =
        [
            Utility("flash", "u1"),
            Utility("flash", "u2"),
            Utility("he", "u3"),
            Utility("smoke", "u4")
        ]
    };
    await repository.SaveAsync(seed, CancellationToken.None);
    var gateway = new FakeGateway
    {
        DebitHandler = request => Completed(request)
    };
    var coordinator = new PurchaseCoordinator(repository, gateway, catalog, config.UtilityLimit);

    var attempt = await coordinator.PurchaseAsync(76561198000000006, "decoy", CancellationToken.None);

    Assert.Equal(PurchaseAttemptStatus.Rejected, attempt.Status);
    Assert.Equal(0, gateway.DebitCallCount);
});

await Run("pending purchase survives store restart", async () =>
{
    var root = Path.Combine(Path.GetTempPath(), $"delta-force-tests-{Guid.NewGuid():N}");
    var path = Path.Combine(root, "loadouts.json");
    try
    {
        const ulong steamId = 76561198000000007;
        using (var store = new LoadoutStore(path))
        {
            var state = await store.GetAsync(steamId, CancellationToken.None);
            state.PendingPurchase = new PendingPurchase
            {
                PurchaseId = "abc123",
                ItemId = "ak47",
                AmountCents = 310_000,
                Description = "test"
            };
            await store.SaveAsync(state, CancellationToken.None);
        }

        using (var store = new LoadoutStore(path))
        {
            var state = await store.GetAsync(steamId, CancellationToken.None);
            Assert.Equal("abc123", state.PendingPurchase?.PurchaseId);
            Assert.Equal(310_000L, state.PendingPurchase?.AmountCents);
        }
    }
    finally
    {
        if (Directory.Exists(root))
        {
            Directory.Delete(root, recursive: true);
        }
    }
});

await Run("new battle royale round clears loadout but preserves pending payment", async () =>
{
    var root = Path.Combine(Path.GetTempPath(), $"delta-force-round-tests-{Guid.NewGuid():N}");
    var path = Path.Combine(root, "loadouts.json");
    try
    {
        const ulong steamId = 76561198000000009;
        using (var store = new LoadoutStore(path))
        {
            var state = await store.GetAsync(steamId, CancellationToken.None);
            state.LifeActive = true;
            state.Items.Add(new LoadoutEntry { ItemId = "ak47", PurchaseId = "paid" });
            state.PendingPurchase = new PendingPurchase
            {
                PurchaseId = "pending",
                ItemId = "awp",
                AmountCents = 500_000,
                Description = "test pending"
            };
            await store.SaveAsync(state, CancellationToken.None);
            await store.ResetAllLivesAsync(CancellationToken.None);
        }

        using (var store = new LoadoutStore(path))
        {
            var state = await store.GetAsync(steamId, CancellationToken.None);
            Assert.Equal(0, state.Items.Count);
            Assert.True(!state.LifeActive);
            Assert.Equal("pending", state.PendingPurchase?.PurchaseId);
        }
    }
    finally
    {
        if (Directory.Exists(root))
        {
            Directory.Delete(root, recursive: true);
        }
    }
});

Console.WriteLine();
if (failures == 0)
{
    Console.WriteLine("All Delta Force game-side tests passed.");
    return 0;
}

Console.WriteLine($"{failures} test(s) failed.");
return 1;

async Task Run(string name, Func<Task> test)
{
    try
    {
        await test();
        Console.WriteLine($"PASS {name}");
    }
    catch (Exception exception)
    {
        failures++;
        Console.WriteLine($"FAIL {name}: {exception.Message}");
    }
}

static (DeltaForceConfig Config, WeaponCatalog Catalog) LoadConfig()
{
    var path = Path.Combine(AppContext.BaseDirectory, "config", "DeltaForceMode.json");
    var config = JsonSerializer.Deserialize<DeltaForceConfig>(
        File.ReadAllText(path),
        new JsonSerializerOptions { PropertyNameCaseInsensitive = true })!;
    var errors = config.Catalog.Validate();
    if (errors.Count > 0)
    {
        throw new InvalidOperationException(string.Join("; ", errors));
    }

    return (config, config.Catalog);
}

static PurchaseResult Completed(PurchaseRequest request)
{
    return new PurchaseResult(
        PurchaseState.Completed,
        request.PurchaseId,
        request.AmountCents,
        $"receipt-{request.PurchaseId}",
        999_999,
        Failure: null);
}

static LoadoutEntry Utility(string itemId, string purchaseId)
{
    return new LoadoutEntry
    {
        ItemId = itemId,
        PurchaseId = purchaseId
    };
}

internal sealed class MemoryRepository : ILoadoutRepository
{
    private readonly Dictionary<ulong, PlayerLoadoutState> _states = [];

    public Task<PlayerLoadoutState> GetAsync(ulong steamId64, CancellationToken cancellationToken)
    {
        var state = _states.TryGetValue(steamId64, out var existing)
            ? existing.Clone()
            : new PlayerLoadoutState { SteamId64 = steamId64 };
        return Task.FromResult(state);
    }

    public Task SaveAsync(PlayerLoadoutState state, CancellationToken cancellationToken)
    {
        _states[state.SteamId64] = state.Clone();
        return Task.CompletedTask;
    }
}

internal sealed class FakeGateway : IPurchaseGateway
{
    public bool IsConfigured { get; init; } = true;

    public Func<PurchaseRequest, PurchaseResult> DebitHandler { get; init; } =
        request => throw new InvalidOperationException("Debit handler not configured.");

    public Func<PurchaseRequest, PurchaseResult> GetPurchaseHandler { get; init; } =
        request => throw new InvalidOperationException("GetPurchase handler not configured.");

    public int DebitCallCount { get; private set; }
    public int GetPurchaseCallCount { get; private set; }
    public List<PurchaseRequest> DebitRequests { get; } = [];
    public List<PurchaseRequest> GetPurchaseRequests { get; } = [];

    public Task<LinkStartResult> BeginLinkAsync(
        ulong steamId64,
        CancellationToken cancellationToken)
    {
        throw new NotSupportedException();
    }

    public Task<LinkPollResult> PollLinkAsync(
        ulong steamId64,
        string linkRequestId,
        CancellationToken cancellationToken)
    {
        throw new NotSupportedException();
    }

    public Task<BalanceResult> GetBalanceAsync(
        ulong steamId64,
        CancellationToken cancellationToken)
    {
        throw new NotSupportedException();
    }

    public Task<PurchaseResult> DebitAsync(
        PurchaseRequest request,
        CancellationToken cancellationToken)
    {
        DebitCallCount++;
        DebitRequests.Add(request);
        return Task.FromResult(DebitHandler(request));
    }

    public Task<PurchaseResult> GetPurchaseAsync(
        PurchaseRequest request,
        CancellationToken cancellationToken)
    {
        GetPurchaseCallCount++;
        GetPurchaseRequests.Add(request);
        return Task.FromResult(GetPurchaseHandler(request));
    }
}

internal static class Assert
{
    public static void True(bool value)
    {
        if (!value)
        {
            throw new InvalidOperationException("Expected true.");
        }
    }

    public static void Null(object? value)
    {
        if (value is not null)
        {
            throw new InvalidOperationException("Expected null.");
        }
    }

    public static void NotNull(object? value)
    {
        if (value is null)
        {
            throw new InvalidOperationException("Expected a non-null value.");
        }
    }

    public static void Equal<T>(T expected, T actual)
    {
        if (!EqualityComparer<T>.Default.Equals(expected, actual))
        {
            throw new InvalidOperationException($"Expected {expected}, got {actual}.");
        }
    }
}
