using Port.Content;

var root = Path.Combine(Path.GetTempPath(), "robust-android-port-content");
Directory.CreateDirectory(root);

var session = new ContentProbeSession
{
    StatusBaseUrl = "http://ss14.ministation.ru:1214",
    ContentRoot = root,
};

Console.WriteLine($"Content root: {root}");
await session.RunAsync();
Console.WriteLine(session.Format());

if (session.LastManifest is { } m && session.SampleFilesDownloaded > 0)
{
    Console.WriteLine("RESULT: Phase 6 partial success (ACZ manifest + sample blobs)");
    Console.WriteLine($"Saved under: {session.ContentRoot}");
}
else
{
    Console.WriteLine($"RESULT: {session.Summary}");
    Environment.ExitCode = 1;
}
