using System;
using System.Collections.Generic;

namespace Robust.Shared.GameObjects;

public sealed class GuidEntityIndex
{
    private readonly Dictionary<Guid, EntityUid> _uidById = new();
    private readonly Dictionary<EntityUid, Guid> _idByUid = new();
    private readonly HashSet<Guid> _tombstones = new();

    public Guid Ensure(EntityUid uid, MetaDataComponent meta)
    {
        if (meta.Guid == Guid.Empty)
            meta.Guid = Guid.NewGuid();

        _uidById[meta.Guid] = uid;
        _idByUid[uid] = meta.Guid;
        _tombstones.Remove(meta.Guid);
        return meta.Guid;
    }

    public bool TryGetReference(EntityUid uid, out Guid id)
        => _idByUid.TryGetValue(uid, out id);

    public bool TryGetLive(Guid id, out EntityUid uid)
    {
        if (_uidById.TryGetValue(id, out uid) && !_tombstones.Contains(id))
            return true;

        uid = default;
        return false;
    }

    public EntityUid ResolveReference(Guid id, Func<EntityUid> reserve)
    {
        if (_uidById.TryGetValue(id, out var uid))
            return uid;

        uid = reserve();
        _uidById[id] = uid;
        _idByUid[uid] = id;
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
        _idByUid[uid] = id;
        _tombstones.Remove(id);
        return uid;
    }

    public void Adopt(EntityUid uid, MetaDataComponent meta, Guid id)
    {
        meta.Guid = id;
        _uidById[id] = uid;
        _idByUid[uid] = id;
        _tombstones.Remove(id);
    }

    public void Tombstone(EntityUid uid, MetaDataComponent meta)
    {
        if (meta.Guid == Guid.Empty)
            return;

        _uidById[meta.Guid] = uid;
        _idByUid[uid] = meta.Guid;
        _tombstones.Add(meta.Guid);
    }
}
