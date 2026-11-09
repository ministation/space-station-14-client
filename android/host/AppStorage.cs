using System.IO;
using Android.App;

namespace Content.Android;

/// <summary>App-private storage paths. Context.FilesDir is /data/user/0/&lt;pkg&gt;/files.</summary>
public static class AppStorage
{
    public static string FilesDir => Application.Context.FilesDir!.AbsolutePath;

    /// <summary>
    /// Контент держим вне FilesDir: движок монтирует FilesDir как /Assemblies/ и видел бы
    /// копию Content.*.dll из кеша вторым модулем.
    /// </summary>
    public static string ContentCacheDir
    {
        get
        {
            var dir = Application.Context.NoBackupFilesDir?.AbsolutePath ?? Application.Context.CacheDir?.AbsolutePath ?? FilesDir;
            return Path.Combine(dir, "content_cache");
        }
    }

    public static string LegacyContentCacheDir => Path.Combine(FilesDir, "content_cache");
}
