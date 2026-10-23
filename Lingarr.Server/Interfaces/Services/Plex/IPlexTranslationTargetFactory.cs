using Lingarr.Server.Models.Plex;

namespace Lingarr.Server.Interfaces.Services.Plex;

public interface IPlexTranslationTargetFactory
{
    Task<PlexTranslationTarget?> TryCreateAsync(
        PlexMediaResolveResult resolved,
        CancellationToken cancellationToken = default);
}
