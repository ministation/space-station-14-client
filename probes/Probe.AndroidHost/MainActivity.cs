using Android.Content.PM;
using Android.Graphics;
using Android.Util;
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
    ProgressBar? _downloadProgress;
    bool _debugOpen;
    EditText? _authUsername;
    EditText? _authPassword;
    EditText? _authTfa;
    EditText? _charName;
    EditText? _customServer;
    LinearLayout? _serverList;
    Button? _connectBtn;
    Button? _loginBtn;
    Button? _logoutBtn;
    Button? _disconnectBtn;
    Button? _observeLeaveBtn;
    Button? _readyBtn;
    Button? _observeBtn;
    Button? _addServerBtn;
    FrameLayout? _observeGl;
    GlesClearSurfaceView? _glView;

    AndroidPlatformHost? _host;
    readonly ConnectSession _connect = new();
    readonly Ss14AuthClient _authClient = new();
    HubServerCatalog? _hub;
    HubServerEntry? _selected;
    CancellationTokenSource? _connectCts;
    CancellationTokenSource? _loginCts;
    Timer? _uiTimer;
    string _authUiStatus = "Войдите через аккаунт SS14";
    bool _loginBusy;
    float _lastTouchX, _lastTouchY;
    bool _dragging;
    bool _landscapeLocked;

    protected override void OnCreate(Bundle? savedInstanceState)
    {
        SodiumAndroidBootstrap.EnsureLoaded();
        base.OnCreate(savedInstanceState);
        RequestedOrientation = ScreenOrientation.Portrait;
        SetContentView(Resource.Layout.activity_main);

        var paths = AndroidContentPaths.FromContext(this);
        _host = new AndroidPlatformHost(paths);
        _host.EnsureDirectories();
        _host.OnLifecycle(PlatformLifecycle.Created);
        _connect.AuthConfigPath = System.IO.Path.Combine(paths.FilesDir, "auth-session.json");
        _connect.ContentRoot = paths.ContentDir;
        _hub = new HubServerCatalog(System.IO.Path.Combine(paths.FilesDir, "hub-favorites.json"));
        _connect.ProgressChanged += p => RunOnUiThread(() => ApplyProgress(p));
        _connect.DebugChanged += () => RunOnUiThread(RenderStatus);

        BindViews();
        WireButtons();
        SelectServer(_hub.All.FirstOrDefault());
        RebuildServerList();

        var existing = AuthSessionConfig.TryLoad(_connect.AuthConfigPath);
        if (existing?.HasRequiredFields == true)
        {
            _authUiStatus = existing.StatusLine();
            if (_authUsername != null && !string.IsNullOrWhiteSpace(existing.UserName))
                _authUsername.Text = existing.UserName;
            if (_charName != null && string.IsNullOrWhiteSpace(_charName.Text))
                _charName.Text = existing.UserName;
        }

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

    void BindViews()
    {
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
        _authUsername = FindViewById<EditText>(Resource.Id.auth_username);
        _authPassword = FindViewById<EditText>(Resource.Id.auth_password);
        _authTfa = FindViewById<EditText>(Resource.Id.auth_tfa);
        _charName = FindViewById<EditText>(Resource.Id.char_name);
        _customServer = FindViewById<EditText>(Resource.Id.custom_server);
        _serverList = FindViewById<LinearLayout>(Resource.Id.server_list);
        _connectBtn = FindViewById<Button>(Resource.Id.btn_connect);
        _loginBtn = FindViewById<Button>(Resource.Id.btn_login_ss14);
        _logoutBtn = FindViewById<Button>(Resource.Id.btn_logout_ss14);
        _disconnectBtn = FindViewById<Button>(Resource.Id.btn_disconnect);
        _observeLeaveBtn = FindViewById<Button>(Resource.Id.btn_observe_leave);
        _readyBtn = FindViewById<Button>(Resource.Id.btn_ready);
        _observeBtn = FindViewById<Button>(Resource.Id.btn_observe);
        _addServerBtn = FindViewById<Button>(Resource.Id.btn_add_server);
        _observeGl = FindViewById<FrameLayout>(Resource.Id.observe_gl);

        foreach (var b in new[] { _loginBtn, _logoutBtn, _connectBtn, _disconnectBtn, _observeLeaveBtn, _readyBtn, _observeBtn, _addServerBtn })
            ClearMaterialTint(b);
        ClearMaterialTint(FindViewById<Button>(Resource.Id.btn_touch_up));
        ClearMaterialTint(FindViewById<Button>(Resource.Id.btn_touch_down));
        ClearMaterialTint(FindViewById<Button>(Resource.Id.btn_touch_left));
        ClearMaterialTint(FindViewById<Button>(Resource.Id.btn_touch_right));
    }

    void WireButtons()
    {
        if (_debugToggle != null)
        {
            _debugToggle.Click += (_, _) =>
            {
                _debugOpen = !_debugOpen;
                if (_joinDebug != null)
                    _joinDebug.Visibility = _debugOpen ? ViewStates.Visible : ViewStates.Gone;
                _debugToggle.Text = _debugOpen ? "Скрыть журнал" : GetString(Resource.String.debug_toggle);
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
        if (_addServerBtn != null)
            _addServerBtn.Click += (_, _) => AddCustomServer();

        if (_readyBtn != null)
            _readyBtn.Click += (_, _) =>
            {
                var next = !_connect.Session.IsReady;
                if (!_connect.SetReady(next))
                {
                    Toast.MakeText(this, "Нет соединения", ToastLength.Short)?.Show();
                    return;
                }

                Toast.MakeText(this, next ? "Ready" : "Unready", ToastLength.Short)?.Show();
                RenderStatus();
            };

        if (_observeBtn != null)
            _observeBtn.Click += (_, _) =>
            {
                if (!_connect.Observe())
                {
                    Toast.MakeText(this, "Нет соединения", ToastLength.Short)?.Show();
                    return;
                }

                EnsureGl();
                _glView?.Renderer.SetGhostMode(true);
                ApplyOrientation(forceLandscape: true);
                Toast.MakeText(this, "Observe — тач для камеры", ToastLength.Short)?.Show();
                RenderStatus();
            };

        WireNudge(Resource.Id.btn_touch_up, 0, -48);
        WireNudge(Resource.Id.btn_touch_down, 0, 48);
        WireNudge(Resource.Id.btn_touch_left, -48, 0);
        WireNudge(Resource.Id.btn_touch_right, 48, 0);
    }

    void WireNudge(int id, float dx, float dy)
    {
        var btn = FindViewById<Button>(id);
        if (btn is null) return;
        btn.Touch += (_, e) =>
        {
            if (e.Event is null) return;
            if (e.Event.ActionMasked is MotionEventActions.Down or MotionEventActions.Move)
            {
                _connect.PanCamera(dx * 0.35f, dy * 0.35f);
                _glView?.Renderer.SetCamera(_connect.Session.CamX, _connect.Session.CamY);
            }

            e.Handled = true;
        };
    }

    void RebuildServerList()
    {
        if (_serverList is null || _hub is null)
            return;
        _serverList.RemoveAllViews();
        var pad = (int)TypedValue.ApplyDimension(ComplexUnitType.Dip, 12, Resources!.DisplayMetrics);
        foreach (var server in _hub.All)
        {
            var row = new LinearLayout(this)
            {
                Orientation = Orientation.Vertical,
                Clickable = true,
                Focusable = true,
            };
            row.SetBackgroundResource(Resource.Drawable.hub_server_row);
            row.SetPadding(pad, pad, pad, pad);
            var lp = new LinearLayout.LayoutParams(
                ViewGroup.LayoutParams.MatchParent,
                ViewGroup.LayoutParams.WrapContent);
            lp.BottomMargin = pad / 2;
            row.LayoutParameters = lp;

            var title = new TextView(this)
            {
                Text = server.Name,
                TextSize = 14,
            };
            title.SetTextColor(Color.ParseColor("#F3F0E8"));

            var addr = new TextView(this)
            {
                Text = server.ConnectUri + (server.Region is null ? "" : $" · {server.Region}"),
                TextSize = 11,
            };
            addr.SetTextColor(Color.ParseColor("#D4C5A9"));
            addr.Typeface = Typeface.Monospace;

            row.AddView(title);
            row.AddView(addr);
            var captured = server;
            row.Click += (_, _) =>
            {
                SelectServer(captured);
                RebuildServerList();
                _ = RefreshHomeStatusAsync();
            };

            if (_selected?.Id == server.Id)
                row.SetBackgroundColor(Color.ParseColor("#525A66"));

            _serverList.AddView(row);
        }
    }

    void SelectServer(HubServerEntry? server)
    {
        if (server is null) return;
        _selected = server;
        _connect.Endpoint = server.ToEndpoint();
        if (_serverChip != null)
            _serverChip.Text = $"выбран: {server.Name}\n{server.ConnectUri}";
        if (_connectStatus != null)
            _connectStatus.Text = $"Сервер: {server.Name}";
    }

    void AddCustomServer()
    {
        var raw = _customServer?.Text?.Trim() ?? "";
        var entry = HubServerEntry.TryParse(raw);
        if (entry is null || _hub is null)
        {
            Toast.MakeText(this, "Адрес: ss14://host:port", ToastLength.Short)?.Show();
            return;
        }

        _hub.AddCustom(entry);
        SelectServer(entry);
        RebuildServerList();
        if (_customServer != null) _customServer.Text = "";
        Toast.MakeText(this, $"Добавлен {entry.Name}", ToastLength.Short)?.Show();
    }

    void ApplyOrientation(bool forceLandscape)
    {
        var want = forceLandscape || _connect.Observing || _connect.InLobby;
        if (want == _landscapeLocked && want)
        {
            RequestedOrientation = ScreenOrientation.SensorLandscape;
            return;
        }

        _landscapeLocked = want;
        RequestedOrientation = want
            ? ScreenOrientation.SensorLandscape
            : ScreenOrientation.Portrait;
    }

    async Task RefreshHomeStatusAsync()
    {
        try
        {
            await _connect.RefreshStatusAsync();
            RunOnUiThread(() =>
            {
                if (_connect.LastStatus is { Online: true } st && _connectStatus != null && !_connect.Busy)
                    _connectStatus.Text = $"{st.Name} · {st.Players}/{st.MaxPlayers}";
                RenderStatus();
            });
        }
        catch { /* ignore */ }
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
        ApplyOrientation(forceLandscape: false);
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
                _connect.PanCamera(-(x - _lastTouchX), y - _lastTouchY);
                _lastTouchX = x;
                _lastTouchY = y;
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
        _authUiStatus = $"Вход: {user}…";
        RenderStatus();

        try
        {
            var result = await _authClient.AuthenticateAsync(user, pass, tfa, _loginCts.Token);
            if (!result.Ok)
            {
                _authUiStatus = $"Ошибка [{result.ErrorCode}]: {result.Error}";
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
            _authUiStatus = $"Ошибка входа: {ex.Message}";
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
        _authUiStatus = "Вы вышли";
        RenderStatus();
    }

    async Task RunConnectAsync()
    {
        if (_connect.Busy) return;
        if (_selected is null)
        {
            Toast.MakeText(this, "Выберите сервер", ToastLength.Short)?.Show();
            return;
        }

        _connect.Endpoint = _selected.ToEndpoint();
        _connectCts?.Cancel();
        _connectCts = new CancellationTokenSource();
        if (_connectBtn != null) _connectBtn.Enabled = false;
        ApplyOrientation(forceLandscape: true);
        RenderStatus();
        try
        {
            await _connect.RunAsync(_connectCts.Token);
            if (_connect.InLobby || _connect.Observing)
            {
                _authUiStatus = $"В сети: {_connect.Session.UserName}";
                if (_charName != null && string.IsNullOrWhiteSpace(_charName.Text))
                    _charName.Text = _connect.Session.UserName;
            }
            else
            {
                ApplyOrientation(forceLandscape: false);
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

        if (lobby || observing)
            ApplyOrientation(forceLandscape: true);
        else if (!_connect.Busy)
            ApplyOrientation(forceLandscape: false);

        if (observing)
            EnsureGl();

        if (_authStatus != null)
            _authStatus.Text = _authUiStatus;
        if (_connectStatus != null && string.IsNullOrWhiteSpace(_connectStatus.Text))
            _connectStatus.Text = _connect.Summary;
        if (_connect.Busy || _connect.InLobby || _connect.Observing)
        {
            if (_connectStatus != null)
                _connectStatus.Text = _connect.Summary;
        }

        if (_lobbyStation != null)
        {
            var name = _connect.LastStatus?.Name ?? _selected?.Name;
            _lobbyStation.Text = string.IsNullOrWhiteSpace(name) ? "Server" : name;
        }

        if (_lobbyRound != null)
        {
            if (_connect.Session.IsConnected)
            {
                var st = _connect.Session.LocalStatus;
                var map = _connect.LastStatus?.Map;
                var preset = _connect.LastStatus?.Preset;
                var ready = _connect.Session.IsReady ? " · READY" : "";
                _lobbyRound.Text = string.IsNullOrWhiteSpace(map)
                    ? $"{st}{ready}"
                    : $"{st}{ready} · {map} · {preset}";
            }
            else
                _lobbyRound.Text = "Offline";
        }

        if (_lobbyAccount != null)
            _lobbyAccount.Text = $"account: {_connect.Session.UserName ?? "—"}";

        if (_lobbyPlayers != null)
        {
            var players = _connect.Session.Players;
            if (players.Count == 0)
                _lobbyPlayers.Text = lobby ? "…" : "—";
            else
            {
                _lobbyPlayers.Text = string.Join('\n', players
                    .OrderBy(p => p.Status)
                    .ThenBy(p => p.Name, StringComparer.OrdinalIgnoreCase)
                    .Select(p => $"[{StatusLabel(p.Status)}]  {p.Name}"));
            }
        }

        if (_lobbyDetail != null)
        {
            _lobbyDetail.Text = _connect.Session.Format();
            if (!string.IsNullOrWhiteSpace(_connect.DebugLog))
                _lobbyDetail.Text += "\n---\n" + _connect.DebugLog;
        }

        if (_joinDebug != null)
            _joinDebug.Text = string.IsNullOrWhiteSpace(_connect.DebugLog) ? _connect.Summary : _connect.DebugLog;

        if (_connect.LastProgress is { } prog && _connect.Busy)
            ApplyProgress(prog);

        if (_observeHud != null && observing)
        {
            var s = _connect.Session;
            _observeHud.Text =
                $"{s.Detail}\n" +
                $"MsgState={s.StatesReceived}  {s.SerializerStatus}\n" +
                $"eye: {s.LastEye?.Detail ?? s.LastEyeHint}\n" +
                $"cam=({s.CamX:0},{s.CamY:0}) · drag / D-pad";
            _glView?.Renderer.SetCamera(s.CamX, s.CamY);
        }

        if (_readyBtn != null)
            _readyBtn.Text = _connect.Session.IsReady ? "Unready" : GetString(Resource.String.btn_ready);
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
