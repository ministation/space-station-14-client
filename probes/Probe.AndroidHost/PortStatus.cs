namespace Probe.AndroidHost;

internal static class PortStatus
{
    public sealed record Phase(string Id, string Title, string State);

    public static IReadOnlyList<Phase> CurrentPhases { get; } =
    [
        new("0", "Android host shell", "DONE"),
        new("1", "Robust inventory / deps map", "DONE"),
        new("2", "Shared compile spike", "DONE"),
        new("3", "Android platform stubs", "DONE"),
        new("4", "Graphics (GLES/Vulkan)", "DONE"),
        new("5", "Networking", "DONE"),
        new("6", "Content packs", "DONE"),
        new("7", "Observe / ghost (connect)", "NEXT"),
    ];

    public static string Format()
    {
        string sharedSmoke;
        try
        {
            sharedSmoke = Probe.SharedOnAndroid.SharedAndroidSmoke.Ping();
        }
        catch (Exception ex)
        {
            sharedSmoke = $"Shared runtime FAIL: {ex.GetType().Name}: {ex.Message}";
        }

        var lines = new List<string>
        {
            $".NET: {Environment.Version}",
            $"OS: {Environment.OSVersion}",
            sharedSmoke,
            "",
        };

        foreach (var phase in CurrentPhases)
        {
            var mark = phase.State switch
            {
                "DONE" => "[x]",
                "NEXT" => "[>]",
                _ => "[ ]",
            };
            lines.Add($"{mark} {phase.Id}. {phase.Title}  ({phase.State})");
        }

        lines.Add("");
        lines.Add("Repo: robust-android-port");
        return string.Join('\n', lines);
    }
}
