using Port.Content;
using Robust.Shared.Enums;

namespace Port.Net;

/// <summary>
/// Deploy path: auth → content sync (progress) → lobby join (verbose debug).
/// </summary>
public sealed class ConnectSession
{
    public GameEndpoint Endpoint { get; set; } = GameEndpoint.MiniStation;
    public string? AuthConfigPath { get; set; }
    public string? ContentRoot { get; set; }
    public bool Busy { get; private set; }
    public string Summary { get; private set; } = "Готово к подключению";
    public GameSessionClient Session { get; private set; } = new();
    public ContentProbeSession Content { get; } = new();
    public ServerBuildInfo? LastInfo { get; private set; }
    public GameStatusInfo? LastStatus { get; private set; }
    public ContentDownloadProgress? LastProgress { get; private set; }
    public string DebugLog { get; private set; } = "";

    readonly List<string> _log = new();

    public bool InLobby => Session.IsConnected && !Session.IsObserving;
    public bool Observing => Session.IsConnected && Session.IsObserving;

    public event Action<ContentDownloadProgress>? ProgressChanged;
    public event Action? DebugChanged;

    public bool SetReady(bool ready) => Session.SetReady(ready);
    public bool Observe() => Session.Observe();
    public void PanCamera(float dx, float dy) => Session.PanCamera(dx, dy);

    public string Format()
    {
        var lines = new List<string> { Summary };
        if (LastProgress is { } p)
            lines.Add($"dl: {p.Line}");
        var auth = AuthSessionConfig.TryLoad(AuthConfigPath);
        lines.Add(auth?.StatusLine() ?? "Not logged in");
        if (LastStatus is { } st)
            lines.Add($"{st.Name}: {st.Players}/{st.MaxPlayers} · {st.Map} · {st.Preset}");
        if (LastInfo is { } info)
            lines.Add($"engine {info.EngineVersion} · auth {info.AuthMode} · acz={info.Acz}");
        if (Session.IsConnected)
            lines.Add(Session.Format());
        foreach (var l in _log.TakeLast(20))
            lines.Add(l);
        return string.Join('\n', lines);
    }

    public async Task RefreshStatusAsync(CancellationToken ct = default)
    {
        try
        {
            LastStatus = await new GameStatusClient().FetchAsync(Endpoint, ct);
            NotifyDebug();
        }
        catch
        {
            /* ignore — home widget stays on last known */
        }
    }

