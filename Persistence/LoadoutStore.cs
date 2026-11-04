using System.Text.Json;
using DeltaForceMode.Domain;

namespace DeltaForceMode.Persistence;

public sealed class LoadoutStore : ILoadoutRepository, IDisposable
{
    private readonly string _filePath;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly JsonSerializerOptions _jsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        WriteIndented = true
    };

    private LoadoutDatabase? _database;

    public LoadoutStore(string filePath)
    {
        _filePath = filePath;
    }

    public async Task<PlayerLoadoutState> GetAsync(
        ulong steamId64,
        CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var database = await LoadDatabaseAsync(cancellationToken).ConfigureAwait(false);
            var key = steamId64.ToString();
            if (!database.Players.TryGetValue(key, out var state))
            {
                state = new PlayerLoadoutState
                {
                    SteamId64 = steamId64
                };
            }

            return state.Clone();
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task SaveAsync(
        PlayerLoadoutState state,
        CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var database = await LoadDatabaseAsync(cancellationToken).ConfigureAwait(false);
            var next = CloneDatabase(database);
            state.UpdatedAtUtc = DateTimeOffset.UtcNow;
            next.Players[state.SteamId64.ToString()] = state.Clone();
            await SaveDatabaseAsync(next, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<bool> ClearOnDeathAsync(
        ulong steamId64,
        CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var database = await LoadDatabaseAsync(cancellationToken).ConfigureAwait(false);
            if (!database.Players.TryGetValue(steamId64.ToString(), out var state) ||
                !state.LifeActive ||
                state.Items.Count == 0)
            {
                return false;
            }

            var next = CloneDatabase(database);
            var nextState = next.Players[steamId64.ToString()];
            nextState.Items.Clear();
            nextState.LifeActive = false;
            nextState.UpdatedAtUtc = DateTimeOffset.UtcNow;
            await SaveDatabaseAsync(next, cancellationToken).ConfigureAwait(false);
            return true;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task ResetAllLivesAsync(CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var database = await LoadDatabaseAsync(cancellationToken).ConfigureAwait(false);
            var next = CloneDatabase(database);
            var changed = false;
            foreach (var state in next.Players.Values)
            {
                if (state.Items.Count == 0 && !state.LifeActive)
                {
                    continue;
                }

                state.Items.Clear();
                state.LifeActive = false;
                state.UpdatedAtUtc = DateTimeOffset.UtcNow;
                changed = true;
            }

            if (changed)
            {
                await SaveDatabaseAsync(next, cancellationToken).ConfigureAwait(false);
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task<LoadoutDatabase> LoadDatabaseAsync(CancellationToken cancellationToken)
    {
        if (_database is not null)
        {
            return _database;
        }

        if (!File.Exists(_filePath))
        {
            _database = new LoadoutDatabase();
            return _database;
        }

        await using var stream = new FileStream(
            _filePath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            bufferSize: 16 * 1024,
            FileOptions.Asynchronous | FileOptions.SequentialScan);

        _database = await JsonSerializer.DeserializeAsync<LoadoutDatabase>(
            stream,
            _jsonOptions,
            cancellationToken).ConfigureAwait(false) ?? new LoadoutDatabase();

        return _database;
    }

    private async Task SaveDatabaseAsync(
        LoadoutDatabase database,
        CancellationToken cancellationToken)
    {
        var directory = Path.GetDirectoryName(_filePath);
        if (!string.IsNullOrWhiteSpace(directory))
        {
            Directory.CreateDirectory(directory);
        }

        var temporaryPath = $"{_filePath}.{Guid.NewGuid():N}.tmp";
        try
        {
            await using (var stream = new FileStream(
                             temporaryPath,
                             FileMode.CreateNew,
                             FileAccess.Write,
                             FileShare.None,
                             bufferSize: 16 * 1024,
                             FileOptions.Asynchronous | FileOptions.WriteThrough))
            {
                await JsonSerializer.SerializeAsync(
                    stream,
                    database,
                    _jsonOptions,
                    cancellationToken).ConfigureAwait(false);
                await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
            }

            File.Move(temporaryPath, _filePath, overwrite: true);
            _database = database;
        }
        finally
        {
            if (File.Exists(temporaryPath))
            {
                File.Delete(temporaryPath);
            }
        }
    }

    private static LoadoutDatabase CloneDatabase(LoadoutDatabase database)
    {
        return new LoadoutDatabase
        {
            Version = database.Version,
            Players = database.Players.ToDictionary(
                pair => pair.Key,
                pair => pair.Value.Clone(),
                StringComparer.Ordinal)
        };
    }

    public void Dispose()
    {
        _gate.Dispose();
    }
}
