namespace Port.Platform.Android;

/// <summary>
/// Android storage layout for future engine/content packs.
/// Mirrors the idea of Robust user data / content dirs without Client deps.
/// </summary>
public sealed class AndroidContentPaths
{
    public required string FilesDir { get; init; }
    public required string CacheDir { get; init; }
    public string ContentDir => Path.Combine(FilesDir, "content");
    public string UserDataDir => Path.Combine(FilesDir, "userdata");
    public string LogsDir => Path.Combine(FilesDir, "logs");

    public static AndroidContentPaths FromContext(global::Android.Content.Context context)
    {
        var files = context.FilesDir?.AbsolutePath
            ?? throw new InvalidOperationException("Context.FilesDir is null");
        var cache = context.CacheDir?.AbsolutePath
            ?? Path.Combine(files, "cache");
        return new AndroidContentPaths
        {
            FilesDir = files,
            CacheDir = cache,
        };
    }
}
