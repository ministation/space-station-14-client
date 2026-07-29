using Android.Content.PM;
using Android.Views;
using Android.Widget;
using Port.Content;
using Port.Net;
using Port.Platform.Android;
using Port.Platform.Android.Graphics;
using System.Timers;
using Timer = System.Timers.Timer;
using View = Android.Views.View;
using RobustSessionStatus = Robust.Shared.Enums.SessionStatus;

namespace Probe.AndroidHost;

[Activity(
    Label = "@string/app_name",
    MainLauncher = true,
    Theme = "@android:style/Theme.DeviceDefault.NoActionBar",
    ConfigurationChanges = ConfigChanges.Orientation | ConfigChanges.ScreenSize | ConfigChanges.KeyboardHidden)]
public class MainActivity : Activity
{
    View? _screenHome;
    View? _screenLobby;
    View? _screenObserve;
    TextView? _authStatus;
    TextView? _connectStatus;
    TextView? _serverChip;
    TextView? _lobbyStation;
    TextView? _lobbyRound;
    TextView? _lobbyAccount;
    TextView? _lobbyPlayers;
    TextView? _lobbyDetail;
    TextView? _observeHud;
    TextView? _joinDebug;
    TextView? _debugToggle;
    TextView? _downloadLabel;
    TextView? _downloadPct;
    TextView? _statusBadge;
    TextView? _serverPlayersBig;
    ProgressBar? _downloadProgress;
    bool _debugOpen;
    EditText? _authUsername;
    EditText? _authPassword;
    EditText? _authTfa;
    EditText? _charName;
    Button? _connectBtn;
    Button? _loginBtn;
    Button? _logoutBtn;
    Button? _disconnectBtn;
    Button? _observeLeaveBtn;
    Button? _readyBtn;
    Button? _observeBtn;
    FrameLayout? _observeGl;
    GlesClearSurfaceView? _glView;

    AndroidPlatformHost? _host;
    readonly ConnectSession _connect = new();
    readonly Ss14AuthClient _authClient = new();
    CancellationTokenSource? _connectCts;
    CancellationTokenSource? _loginCts;
    Timer? _uiTimer;
        string _authUiStatus = "Войдите через аккаунт Space Station 14";
    bool _loginBusy;
    float _lastTouchX, _lastTouchY;
    bool _dragging;

