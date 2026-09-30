using System;
using System.Collections.Generic;
using Robust.Shared.Map;
using Robust.Shared.Map.Components;
using Robust.Shared.Maths;

namespace Robust.Shared.GameObjects;

public partial class SharedMapSystem
{
    public ushort GetChunkSize(MapGridComponent grid) => grid.ChunkSize;
    public IEnumerable<Vector2i> EnumerateChunkIndices(EntityUid uid, MapGridComponent grid)
    {
        return grid.Chunks.Keys;
    }

    public bool TryCopyChunkTiles(
        EntityUid uid,
        MapGridComponent grid,
        Vector2i chunkIndices,
        Span<Tile> destination)
    {
        if (!TryGetChunk(uid, grid, chunkIndices, out var chunk))
            return false;

        var size = chunk.ChunkSize;
        var needed = size * size;
        if (destination.Length < needed)
            throw new ArgumentException($"Destination span too small: need {needed}, got {destination.Length}");

        var i = 0;
        for (ushort y = 0; y < size; y++)
        {
            for (ushort x = 0; x < size; x++)
                destination[i++] = chunk.GetTile(x, y);
        }

        return true;
    }

    public void GetAnchoredEntitiesInChunk(
        EntityUid uid,
        MapGridComponent grid,
        Vector2i chunkIndices,
        HashSet<EntityUid> into)
    {
        if (!TryGetChunk(uid, grid, chunkIndices, out var chunk))
            return;

        var size = chunk.ChunkSize;
        var originX = chunkIndices.X * size;
        var originY = chunkIndices.Y * size;

        var scratch = new List<EntityUid>();
        for (ushort y = 0; y < size; y++)
        {
            for (ushort x = 0; x < size; x++)
            {
                var tile = new Vector2i(originX + x, originY + y);
                GetAnchoredEntities((uid, grid), tile, scratch);
                foreach (var ent in scratch)
                    into.Add(ent);
                scratch.Clear();
            }
        }
    }
}
