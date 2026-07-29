using Port.Content;
using Port.Net;

var authPath = Path.Combine(AppContext.BaseDirectory, "auth-session.json");

// Optional: dotnet run -- <username> <password> [tfa]
if (args.Length >= 2)
{
    Console.WriteLine($"Logging in as {args[0]} …");
    var authClient = new Ss14AuthClient();
    var result = await authClient.AuthenticateAsync(
        args[0],
        args[1],
        args.Length >= 3 ? args[2] : null);
    if (!result.Ok)
    {
        Console.WriteLine($"AUTH FAIL [{result.ErrorCode}]: {result.Error}");
        return;
    }

    var cfg = authClient.ToSession(result);
    cfg.Save(authPath);
    Console.WriteLine(cfg.StatusLine());
}
else if (!File.Exists(authPath))
{
    await File.WriteAllTextAsync(authPath, AuthSessionConfig.ExampleJson());
    Console.WriteLine("Wrote example auth-session.json — fill token or pass username/password.");
    return;
}

var auth = AuthSessionConfig.TryLoad(authPath);
if (auth?.HasRequiredFields != true)
{
    Console.WriteLine("No valid auth session. Run with: dotnet run -- user pass [tfa]");
    return;
}

var endpoint = GameEndpoint.MiniStation;
Console.WriteLine($"Joining lobby on {endpoint.ConnectUri} …");
Console.WriteLine($"Auth: {auth.StatusLine()}");

var info = await new ServerInfoClient().FetchAsync(endpoint.HttpBaseUrl);
Console.WriteLine($"info: engine={info.EngineVersion} auth={info.AuthMode}");
if (!string.IsNullOrWhiteSpace(info.PublicKey))
{
    auth.PublicKey = info.PublicKey;
    auth.Save(authPath);
}

using var session = new GameSessionClient();
var lobby = await session.JoinLobbyAsync(
    endpoint,
    info.AuthMode,
    info.PublicKey,
    auth,
    TimeSpan.FromSeconds(45));

Console.WriteLine(session.Format());
Console.WriteLine(lobby.Phase == GameSessionPhase.InLobby
    ? $"RESULT: LOBBY OK — {lobby.UserName}, players={lobby.Players?.Count}"
    : $"RESULT: {lobby.Phase} — {lobby.Detail}");