    public async Task RunAsync(CancellationToken ct = default)
    {
        if (Busy) return;
        Busy = true;
        _log.Clear();
        try
        {
            Session.Disconnect("reconnect");
            Session = new GameSessionClient();

            Note("=== connect start ===");
            Summary = "Checking SS14 login…";
            NotifyDebug();
            var auth = await EnsureAuthAsync(ct);
            if (auth is null)
            {
                Summary = "Log in with your SS14 account first";
                return;
            }

            Note(auth.StatusLine());
            Summary = "Fetching server status…";
            NotifyDebug();
            try
            {
                LastStatus = await new GameStatusClient().FetchAsync(Endpoint, ct);
                Note($"status: {LastStatus.Name} {LastStatus.Players}/{LastStatus.MaxPlayers} map={LastStatus.Map}");
            }
            catch (Exception ex)
            {
                Note($"status warn: {ex.Message}");
            }

            Summary = "Fetching /info…";
            NotifyDebug();
            LastInfo = await new ServerInfoClient().FetchAsync(Endpoint.HttpBaseUrl, ct);
            Note($"info: engine={LastInfo.EngineVersion} auth={LastInfo.AuthMode} acz={LastInfo.Acz}");

            if (!string.IsNullOrWhiteSpace(LastInfo.PublicKey))
            {
                auth.PublicKey = LastInfo.PublicKey;
                if (!string.IsNullOrWhiteSpace(AuthConfigPath))
                    auth.Save(AuthConfigPath);
                Note("pubkey saved to auth-session");
            }

            // UDP join is independent of ACZ. Run assemblies download in parallel so a
            // Java/HTTP blip no longer blocks the lobby handshake (~15–25s).
            Task? contentTask = null;
            if (!string.IsNullOrWhiteSpace(ContentRoot))
            {
                Content.StatusBaseUrl = Endpoint.HttpBaseUrl;
                Content.ContentRoot = ContentRoot;
                Content.DownloadFullPack = false;
                Content.ProgressChanged -= OnContentProgress;
                Content.ProgressChanged += OnContentProgress;

                Summary = "UDP join + content…";
                Note("content sync: assemblies in parallel with UDP join");
                NotifyDebug();
                contentTask = Task.Run(async () =>
                {
                    try
                    {
                        await Content.RunAsync(ct);
                        Note(Content.Summary);
                        if (!string.IsNullOrWhiteSpace(Content.FilesRoot))
                        {
                            Session.AssembliesDirectory = Path.Combine(Content.FilesRoot, "Assemblies");
                            Note($"assemblies dir → {Session.AssembliesDirectory}");
                        }

                        if (Content.Summary.StartsWith("content: OK", StringComparison.Ordinal))
                        {
                            Content.DownloadFullPack = true;
                            Note("background pack download start");
                            await Content.RunAsync(CancellationToken.None);
                            Note(Content.Summary);
                        }
                    }
                    catch (Exception ex)
                    {
                        Note($"content parallel: {Port.Content.PortHttp.FormatException(ex)}");
                    }
                }, CancellationToken.None);
            }
            else
            {
                Note("ContentRoot unset — skip content sync");
            }

            Summary = "Connecting UDP / handshake…";
            Note($"join lobby → {Endpoint.Host}:{Endpoint.Port}");
            NotifyDebug();

            var result = await Session.JoinLobbyAsync(
                Endpoint,
                LastInfo.AuthMode,
                LastInfo.PublicKey,
                auth,
                TimeSpan.FromSeconds(75),
                ct);

            if (contentTask is not null)
            {
                // Don't fail the join if content is still finishing; wait briefly for assemblies path.
                var finished = await Task.WhenAny(contentTask, Task.Delay(TimeSpan.FromSeconds(2), ct));
                if (finished == contentTask)
                {
                    try { await contentTask; }
                    catch (Exception ex) { Note($"content await: {ex.Message}"); }
                }
                else
                {
                    Note("content still running in background");
                }

                if (string.IsNullOrWhiteSpace(Session.AssembliesDirectory)
                    && !string.IsNullOrWhiteSpace(Content.FilesRoot))
                {
                    Session.AssembliesDirectory = Path.Combine(Content.FilesRoot, "Assemblies");
                }
            }

            Note($"session phase={result.Phase} detail={result.Detail}");
            foreach (var line in Session.SnapshotLogPublic(24))
                Note(line);

            if (result.Phase is GameSessionPhase.InLobby or GameSessionPhase.Observing)
            {
                var local = result.Players?.FirstOrDefault(p => p.UserId == result.UserId);
                Summary =
                    $"In lobby as {result.UserName}\n" +
                    $"Status: {local?.Status ?? SessionStatus.Connected} · {result.Players?.Count ?? 0} players";
                Note(Summary);
            }
            else
            {
                Summary = $"Connect failed: {result.Detail}";
                if (result.Detail.Contains("no response", StringComparison.OrdinalIgnoreCase))
                    Summary += "\nUDP до сервера не доходит — выключи VPN / Private DNS и попробуй на мобильном интернете.";
                Note(Summary);
            }
        }
        catch (Exception ex)
        {
            Summary = $"Error: {ex.Message}";
            Note($"{ex.GetType().Name}: {ex.Message}");
        }
        finally
        {
            Content.ProgressChanged -= OnContentProgress;
            Busy = false;
            NotifyDebug();
        }
    }

    public void Disconnect()
    {
        Session.Disconnect("user disconnect");
        Summary = "Disconnected";
        Note("disconnected");
        NotifyDebug();
    }

    void OnContentProgress(ContentDownloadProgress p)
    {
        LastProgress = p;
        Summary = $"Downloading: {p.Percent}% — {p.Stage} {p.Done}/{p.Total}";
        if (!string.IsNullOrWhiteSpace(p.CurrentPath))
            Summary += $"\n{p.CurrentPath}";
        ProgressChanged?.Invoke(p);
        NotifyDebug();
    }

    async Task<AuthSessionConfig?> EnsureAuthAsync(CancellationToken ct)
    {
        var auth = AuthSessionConfig.TryLoad(AuthConfigPath);
        if (auth?.HasRequiredFields != true)
            return null;

        var client = new Ss14AuthClient { AuthServer = auth.AuthServer };
        if (await client.PingAsync(auth.Token, ct))
        {
            Note("token ping OK");
            return auth;
        }

        Note("token ping failed — refresh");
        var refreshed = await client.RefreshAsync(auth.Token, ct);
        if (!refreshed.Ok || refreshed.Token is null)
        {
            Note($"refresh failed: {refreshed.Error}");
            return null;
        }

        auth.Token = refreshed.Token;
        if (refreshed.ExpireTime is { } exp)
            auth.ExpireTime = exp.ToString("O");
        if (!string.IsNullOrWhiteSpace(AuthConfigPath))
            auth.Save(AuthConfigPath);
        Note("token refreshed OK");
        return auth;
    }

    void Note(string msg)
    {
        var line = $"{DateTime.Now:HH:mm:ss.fff} {msg}";
        _log.Add(line);
        if (_log.Count > 200)
            _log.RemoveRange(0, _log.Count - 150);
        DebugLog = string.Join('\n', _log.TakeLast(40));
    }

    void NotifyDebug() => DebugChanged?.Invoke();
}
