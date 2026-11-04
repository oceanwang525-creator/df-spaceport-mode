namespace DeltaForceMode.Domain;

public interface ILoadoutRepository
{
    Task<PlayerLoadoutState> GetAsync(ulong steamId64, CancellationToken cancellationToken);

    Task SaveAsync(PlayerLoadoutState state, CancellationToken cancellationToken);
}