    protected override void OnCreate(Bundle? savedInstanceState)
    {
        SodiumAndroidBootstrap.EnsureLoaded();
        base.OnCreate(savedInstanceState);
        SetContentView(Resource.Layout.activity_main);

        var paths = AndroidContentPaths.FromContext(this);
        _host = new AndroidPlatformHost(paths);
        _host.EnsureDirectories();
        _host.OnLifecycle(PlatformLifecycle.Created);
        _connect.AuthConfigPath = Path.Combine(paths.FilesDir, "auth-session.json");
        _connect.ContentRoot = paths.ContentDir;
        _connect.ProgressChanged += p => RunOnUiThread(() => ApplyProgress(p));
        _connect.DebugChanged += () => RunOnUiThread(RenderStatus);

        _screenHome = FindViewById(Resource.Id.screen_home);
        _screenLobby = FindViewById(Resource.Id.screen_lobby);
        _screenObserve = FindViewById(Resource.Id.screen_observe);
        _authStatus = FindViewById<TextView>(Resource.Id.auth_status);
        _connectStatus = FindViewById<TextView>(Resource.Id.connect_status);
        _serverChip = FindViewById<TextView>(Resource.Id.server_chip);
        _lobbyStation = FindViewById<TextView>(Resource.Id.lobby_station);
        _lobbyRound = FindViewById<TextView>(Resource.Id.lobby_round);
        _lobbyAccount = FindViewById<TextView>(Resource.Id.lobby_account);
        _lobbyPlayers = FindViewById<TextView>(Resource.Id.lobby_players);
        _lobbyDetail = FindViewById<TextView>(Resource.Id.lobby_detail);
        _observeHud = FindViewById<TextView>(Resource.Id.observe_hud);
        _joinDebug = FindViewById<TextView>(Resource.Id.join_debug);
        _debugToggle = FindViewById<TextView>(Resource.Id.debug_toggle);
        _downloadLabel = FindViewById<TextView>(Resource.Id.download_label);
        _downloadPct = FindViewById<TextView>(Resource.Id.download_pct);
        _downloadProgress = FindViewById<ProgressBar>(Resource.Id.download_progress);
        _statusBadge = FindViewById<TextView>(Resource.Id.status_badge);
        _serverPlayersBig = FindViewById<TextView>(Resource.Id.server_players_big);
        _authUsername = FindViewById<EditText>(Resource.Id.auth_username);
        _authPassword = FindViewById<EditText>(Resource.Id.auth_password);
        _authTfa = FindViewById<EditText>(Resource.Id.auth_tfa);
        _charName = FindViewById<EditText>(Resource.Id.char_name);
        _connectBtn = FindViewById<Button>(Resource.Id.btn_connect);
        _loginBtn = FindViewById<Button>(Resource.Id.btn_login_ss14);
        _logoutBtn = FindViewById<Button>(Resource.Id.btn_logout_ss14);
        _disconnectBtn = FindViewById<Button>(Resource.Id.btn_disconnect);
        _observeLeaveBtn = FindViewById<Button>(Resource.Id.btn_observe_leave);
        _readyBtn = FindViewById<Button>(Resource.Id.btn_ready);
        _observeBtn = FindViewById<Button>(Resource.Id.btn_observe);
        _observeGl = FindViewById<FrameLayout>(Resource.Id.observe_gl);

        if (_serverChip != null)
            _serverChip.Text = $"ss14://{_connect.Endpoint.Host}:{_connect.Endpoint.Port}";

        ClearMaterialTint(_loginBtn);
        ClearMaterialTint(_logoutBtn);
        ClearMaterialTint(_connectBtn);
        ClearMaterialTint(_disconnectBtn);
        ClearMaterialTint(_observeLeaveBtn);
        ClearMaterialTint(_readyBtn);
        ClearMaterialTint(_observeBtn);

        var existing = AuthSessionConfig.TryLoad(_connect.AuthConfigPath);
        if (existing?.HasRequiredFields == true)
        {
            _authUiStatus = existing.StatusLine();
            if (_authUsername != null && !string.IsNullOrWhiteSpace(existing.UserName))
                _authUsername.Text = existing.UserName;
            if (_charName != null && string.IsNullOrWhiteSpace(_charName.Text))
                _charName.Text = existing.UserName;
        }

        if (_debugToggle != null)
        {
            _debugToggle.Click += (_, _) =>
            {
                _debugOpen = !_debugOpen;
                if (_joinDebug != null)
                    _joinDebug.Visibility = _debugOpen ? ViewStates.Visible : ViewStates.Gone;
                _debugToggle.Text = _debugOpen ? "Скрыть журнал подключения" : GetString(Resource.String.debug_toggle);
            };
        }

        if (_loginBtn != null)
            _loginBtn.Click += async (_, _) => await RunLoginAsync();
        if (_logoutBtn != null)
            _logoutBtn.Click += (_, _) => Logout();
        if (_connectBtn != null)
            _connectBtn.Click += async (_, _) => await RunConnectAsync();
        if (_disconnectBtn != null)
            _disconnectBtn.Click += (_, _) => LeaveServer();
        if (_observeLeaveBtn != null)
            _observeLeaveBtn.Click += (_, _) => LeaveServer();
        if (_readyBtn != null)
            _readyBtn.Click += (_, _) =>
            {
                var next = !_connect.Session.IsReady;
                if (!_connect.SetReady(next))
                {
                    Toast.MakeText(this, "Not connected", ToastLength.Short)?.Show();
                    return;
                }

                Toast.MakeText(this,
                    next
                        ? "Ready sent (toggleready True) — only works in pre-round lobby"
                        : "Unready sent",
                    ToastLength.Short)?.Show();
                RenderStatus();
            };
        if (_observeBtn != null)
            _observeBtn.Click += (_, _) =>
            {
                if (!_connect.Observe())
                {
                    Toast.MakeText(this, "Not connected", ToastLength.Short)?.Show();
                    return;
                }

                EnsureGl();
                _glView?.Renderer.SetGhostMode(true);
                Toast.MakeText(this,
                    "Observe sent — needs round in progress. Drag to pan.",
                    ToastLength.Short)?.Show();
                RenderStatus();
            };

        _uiTimer = new Timer(250);
        _uiTimer.Elapsed += (_, _) =>
        {
            _host?.Clock.Pulse();
            RunOnUiThread(RenderStatus);
        };
        _uiTimer.AutoReset = true;

        RenderStatus();
        _ = RefreshHomeStatusAsync();
    }

