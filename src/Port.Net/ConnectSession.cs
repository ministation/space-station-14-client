using Port.Content;
using Robust.Shared.Enums;

namespace Port.Net;

/// <summary>
/// Auth → full content preload (assemblies/prototypes/textures) → lobby join.
/// Lobby is never entered until textures finish downloading.
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
    bool _joinSettled;

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
        _joinSettled = false;
        _log.Clear();
        try
        {
            Session.Disconnect("reconnect");
            Session = new GameSessionClient();

            Note("=== connect start ===");
            Summary = "Проверка аккаунта SS14…";
            NotifyDebug();
            var auth = await EnsureAuthAsync(ct);
            if (auth is null)
            {
                Summary = "Сначала войдите в аккаунт SS14";
                return;
            }

            if (!auth.AllowHwid)
            {
                auth.AllowHwid = true;
                if (!string.IsNullOrWhiteSpace(AuthConfigPath))
                    auth.Save(AuthConfigPath);
                Note("AllowHwid enabled (required by server)");
            }

            if (string.IsNullOrWhiteSpace(ClientHwid.StorageDirectory)
                && !string.IsNullOrWhiteSpace(AuthConfigPath))
            {
                ClientHwid.StorageDirectory = Path.Combine(
                    Path.GetDirectoryName(AuthConfigPath) ?? ".",
                    "userdata");
            }

            Note(auth.StatusLine());
            Summary = "Статус сервера…";
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

            Summary = "Информация о билде…";
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

            // Content MUST finish (including all .rsic) before UDP lobby join.
            if (!string.IsNullOrWhiteSpace(ContentRoot))
            {
                Content.StatusBaseUrl = Endpoint.HttpBaseUrl;
                Content.ContentRoot = ContentRoot;
                Content.DownloadFullPack = false;
                Content.DownloadGhostAssets = true;
                Content.DownloadAllTextures = true;
                Content.ProgressChanged -= OnContentProgress;
                Content.ProgressChanged += OnContentProgress;

                Session.ContentSearchRoot = ContentRoot;
                Session.AssembliesDirectory = Path.Combine(ContentRoot, "Assemblies");
                Session.StringsCacheDirectory = Path.Combine(ContentRoot, "string-cache");
                Note($"content search root → {Session.ContentSearchRoot}");

                Summary = "Загрузка контента…";
                Note("content sync BEFORE lobby: assemblies + prototypes + all .rsic");
                NotifyDebug();

                await Content.RunAsync(ct);
                Note(Content.Summary);

                if (!Content.LastSucceeded)
                {
                    Summary = string.IsNullOrWhiteSpace(Content.Summary)
                        ? "Не удалось загрузить контент"
                        : Content.Summary;
                    Note("content failed — abort join");
                    return;
                }

                if (!string.IsNullOrWhiteSpace(Content.FilesRoot))
                {
                    Session.AssembliesDirectory = Path.Combine(Content.FilesRoot, "Assemblies");
                    Session.ContentFilesRoot = Content.FilesRoot;
                    if (Content.TextureIndex is { Count: > 0 } idx)
                        Session.ConfigureTextureFetcher(Endpoint.HttpBaseUrl, idx, Content.FilesRoot);
                    Session.NotifyContentReady(Content.FilesRoot);
                    Note($"assemblies dir → {Session.AssembliesDirectory}");
                    Note($"rsic index={Session.TextureFetcher.IndexedRsicCount}");
                }
            }
            else
            {
                Note("ContentRoot unset — skip content sync");
            }

            Summary = "Подключение к серверу…";
            Note($"join lobby → {Endpoint.Host}:{Endpoint.Port}");
            NotifyDebug();

            var result = await Session.JoinLobbyAsync(
                Endpoint,
                LastInfo.AuthMode,
                LastInfo.PublicKey,
                auth,
                TimeSpan.FromSeconds(90),
                ct);

            Note($"session phase={result.Phase} detail={result.Detail}");
            foreach (var line in Session.SnapshotLogPublic(24))
                Note(line);

            _joinSettled = true;
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
                var readable = ConnectFailureFormatter.ExtractReason(result.Detail);
                Summary = ConnectFailureFormatter.FormatUserSummary(result.Detail);
                Note($"connect denied (raw): {result.Detail}");
                if (!string.IsNullOrWhiteSpace(readable) && readable != result.Detail)
                    Note($"connect denied: {readable}");
                Note(Summary);
            }
        }
        catch (Exception ex)
        {
            _joinSettled = true;
            Summary = ConnectFailureFormatter.FormatUserSummary(ex.Message);
            Note($"{ex.GetType().Name}: {ex.Message}");
            Note(Summary);
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
        if (!_joinSettled)
            Summary = Content.Summary;
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
