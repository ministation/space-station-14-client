namespace Robust.Client.ResourceManagement;

/// <summary>Resource cache shell for Content.Client UI textures.</summary>
public interface IResourceCache
{
    bool ContentFileExists(string path);
    object? GetResource(string path);

    event Action<TextureLoadedEventArgs>? OnRawTextureLoaded;
    event Action<RsiLoadedEventArgs>? OnRsiLoaded;
}

public sealed class NullResourceCache : IResourceCache
{
    public bool ContentFileExists(string path) => false;
    public object? GetResource(string path) => null;

    public event Action<TextureLoadedEventArgs>? OnRawTextureLoaded;
    public event Action<RsiLoadedEventArgs>? OnRsiLoaded;
}