    async Task RefreshHomeStatusAsync()
    {
        try
        {
            await _connect.RefreshStatusAsync();
            RunOnUiThread(RenderStatus);
        }
        catch
        {
            /* ignore */
        }
    }

    void EnsureGl()
    {
        if (_observeGl is null || _glView != null)
            return;
        _glView = new GlesClearSurfaceView(this);
        _glView.LayoutParameters = new FrameLayout.LayoutParams(
            ViewGroup.LayoutParams.MatchParent,
            ViewGroup.LayoutParams.MatchParent);
        _glView.Touch += OnObserveTouch;
        _observeGl.AddView(_glView, 0);
        _glView.Renderer.SetGhostMode(true);
    }

    void LeaveServer()
    {
        _connect.Disconnect();
        _glView?.Renderer.SetGhostMode(false);
        RenderStatus();
    }

    void OnObserveTouch(object? sender, View.TouchEventArgs e)
    {
        if (e.Event is null) return;
        var ev = e.Event;
        switch (ev.ActionMasked)
        {
            case MotionEventActions.Down:
                _dragging = true;
                _lastTouchX = ev.GetX();
                _lastTouchY = ev.GetY();
                break;
            case MotionEventActions.Move when _dragging:
                var x = ev.GetX();
                var y = ev.GetY();
                var dx = x - _lastTouchX;
                var dy = y - _lastTouchY;
                _lastTouchX = x;
                _lastTouchY = y;
                _connect.PanCamera(-dx, dy);
                _glView?.Renderer.SetCamera(_connect.Session.CamX, _connect.Session.CamY);
                break;
            case MotionEventActions.Up:
            case MotionEventActions.Cancel:
                _dragging = false;
                break;
        }

        e.Handled = true;
    }

    async Task RunLoginAsync()
    {
        if (_loginBusy) return;
        _loginBusy = true;
        _loginCts?.Cancel();
        _loginCts = new CancellationTokenSource();
        if (_loginBtn != null) _loginBtn.Enabled = false;

        var user = _authUsername?.Text?.Trim() ?? "";
        var pass = _authPassword?.Text ?? "";
        var tfa = _authTfa?.Text?.Trim();
        _authUiStatus = $"Signing in as {user}…";
        RenderStatus();

        try
        {
            var result = await _authClient.AuthenticateAsync(user, pass, tfa, _loginCts.Token);
            if (!result.Ok)
            {
                _authUiStatus = $"Sign-in failed [{result.ErrorCode}]: {result.Error}";
                return;
            }

            var session = _authClient.ToSession(result);
            if (!string.IsNullOrWhiteSpace(_connect.AuthConfigPath))
                session.Save(_connect.AuthConfigPath);

            _authUiStatus = session.StatusLine();
            if (_authPassword != null) _authPassword.Text = "";
            if (_authTfa != null) _authTfa.Text = "";
            if (_charName != null && string.IsNullOrWhiteSpace(_charName.Text))
                _charName.Text = session.UserName;
        }
        catch (Exception ex)
        {
            _authUiStatus = $"Sign-in error: {ex.Message}";
        }
        finally
        {
            _loginBusy = false;
            if (_loginBtn != null) _loginBtn.Enabled = true;
            RenderStatus();
        }
    }

