using System.Diagnostics.CodeAnalysis;
using Robust.Shared.Utility;

namespace Robust.Client.UserInterface.RichText;

/// <summary>
/// Stub of upstream IMarkupTagHandler so Content.*.Shared can load against our Robust.Client shim.
/// </summary>
public interface IMarkupTagHandler
{
    string Name { get; }

    void PushDrawContext(MarkupNode node, MarkupDrawingContext context)
    {
    }

    string TextBefore(MarkupNode node) => "";

    string TextAfter(MarkupNode node) => "";

    void PopDrawContext(MarkupNode node, MarkupDrawingContext context)
    {
    }

    bool TryCreateControl(MarkupNode node, [NotNullWhen(true)] out Control? control)
    {
        control = null;
        return false;
    }
}

[Obsolete("Use IMarkupTagHandler")]
public interface IMarkupTag : IMarkupTagHandler
{
    bool IMarkupTagHandler.TryCreateControl(MarkupNode node, [NotNullWhen(true)] out Control? control)
        => TryGetControl(node, out control);

    bool TryGetControl(MarkupNode node, [NotNullWhen(true)] out Control? control)
    {
        control = null;
        return false;
    }
}
