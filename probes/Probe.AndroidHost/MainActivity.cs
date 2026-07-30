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
    TextView? _lobbyCharStatus;
    TextView? _lobbyPlayerCount;
    TextView? _lobbyContentStatus;
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
    LinearLayout? _authLoginFields;
    LinearLayout? _hubBody;
    View? _hubHeader;
    TextView? _hubStatus;
    TextView? _hubHeaderTitle;
    TextView? _hubHeaderChevron;
    EditText? _hubSearch;
    Button? _connectBtn;
    Button? _loginBtn;
    Button? _logoutBtn;
    Button? _disconnectBtn;
    Button? _observeLeaveBtn;
    Button? _readyBtn;
    Button? _observeBtn;
    Button? _addServerBtn;
    Button? _refreshHubBtn;
    FrameLayout? _observeGl;
    GlesClearSurfaceView? _glView;

    AndroidPlatformHost? _host;
    readonly ConnectSession _connect = new();
    readonly Ss14AuthClient _authClient = new();
    readonly ServerInfoClient _infoClient = new();
    HubServerCatalog? _hub;
    HubServerEntry? _selected;
    readonly HashSet<string> _expandedServers = new(StringComparer.OrdinalIgnoreCase);
    readonly Dictionary<string, string> _descCache = new(StringComparer.OrdinalIgnoreCase);
    CancellationTokenSource? _connectCts;
    CancellationTokenSource? _loginCts;
    CancellationTokenSource? _hubCts;
    Timer? _uiTimer;
    string _authUiStatus = "Войдите через аккаунт SS14";
    string _hubSearchQuery = "";
    bool _loginBusy;
    bool _hubBusy;
    bool _hubPanelExpanded;
    float _lastTouchX, _lastTouchY;
    bool _dragging;
    bool _landscapeLocked;

    protected override void OnCreate(Bundle? savedInstanceState)
    {
        SodiumAndroidBootstrap.EnsureLoaded();
        base.OnCreate(savedInstanceState);
        RequestedOrientation = ScreenOrientation.Portrait;
        SetContentView(Resource.Layout.activity_main);
        ApplySafeAreaInsets();

        var paths = AndroidContentPaths.FromContext(this);
        _host = new AndroidPlatformHost(paths);
        _host.EnsureDirectories();
        _host.OnLifecycle(PlatformLifecycle.Created);
        _connect.AuthConfigPath = System.IO.Path.Combine(paths.FilesDir, "auth-session.json");
        _connect.ContentRoot = paths.ContentDir;
        ClientHwid.StorageDirectory = paths.UserDataDir;
        _hub = new HubServerCatalog(System.IO.Path.Combine(paths.FilesDir, "hub-favorites.json"));
        _connect.ProgressChanged += p => RunOnUiThread(() => ApplyProgress(p));
        _connect.DebugChanged += () => RunOnUiThread(RenderStatus);

        BindViews();
        WireButtons();
        RebuildServerList();

        var existing = AuthSessionConfig.TryLoad(_connect.AuthConfigPath);
        if (existing?.HasRequiredFields == true)
        {
            _authUiStatus = existing.StatusLine();
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
        _ = RefreshHubAsync();
        _ = RefreshHomeStatusAsync();
    }

    void ApplySafeAreaInsets()
    {
        var root = FindViewById(Resource.Id.root);
        if (root is null || Window is null)
            return;

        // Edge-to-edge (Android 15+) draws under status/nav bars; pad content for cutouts.
        if (OperatingSystem.IsAndroidVersionAtLeast(30))
            Window.SetDecorFitsSystemWindows(false);

        Window.SetStatusBarColor(Color.Transparent);
        Window.SetNavigationBarColor(Color.ParseColor("#1E232A"));

        if (OperatingSystem.IsAndroidVersionAtLeast(30) && Window.InsetsController is { } ctrl)
        {
            ctrl.SetSystemBarsAppearance(
                0,
                (int)WindowInsetsControllerAppearance.LightStatusBars
                | (int)WindowInsetsControllerAppearance.LightNavigationBars);
        }

        root.SetOnApplyWindowInsetsListener(new SafeAreaInsetsListener());
        root.RequestApplyInsets();
    }

    sealed class SafeAreaInsetsListener : Java.Lang.Object, View.IOnApplyWindowInsetsListener
    {
        public WindowInsets OnApplyWindowInsets(View v, WindowInsets insets)
        {
            if (OperatingSystem.IsAndroidVersionAtLeast(30))
            {
                var bars = insets.GetInsets(
                    WindowInsets.Type.SystemBars() | WindowInsets.Type.DisplayCutout());
                v.SetPadding(bars.Left, bars.Top, bars.Right, bars.Bottom);
                return insets;
            }

#pragma warning disable CS0618
            v.SetPadding(
                insets.SystemWindowInsetLeft,
                insets.SystemWindowInsetTop,
                insets.SystemWindowInsetRight,
                insets.SystemWindowInsetBottom);
#pragma warning restore CS0618
            return insets;
        }
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
        _lobbyCharStatus = FindViewById<TextView>(Resource.Id.lobby_char_status);
        _lobbyPlayerCount = FindViewById<TextView>(Resource.Id.lobby_player_count);
        _lobbyContentStatus = FindViewById<TextView>(Resource.Id.lobby_content_status);
        _observeHud = FindViewById<TextView>(Resource.Id.observe_hud);
        _joinDebug = FindViewById<TextView>(Resource.Id.join_debug);
        _debugToggle = FindViewById<TextView>(Resource.Id.debug_toggle);
        _downloadLabel = FindViewById<TextView>(Resource.Id.download_label);
        _downloadPct = FindViewById<TextView>(Resource.Id.download_pct);
        _downloadProgress = FindViewById<ProgressBar>(Resource.Id.download_progress);
        _authUsername = FindViewById<EditText>(Resource.Id.auth_username);
        _authPassword = FindViewById<EditText>(Resource.Id.auth_password);
        _authTfa = FindViewById<EditText>(Resource.Id.auth_tfa);
        _authLoginFields = FindViewById<LinearLayout>(Resource.Id.auth_login_fields);
        _hubStatus = FindViewById<TextView>(Resource.Id.hub_status);
        _hubBody = FindViewById<LinearLayout>(Resource.Id.hub_body);
        _hubHeader = FindViewById(Resource.Id.hub_header);
        _hubHeaderTitle = FindViewById<TextView>(Resource.Id.hub_header_title);
        _hubHeaderChevron = FindViewById<TextView>(Resource.Id.hub_header_chevron);
        _hubSearch = FindViewById<EditText>(Resource.Id.hub_search);
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
        _refreshHubBtn = FindViewById<Button>(Resource.Id.btn_refresh_hub);
        _observeGl = FindViewById<FrameLayout>(Resource.Id.observe_gl);

        foreach (var b in new[] { _loginBtn, _logoutBtn, _connectBtn, _disconnectBtn, _observeLeaveBtn, _readyBtn, _observeBtn, _addServerBtn, _refreshHubBtn })
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
        if (_refreshHubBtn != null)
        {
            _refreshHubBtn.Click += async (_, e) =>
            {
                // Don't toggle collapse when tapping refresh.
                await RefreshHubAsync();
            };
        }

        if (_hubHeader != null)
            _hubHeader.Click += (_, _) => SetHubPanelExpanded(!_hubPanelExpanded);

        if (_hubSearch != null)
        {
            _hubSearch.TextChanged += (_, _) =>
            {
                _hubSearchQuery = _hubSearch.Text?.Trim() ?? "";
                RebuildServerList();
            };
        }

        ApplyHubPanelVisibility();

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
                _connect.Session.EnsureSerializerPublic();
                if (!_connect.Observe())
                {
                    Toast.MakeText(this, "Нет соединения с сервером", ToastLength.Short)?.Show();
                    return;
                }

                EnsureGl();
                _glView?.Renderer.SetGhostMode(true);
                ApplyOrientation(forceLandscape: true);
                Toast.MakeText(this, "Наблюдение…", ToastLength.Short)?.Show();
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

    void SetHubPanelExpanded(bool expanded)
    {
        _hubPanelExpanded = expanded;
        ApplyHubPanelVisibility();
        if (_hubPanelExpanded)
            RebuildServerList();
        else
            UpdateHubHeaderTitle();
    }

    void ApplyHubPanelVisibility()
    {
        if (_hubBody != null)
            _hubBody.Visibility = _hubPanelExpanded ? ViewStates.Visible : ViewStates.Gone;
        if (_hubHeaderChevron != null)
            _hubHeaderChevron.Text = _hubPanelExpanded ? "▼" : "▶";
        UpdateHubHeaderTitle();
    }

    void UpdateHubHeaderTitle()
    {
        if (_hubHeaderTitle is null || _hub is null)
            return;
        var total = _hub.All.Count;
        var filtered = FilterServers(_hub.All).Count;
        var q = _hubSearchQuery;
        if (!_hubPanelExpanded)
            _hubHeaderTitle.Text = total > 0 ? $"СЕРВЕРЫ ({total})" : "СЕРВЕРЫ";
        else if (!string.IsNullOrWhiteSpace(q))
            _hubHeaderTitle.Text = $"СЕРВЕРЫ ({filtered}/{total})";
        else
            _hubHeaderTitle.Text = total > 0 ? $"СЕРВЕРЫ ({total})" : "СЕРВЕРЫ";
    }

    bool DescMatches(string id, string q) =>
        _descCache.TryGetValue(id, out var d) && d.Contains(q, StringComparison.OrdinalIgnoreCase);

    IReadOnlyList<HubServerEntry> FilterServers(IReadOnlyList<HubServerEntry> all)
    {
        var q = _hubSearchQuery;
        if (string.IsNullOrWhiteSpace(q))
            return all;
        return all.Where(s =>
        {
            if (s.Name.Contains(q, StringComparison.OrdinalIgnoreCase)) return true;
            if (s.Host.Contains(q, StringComparison.OrdinalIgnoreCase)) return true;
            if (s.ConnectUri.Contains(q, StringComparison.OrdinalIgnoreCase)) return true;
            if (s.Map?.Contains(q, StringComparison.OrdinalIgnoreCase) == true) return true;
            if (s.Preset?.Contains(q, StringComparison.OrdinalIgnoreCase) == true) return true;
            if (s.Region?.Contains(q, StringComparison.OrdinalIgnoreCase) == true) return true;
            if (s.Description?.Contains(q, StringComparison.OrdinalIgnoreCase) == true) return true;
            if (DescMatches(s.Id, q)) return true;
            if (s.Tags is { Count: > 0 } && s.Tags.Any(t => t.Contains(q, StringComparison.OrdinalIgnoreCase)))
                return true;
            return false;
        }).ToList();
    }

    void RebuildServerList()
    {
        UpdateHubHeaderTitle();
        if (_serverList is null || _hub is null)
            return;
        if (!_hubPanelExpanded)
            return;

        _serverList.RemoveAllViews();
        var pad = (int)TypedValue.ApplyDimension(ComplexUnitType.Dip, 10, Resources!.DisplayMetrics);
        var all = _hub.All;
        var servers = FilterServers(all);

        if (servers.Count == 0)
        {
            var empty = new TextView(this)
            {
                Text = string.IsNullOrWhiteSpace(_hubSearchQuery)
                    ? "Нет серверов — нажмите Обновить"
                    : "Ничего не найдено",
                TextSize = 13,
            };
            empty.SetTextColor(Color.ParseColor("#A8A295"));
            empty.SetPadding(pad, pad, pad, pad);
            _serverList.AddView(empty);
            return;
        }

        foreach (var server in servers)
        {
            var expanded = _expandedServers.Contains(server.Id);
            var selected = _selected?.Id == server.Id;

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
            if (selected)
                row.SetBackgroundColor(Color.ParseColor("#525A66"));

            var head = new LinearLayout(this) { Orientation = Orientation.Horizontal };
            head.SetGravity(GravityFlags.CenterVertical);

            var favBtn = new TextView(this)
            {
                Text = server.Favorite ? "★" : "☆",
                TextSize = 20,
                Clickable = true,
                Focusable = true,
            };
            favBtn.SetTextColor(Color.ParseColor(server.Favorite ? "#E8C96A" : "#A8A295"));
            favBtn.SetPadding(0, 0, pad, 0);

            var textCol = new LinearLayout(this)
            {
                Orientation = Orientation.Vertical,
                LayoutParameters = new LinearLayout.LayoutParams(0, ViewGroup.LayoutParams.WrapContent, 1f),
            };

            var title = new TextView(this)
            {
                Text = server.Name,
                TextSize = 14,
            };
            title.SetTextColor(Color.ParseColor("#F3F0E8"));

            var meta = new TextView(this)
            {
                Text = server.SummaryLine,
                TextSize = 11,
            };
            meta.SetTextColor(Color.ParseColor("#D4C5A9"));

            var addr = new TextView(this)
            {
                Text = server.ConnectUri,
                TextSize = 10,
            };
            addr.SetTextColor(Color.ParseColor("#A8A295"));
            addr.Typeface = Typeface.Monospace;

            textCol.AddView(title);
            textCol.AddView(meta);
            textCol.AddView(addr);

            var expandBtn = new TextView(this)
            {
                Text = expanded ? "▲" : "▼",
                TextSize = 14,
                Clickable = true,
                Focusable = true,
            };
            expandBtn.SetTextColor(Color.ParseColor("#D4C5A9"));
            expandBtn.SetPadding(pad, 0, 0, 0);

            head.AddView(favBtn);
            head.AddView(textCol);
            head.AddView(expandBtn);
            row.AddView(head);

            if (expanded)
            {
                var descText = _descCache.TryGetValue(server.Id, out var cached)
                    ? cached
                    : (server.Description ?? "Загрузка описания…");
                var tags = server.Tags is { Count: > 0 }
                    ? "\nТеги: " + string.Join(", ", server.Tags.Take(12))
                    : "";
                var body = new TextView(this)
                {
                    Text = descText + tags,
                    TextSize = 12,
                };
                body.SetTextColor(Color.ParseColor("#F3F0E8"));
                body.SetPadding(0, pad / 2, 0, 0);
                row.AddView(body);
            }

            var captured = server;
            favBtn.Click += (_, _) =>
            {
                _hub.ToggleFavorite(captured.Id);
                RebuildServerList();
            };
            expandBtn.Click += (_, _) =>
            {
                if (!_expandedServers.Add(captured.Id))
                    _expandedServers.Remove(captured.Id);
                RebuildServerList();
                if (_expandedServers.Contains(captured.Id))
                    _ = LoadDescriptionAsync(captured);
            };
            row.Click += (_, _) =>
            {
                SelectServer(captured);
                RebuildServerList();
                _ = RefreshHomeStatusAsync();
            };

            _serverList.AddView(row);
        }

        if (_selected is null && all.Count > 0)
            SelectServer(all.FirstOrDefault(s => s.Favorite) ?? all[0]);
    }

    async Task LoadDescriptionAsync(HubServerEntry server)
    {
        if (_descCache.ContainsKey(server.Id))
            return;
        try
        {
            var info = await _infoClient.FetchAsync(server.HttpBaseUrl);
            var desc = string.IsNullOrWhiteSpace(info.Description)
                ? "Нет описания"
                : info.Description!;
            _descCache[server.Id] = desc;
            _hub?.SetDescription(server.Id, desc);
        }
        catch (Exception ex)
        {
            _descCache[server.Id] = $"Описание недоступно: {ex.Message}";
        }

        RunOnUiThread(RebuildServerList);
    }

    async Task RefreshHubAsync()
    {
        if (_hub is null || _hubBusy)
            return;
        _hubBusy = true;
        _hubCts?.Cancel();
        _hubCts = new CancellationTokenSource();
        if (_hubStatus != null)
            _hubStatus.Text = "Обновление хаба…";
        if (_refreshHubBtn != null)
            _refreshHubBtn.Enabled = false;
        try
        {
            await _hub.RefreshFromHubAsync(_hubCts.Token);
            RunOnUiThread(() =>
            {
                if (_hubStatus != null)
                    _hubStatus.Text = $"Хабов: {_hub.All.Count} · {_hub.LastRefreshUtc:HH:mm:ss} UTC";
                UpdateHubHeaderTitle();
                RebuildServerList();
            });
        }
        catch (Exception ex)
        {
            RunOnUiThread(() =>
            {
                if (_hubStatus != null)
                    _hubStatus.Text = $"Хабу недоступен: {ex.Message}. Показаны избранные/локальные.";
                RebuildServerList();
            });
        }
        finally
        {
            _hubBusy = false;
            RunOnUiThread(() =>
            {
                if (_refreshHubBtn != null)
                    _refreshHubBtn.Enabled = true;
            });
        }
    }

    void SelectServer(HubServerEntry? server)
    {
        if (server is null) return;
        _selected = server;
        _connect.Endpoint = server.ToEndpoint();
        if (_serverChip != null)
            _serverChip.Text = $"выбран: {server.Name}\n{server.ConnectUri}\n{server.SummaryLine}";
        if (_connectStatus != null)
            _connectStatus.Text = $"Сервер: {server.Name} · {server.PlayersLabel}";
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

    readonly GlesClearRenderer.EntitySprite[] _spriteScratch = new GlesClearRenderer.EntitySprite[3500];

    void PushWorldToGl()
    {
        if (_glView is null)
            return;
        var s = _connect.Session;
        _glView.Renderer.SetContentFilesRoot(s.ContentFilesRoot);
        _glView.Renderer.SetCamera(s.CamX, s.CamY);
        var world = s.LastWorld;
        if (world is null || world.Entities.Count == 0)
        {
            _glView.Renderer.SetEntities(Array.Empty<GlesClearRenderer.EntitySprite>(), 0);
            return;
        }

        var n = Math.Min(world.Entities.Count, _spriteScratch.Length);
        for (var i = 0; i < n; i++)
        {
            var e = world.Entities[i];
            _spriteScratch[i] = new GlesClearRenderer.EntitySprite
            {
                X = e.X,
                Y = e.Y,
                Rotation = e.Rotation,
                RsiPath = e.RsiPath,
                R = e.R,
                G = e.G,
                B = e.B,
                IsControlled = e.IsControlled,
            };
        }

        _glView.Renderer.SetEntities(_spriteScratch, n);
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
        _glView.Renderer.SetContentFilesRoot(_connect.Session.ContentFilesRoot);
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
        _hubCts?.Cancel();
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

        var loggedIn = AuthSessionConfig.TryLoad(_connect.AuthConfigPath)?.HasRequiredFields == true;
        if (loggedIn)
        {
            var session = AuthSessionConfig.TryLoad(_connect.AuthConfigPath)!;
            _authUiStatus = session.StatusLine();
            if (_authStatus != null)
            {
                _authStatus.Text = _authUiStatus;
                _authStatus.SetTextColor(Color.ParseColor("#F3F0E8"));
            }
            if (_authLoginFields != null)
                _authLoginFields.Visibility = ViewStates.Gone;
            if (_logoutBtn != null)
                _logoutBtn.Visibility = ViewStates.Visible;
        }
        else
        {
            if (_authStatus != null)
                _authStatus.SetTextColor(Color.ParseColor("#A8A295"));
            if (_authLoginFields != null)
                _authLoginFields.Visibility = ViewStates.Visible;
            if (_logoutBtn != null)
                _logoutBtn.Visibility = ViewStates.Gone;
        }

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
                var phase = st == RobustSessionStatus.InGame ? "In Game"
                    : st == RobustSessionStatus.Zombie ? "Zombie"
                    : "In Lobby";
                _lobbyRound.Text = string.IsNullOrWhiteSpace(map)
                    ? phase
                    : $"{phase}  ·  {map}  ·  {preset}";
                _lobbyRound.SetTextColor(Color.ParseColor(
                    st == RobustSessionStatus.InGame ? "#6B9B6E" : "#D4C5A9"));
            }
            else
                _lobbyRound.Text = "Offline";
        }

        if (_lobbyAccount != null)
            _lobbyAccount.Text = $"{_connect.Session.UserName ?? "—"}\n{_connect.Session.UserId}";

        if (_lobbyCharStatus != null)
        {
            var ready = _connect.Session.IsReady;
            _lobbyCharStatus.Text = ready ? "Статус: READY" : "Статус: Not Ready";
            _lobbyCharStatus.SetTextColor(Color.ParseColor(ready ? "#6B9B6E" : "#D4C5A9"));
        }

        if (_lobbyContentStatus != null && lobby)
        {
            _lobbyContentStatus.Text = ContentCapabilityReport.Format(
                _connect.Session.ContentFilesRoot,
                _connect.Session.SerializerStatus,
                _connect.Session.HasMappedStrings,
                _connect.Session.LastWorld?.Entities.Count ?? 0);
        }

        if (_lobbyPlayers != null)
        {
            var players = _connect.Session.Players;
            if (_lobbyPlayerCount != null)
                _lobbyPlayerCount.Text = players.Count.ToString();
            if (players.Count == 0)
                _lobbyPlayers.Text = lobby ? "ожидание списка…" : "—";
            else
            {
                _lobbyPlayers.Text = string.Join('\n', players
                    .OrderBy(p => p.Status)
                    .ThenBy(p => p.Name, StringComparer.OrdinalIgnoreCase)
                    .Select(p =>
                    {
                        var name = p.Name.PadRight(18);
                        if (name.Length > 18) name = p.Name[..16] + "…";
                        return $"{name}  {StatusLabel(p.Status)}";
                    }));
            }
        }

        if (_lobbyDetail != null)
            _lobbyDetail.Text = $"{_connect.Session.SerializerStatus} · {_connect.Session.Detail}";

        if (_joinDebug != null)
            _joinDebug.Text = string.IsNullOrWhiteSpace(_connect.DebugLog) ? _connect.Summary : _connect.DebugLog;

        if (_connect.LastProgress is { } prog && _connect.Busy)
            ApplyProgress(prog);

        if (_observeHud != null && observing)
        {
            var s = _connect.Session;
            var worldN = s.LastWorld?.Entities.Count ?? 0;
            _observeHud.Text =
                $"{s.Detail}\n" +
                $"MsgState={s.StatesReceived}  strings={s.HasMappedStrings}  {s.SerializerStatus}\n" +
                $"world={worldN}  {_glView?.Renderer.Format()}\n" +
                $"eye: {s.LastEye?.Detail ?? s.LastEyeHint}\n" +
                $"cam=({s.CamX:0},{s.CamY:0}) · drag / D-pad";
            PushWorldToGl();
        }

        if (_readyBtn != null)
        {
            _readyBtn.Text = _connect.Session.IsReady ? "UNREADY" : "READY";
            _readyBtn.SetBackgroundResource(
                _connect.Session.IsReady ? Resource.Drawable.hub_btn_danger : Resource.Drawable.hub_btn);
        }
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