    void Logout()
    {
        if (!string.IsNullOrWhiteSpace(_connect.AuthConfigPath))
            AuthSessionConfig.Clear(_connect.AuthConfigPath);
        LeaveServer();
        _authUiStatus = "Signed out";
        RenderStatus();
    }

    async Task RunConnectAsync()
    {
        if (_connect.Busy) return;
        _connectCts?.Cancel();
        _connectCts = new CancellationTokenSource();
        if (_connectBtn != null) _connectBtn.Enabled = false;
        RenderStatus();
        try
        {
            await _connect.RunAsync(_connectCts.Token);
            if (_connect.InLobby || _connect.Observing)
            {
                _authUiStatus = $"Connected as {_connect.Session.UserName}";
                if (_charName != null && string.IsNullOrWhiteSpace(_charName.Text))
                    _charName.Text = _connect.Session.UserName;
            }
        }
        finally
        {
            if (_connectBtn != null) _connectBtn.Enabled = true;
            RenderStatus();
        }
    }

    protected override void OnStart()
    {
        base.OnStart();
        _host?.OnLifecycle(PlatformLifecycle.Started);
    }

    protected override void OnResume()
    {
        base.OnResume();
        _host?.OnLifecycle(PlatformLifecycle.Resumed);
        _glView?.OnResume();
        _uiTimer?.Start();
        RenderStatus();
    }

    protected override void OnPause()
    {
        _uiTimer?.Stop();
        _glView?.OnPause();
        _host?.OnLifecycle(PlatformLifecycle.Paused);
        base.OnPause();
    }

    protected override void OnStop()
    {
        _host?.OnLifecycle(PlatformLifecycle.Stopped);
        base.OnStop();
    }

    protected override void OnDestroy()
    {
        _connectCts?.Cancel();
        _loginCts?.Cancel();
        _uiTimer?.Stop();
        _uiTimer?.Dispose();
        _uiTimer = null;
        _connect.Disconnect();
        _host?.OnLifecycle(PlatformLifecycle.Destroyed);
        base.OnDestroy();
    }

