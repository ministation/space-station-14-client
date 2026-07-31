namespace Robust.Client.UserInterface;

/// <summary>IUserInterfaceManager shell matching Content.Client EntryPoint usage.</summary>
public interface IUserInterfaceManager
{
    Control? MainViewport { get; }
    void SetDefaultTheme(string theme);
    void SetActiveTheme(string theme);
    T CreateWindow<T>() where T : Control, new();
}

public partial class UIManager : IUserInterfaceManager
{
    public Control? MainViewport { get; set; } = new Control { Name = "MainViewport" };

    public void SetDefaultTheme(string theme) => _ = theme;

    public void SetActiveTheme(string theme) => _ = theme;

    public T CreateWindow<T>() where T : Control, new() => new();
}
