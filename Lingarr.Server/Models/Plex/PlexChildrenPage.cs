namespace Lingarr.Server.Models.Plex;

public sealed class PlexChildrenPage
{
    public IReadOnlyList<PlexMetadata> Items { get; init; } = [];

    public int Size { get; init; }

    public int TotalSize { get; init; }

    public int Offset { get; init; }
}
