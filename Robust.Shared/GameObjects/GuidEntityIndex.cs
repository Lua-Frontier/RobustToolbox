using System;
using System.Collections.Generic;

namespace Robust.Shared.GameObjects;

public sealed class GuidEntityIndex
{
    private readonly Dictionary<Guid, EntityUid> _uidById = new();
    private readonly HashSet<Guid> _tombstones = new();

    public Guid Ensure(EntityUid uid, MetaDataComponent meta)
    {
        if (meta.Guid == Guid.Empty)
            meta.Guid = Guid.NewGuid();

        _uidById[meta.Guid] = uid;
        _tombstones.Remove(meta.Guid);
        return meta.Guid;
    }

    public EntityUid ResolveReference(Guid id, Func<EntityUid> reserve)
    {
        if (_uidById.TryGetValue(id, out var uid))
            return uid;

        uid = reserve();
        _uidById[id] = uid;
        return uid;
    }

    public EntityUid PrepareLoad(Guid id, Func<EntityUid, bool> isLive, Func<EntityUid> reserve, out bool alreadyLive)
    {
        alreadyLive = false;
        if (_uidById.TryGetValue(id, out var uid))
        {
            if (isLive(uid))
            {
                alreadyLive = true;
                return uid;
            }

            _tombstones.Remove(id);
            return uid;
        }

        uid = reserve();
        _uidById[id] = uid;
        _tombstones.Remove(id);
        return uid;
    }

    public void Adopt(EntityUid uid, MetaDataComponent meta, Guid id)
    {
        meta.Guid = id;
        _uidById[id] = uid;
        _tombstones.Remove(id);
    }

    public void Tombstone(EntityUid uid, MetaDataComponent meta)
    {
        if (meta.Guid == Guid.Empty)
            return;

        _uidById[meta.Guid] = uid;
        _tombstones.Add(meta.Guid);
    }
}
