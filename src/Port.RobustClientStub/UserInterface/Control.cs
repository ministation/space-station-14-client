namespace Robust.Client.UserInterface;

/// <summary>
/// Control stub for Content.Client / UIKit type resolution on Android.
/// Not a full Clyde UI implementation — layout/render arrive in later PRs.
/// </summary>
public class Control : IDisposable
{
    readonly List<Control> _children = new();

    public Control? Parent { get; private set; }
    public IReadOnlyList<Control> Children => _children;
    public string? Name { get; set; }
    public bool Visible { get; set; } = true;
    public bool Disabled { get; set; }
    public float MinWidth { get; set; }
    public float MinHeight { get; set; }
    public float SetWidth { get; set; } = float.NaN;
    public float SetHeight { get; set; } = float.NaN;

    public void AddChild(Control child)
    {
        ArgumentNullException.ThrowIfNull(child);
        child.Parent?.RemoveChild(child);
        child.Parent = this;
        _children.Add(child);
    }

    public void RemoveChild(Control child)
    {
        if (!_children.Remove(child))
            return;
        child.Parent = null;
    }

    public virtual void Dispose()
    {
        foreach (var c in _children.ToArray())
            c.Dispose();
        _children.Clear();
        Parent?.RemoveChild(this);
    }
}
