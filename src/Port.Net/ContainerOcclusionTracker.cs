using Robust.Shared.Network;

namespace Port.Net;

/// <summary>
/// Tracks entities inside closed containers so ghost viewport can hide their contents.
/// </summary>
public sealed class ContainerOcclusionTracker
{
    readonly Dictionary<NetEntity, NetEntity> _childToContainer = new();
    readonly Dictionary<NetEntity, bool> _containerShowContents = new();

    public void Clear()
    {
        _childToContainer.Clear();
        _containerShowContents.Clear();
    }

    public void Remove(NetEntity ent)
    {
        _childToContainer.Remove(ent);
        _containerShowContents.Remove(ent);
    }

    public bool TryApplyComponentState(NetEntity ent, object state)
    {
        var tn = state.GetType().Name;
        if (tn.Contains("ContainerManager", StringComparison.Ordinal))
            return ApplyManager(ent, state);
        if (tn.Contains("Container", StringComparison.Ordinal) && !tn.Contains("Manager", StringComparison.Ordinal))
            return ApplySingle(ent, state);
        return false;
    }

    bool ApplyManager(NetEntity ent, object state)
    {
        var t = state.GetType();
        var containers = t.GetProperty("Containers")?.GetValue(state)
                         ?? t.GetField("Containers")?.GetValue(state);
        if (containers is not System.Collections.IEnumerable en)
            return false;

        foreach (var c in en)
        {
            if (c is null) continue;
            var ct = c.GetType();
            var show = ct.GetProperty("ShowContents")?.GetValue(c) as bool?
                       ?? ct.GetField("ShowContents")?.GetValue(c) as bool?
                       ?? false;
            _containerShowContents[ent] = show;

            var contained = ct.GetProperty("Contained")?.GetValue(c)
                            ?? ct.GetField("Contained")?.GetValue(c);
            if (contained is System.Collections.IEnumerable kids)
            {
                foreach (var kid in kids)
                {
                    if (kid is NetEntity ne && ne.IsValid())
                        _childToContainer[ne] = ent;
                }
            }
        }

        return true;
    }

    bool ApplySingle(NetEntity ent, object state)
    {
        var t = state.GetType();
        var show = t.GetProperty("ShowContents")?.GetValue(state) as bool?
                   ?? t.GetField("ShowContents")?.GetValue(state) as bool?
                   ?? false;
        _containerShowContents[ent] = show;

        var contained = t.GetProperty("Contained")?.GetValue(state)
                        ?? t.GetField("Contained")?.GetValue(state);
        if (contained is System.Collections.IEnumerable kids)
        {
            foreach (var kid in kids)
            {
                if (kid is NetEntity ne && ne.IsValid())
                    _childToContainer[ne] = ent;
            }
        }

        return true;
    }

    public bool IsOccluded(NetEntity ent)
    {
        if (!_childToContainer.TryGetValue(ent, out var container))
            return false;
        if (_containerShowContents.TryGetValue(container, out var show) && show)
            return false;
        return true;
    }
}