    void RenderStatus()
    {
        var observing = _connect.Observing;
        var lobby = _connect.InLobby;

        if (_screenHome != null)
            _screenHome.Visibility = lobby || observing ? ViewStates.Gone : ViewStates.Visible;
        if (_screenLobby != null)
            _screenLobby.Visibility = lobby ? ViewStates.Visible : ViewStates.Gone;
        if (_screenObserve != null)
            _screenObserve.Visibility = observing ? ViewStates.Visible : ViewStates.Gone;

        if (observing)
            EnsureGl();

        if (_authStatus != null)
            _authStatus.Text = _authUiStatus;
        if (_connectStatus != null)
            _connectStatus.Text = _connect.Summary;

        if (_serverPlayersBig != null || _statusBadge != null)
        {
            var st = _connect.LastStatus;
            if (st is { Online: true })
            {
                if (_serverPlayersBig != null)
                    _serverPlayersBig.Text = $"{st.Players}/{st.MaxPlayers}";
                if (_statusBadge != null)
                    _statusBadge.Text = "Онлайн";
            }
            else if (_connect.Busy)
            {
                if (_statusBadge != null)
                    _statusBadge.Text = "Загрузка…";
            }
            else if (!string.IsNullOrWhiteSpace(st?.Error))
            {
                if (_statusBadge != null)
                    _statusBadge.Text = "Оффлайн";
            }
        }

        if (_lobbyStation != null)
        {
            var name = _connect.LastStatus?.Name;
            _lobbyStation.Text = string.IsNullOrWhiteSpace(name) ? "Мини-станция" : name;
        }

        if (_lobbyRound != null)
        {
            if (_connect.Session.IsConnected)
            {
                var st = _connect.Session.LocalStatus;
                var map = _connect.LastStatus?.Map;
                var preset = _connect.LastStatus?.Preset;
                var ready = _connect.Session.IsReady ? " · ГОТОВ" : "";
                _lobbyRound.Text = string.IsNullOrWhiteSpace(map)
                    ? $"{st}{ready}"
                    : $"{st}{ready} · {map} · {preset}";
            }
            else
            {
                _lobbyRound.Text = "Отключено";
            }
        }

        if (_lobbyAccount != null)
            _lobbyAccount.Text = $"Аккаунт: {_connect.Session.UserName ?? "—"}";

        if (_lobbyPlayers != null)
        {
            var players = _connect.Session.Players;
            if (players.Count == 0)
                _lobbyPlayers.Text = lobby ? "Ждём список игроков…" : "—";
            else
            {
                var lines = players
                    .OrderBy(p => p.Status)
                    .ThenBy(p => p.Name, StringComparer.OrdinalIgnoreCase)
                    .Select(p => $"[{StatusLabel(p.Status)}]  {p.Name}");
                _lobbyPlayers.Text = string.Join('\n', lines);
            }
        }

        if (_lobbyDetail != null)
        {
            _lobbyDetail.Text = _connect.Session.Format();
            if (!string.IsNullOrWhiteSpace(_connect.DebugLog))
                _lobbyDetail.Text += "\n---\n" + _connect.DebugLog;
        }

        if (_joinDebug != null)
            _joinDebug.Text = string.IsNullOrWhiteSpace(_connect.DebugLog)
                ? _connect.Summary
                : _connect.DebugLog;

        if (_connect.LastProgress is { } prog && _connect.Busy)
            ApplyProgress(prog);

        if (_observeHud != null && observing)
        {
            var s = _connect.Session;
            _observeHud.Text =
                $"{s.Detail}\n" +
                $"status={s.LocalStatus}  MsgState={s.StatesReceived}  last={s.LastStateBytes}B\n" +
                $"{s.SerializerStatus}\n" +
                $"eye: {s.LastEye?.Detail ?? s.LastEyeHint}\n" +
                $"cam=({s.CamX:0},{s.CamY:0})\n" +
                string.Join('\n', s.SnapshotLogPublic(10));
            _glView?.Renderer.SetCamera(s.CamX, s.CamY);
        }

        if (_readyBtn != null)
            _readyBtn.Text = _connect.Session.IsReady ? "Не готов" : GetString(Resource.String.btn_ready);
    }

    static void ClearMaterialTint(Button? btn)
    {
        if (btn is null) return;
        btn.BackgroundTintList = null;
    }

    void ApplyProgress(ContentDownloadProgress p)
    {
        if (_downloadLabel != null)
            _downloadLabel.Visibility = ViewStates.Visible;
        if (_downloadProgress != null)
        {
            _downloadProgress.Visibility = ViewStates.Visible;
            _downloadProgress.Progress = p.Percent;
        }

        if (_downloadPct != null)
        {
            _downloadPct.Visibility = ViewStates.Visible;
            _downloadPct.Text = $"{p.Percent}%  {p.Stage}  {p.Done}/{p.Total}" +
                                (string.IsNullOrWhiteSpace(p.CurrentPath) ? "" : $"\n{p.CurrentPath}");
        }

        if (_connectStatus != null)
            _connectStatus.Text = _connect.Summary;
        if (_joinDebug != null)
            _joinDebug.Text = _connect.DebugLog;
    }

    static string StatusLabel(RobustSessionStatus s) => s switch
    {
        RobustSessionStatus.Connected => "LOBBY",
        RobustSessionStatus.InGame => "ROUND",
        RobustSessionStatus.Connecting => "JOIN",
        RobustSessionStatus.Zombie => "ZOMBIE",
        RobustSessionStatus.Disconnected => "OFF",
        _ => s.ToString().ToUpperInvariant(),
    };
}
