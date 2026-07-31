using Android.Content;
using Android.Content.PM;
using Android.Graphics;
using Android.Util;
using Android.Views;
using Android.Widget;
using Port.Content;
using Port.Net;
using Port.Platform.Android;
using Port.Platform.Android.Audio;
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
    ConfigurationChanges = ConfigChanges.Orientation
        | ConfigChanges.ScreenSize
        | ConfigChanges.ScreenLayout
        | ConfigChanges.SmallestScreenSize
        | ConfigChanges.KeyboardHidden
        | ConfigChanges.UiMode
        | ConfigChanges.Density
        | ConfigChanges.FontScale
        | ConfigChanges.LayoutDirection
        | ConfigChanges.ColorMode)]
public class MainActivity : Activity
{
    // Survive Activity recreate (orientation) so Observe does not dump to hub.
    static ConnectSession? s_connect;
    static bool s_uiObserving;
    static bool s_forceLandscape;
    View? _screenHome;
    View? _screenLobby;
    View? _screenObserve;
    View? _screenLoading;
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
    TextView? _observeFps;
    TextView? _observeDiag;
    View? _observeDiagScroll;
    TextView? _joinDebug;
    TextView? _debugToggle;
    Button? _copyLogBtn;
    Button? _observeCopyBtn;
    bool _observeDiagOpen;
    int _diagUiTick;
    TextView? _downloadLabel;
    TextView? _downloadPct;
    ProgressBar? _downloadProgress;
    TextView? _loadingTitle;
    TextView? _loadingServer;
    TextView? _loadingStatus;
    TextView? _loadingPct;
    ProgressBar? _loadingProgress;
    Button? _loadingCancelBtn;
    FrameLayout? _loadingParallaxHost;
    FrameLayout? _lobbyParallaxHost;
    TextView? _lobbyWelcome;
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
    AndroidAudioPlayer? _audioPlayer;

    AndroidPlatformHost? _host;
    ConnectSession _connect = null!;
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
    bool _uiObserving;
    float _flightX;
    float _flightY;
    bool _immersiveApplied;
    long _lastFullUiMs;
    int _uiTick;
    int _lastChatVersion = -1;
    int _chatChannelIdx;
    bool _chatExpanded = true;
    Robust.Shared.Timing.GameTick _lastPushedWorldTick;
    int _lastPushedWorldEpoch;
    static readonly string[] ChatChannelLabels = ["Рядом", "LOOC", "OOC", "Шёпот", "Emote"];
    static readonly string[] ChatChannelCmds = ["say", "looc", "ooc", "whisper", "me"];

    protected override void OnCreate(Bundle? savedInstanceState)
    {
        SodiumAndroidBootstrap.EnsureLoaded();
        ZstdAndroidBootstrap.EnsureLoaded();
        // Capture managed crashes into diag before the process dies.
        global::Android.Runtime.AndroidEnvironment.UnhandledExceptionRaiser += (_, args) =>
        {
            try
            {
                DiagLog.Error($"UNHANDLED {args.Exception?.GetType().Name}: {args.Exception?.Message}");
            }
            catch { /* ignore */ }
        };
        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
        {
            try
            {
                if (args.ExceptionObject is Exception ex)
                    DiagLog.Error($"FATAL {ex.GetType().Name}: {ex.Message}");
            }
            catch { /* ignore */ }
        };
        base.OnCreate(savedInstanceState);
        // Landscape locked from loading onward. Portrait only on hub home.
        if (s_forceLandscape || s_connect?.Busy == true || s_connect?.InLobby == true
            || s_connect?.Observing == true || s_uiObserving)
            RequestedOrientation = ScreenOrientation.Landscape;
        else
            RequestedOrientation = ScreenOrientation.Portrait;
        SetContentView(Resource.Layout.activity_main);
        ApplySafeAreaInsets();

        var paths = AndroidContentPaths.FromContext(this);
        _host = new AndroidPlatformHost(paths);
        _host.EnsureDirectories();
        _host.OnLifecycle(PlatformLifecycle.Created);
        _connect = s_connect ??= new ConnectSession();
        _uiObserving = s_uiObserving;
        _connect.AuthConfigPath = System.IO.Path.Combine(paths.FilesDir, "auth-session.json");
        _connect.ContentRoot = paths.ContentDir;
        ClientHwid.StorageDirectory = paths.UserDataDir;
        _hub = new HubServerCatalog(System.IO.Path.Combine(paths.FilesDir, "hub-favorites.json"));
        _connect.ProgressChanged -= OnProgressChanged;
        _connect.ProgressChanged += OnProgressChanged;
        _connect.DebugChanged -= OnDebugChanged;
        _connect.DebugChanged += OnDebugChanged;

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

        _uiTimer = new Timer(50);
        _uiTimer.Elapsed += (_, _) =>
        {
            _host?.Clock.Pulse();
            var observing = _uiObserving || _connect.Observing;
            if (observing)
            {
                _connect.Session.SetFlightInput(_flightX, _flightY);
                _connect.Session.TickFlight(0.05f);
            }

            RunOnUiThread(() =>
            {
                if (observing)
                {
                    _uiTick++;
                    // Camera every tick; full entity/tile/audio only when ToSequence changes.
                    PushWorldToGl(forceEntities: false);
                    // Overlay + FPS ≤4 Hz (every 5 × 50ms).
                    if (_uiTick % 5 == 0)
                        UpdateObserveOverlay();
                    var now = Environment.TickCount64;
                    if (now - _lastFullUiMs > 1500)
                    {
                        _lastFullUiMs = now;
                        RenderStatus(skipWorldPush: true);
                    }
                }
                else
                {
                    RenderStatus();
                }
            });
        };
        _uiTimer.AutoReset = true;

        RenderStatus();
        _ = RefreshHubAsync();
        _ = RefreshHomeStatusAsync();
    }

    void OnProgressChanged(ContentDownloadProgress p) => RunOnUiThread(() => ApplyProgress(p));
    void OnDebugChanged() => RunOnUiThread(() => RenderStatus());

    public override void OnConfigurationChanged(Android.Content.Res.Configuration newConfig)
    {
        base.OnConfigurationChanged(newConfig);
        // Keep lobby/observe UI after rotation without tearing down the session.
        RenderStatus();
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

        root.SetOnApplyWindowInsetsListener(new SafeAreaInsetsListener(() =>
            _uiObserving || _connect.Observing || s_uiObserving
            || _connect.InLobby || _connect.Busy));
        root.RequestApplyInsets();
    }

    void ApplyObserveImmersive(bool on)
    {
        if (Window is null) return;
        var root = FindViewById(Resource.Id.root);

        if (OperatingSystem.IsAndroidVersionAtLeast(30) && Window.InsetsController is { } ctrl)
        {
            if (on)
            {
                ctrl.Hide(WindowInsets.Type.SystemBars());
                ctrl.SystemBarsBehavior =
                    (int)WindowInsetsControllerBehavior.ShowTransientBarsBySwipe;
                root?.SetPadding(0, 0, 0, 0);
                Window.SetNavigationBarColor(Color.Transparent);
            }
            else
            {
                ctrl.Show(WindowInsets.Type.SystemBars());
                Window.SetNavigationBarColor(Color.ParseColor("#1E232A"));
                root?.RequestApplyInsets();
            }
        }
        else
        {
#pragma warning disable CS0618
            if (on)
            {
                Window.AddFlags(WindowManagerFlags.Fullscreen);
                root?.SetPadding(0, 0, 0, 0);
            }
            else
            {
                Window.ClearFlags(WindowManagerFlags.Fullscreen);
                root?.RequestApplyInsets();
            }
#pragma warning restore CS0618
        }
    }

    sealed class SafeAreaInsetsListener : Java.Lang.Object, View.IOnApplyWindowInsetsListener
    {
        readonly Func<bool> _isObserving;

        public SafeAreaInsetsListener(Func<bool> isObserving) => _isObserving = isObserving;

        public WindowInsets OnApplyWindowInsets(View v, WindowInsets insets)
        {
            if (_isObserving())
            {
                v.SetPadding(0, 0, 0, 0);
                return insets;
            }

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
        _screenLoading = FindViewById(Resource.Id.screen_loading);
        _loadingParallaxHost = FindViewById<FrameLayout>(Resource.Id.loading_parallax_host);
        if (_loadingParallaxHost != null && _loadingParallaxHost.ChildCount == 0)
        {
            _loadingParallaxHost.AddView(new ParallaxStarfieldView(this),
                new FrameLayout.LayoutParams(
                    ViewGroup.LayoutParams.MatchParent,
                    ViewGroup.LayoutParams.MatchParent));
        }

        _lobbyParallaxHost = FindViewById<FrameLayout>(Resource.Id.lobby_parallax_host);
        if (_lobbyParallaxHost != null && _lobbyParallaxHost.ChildCount == 0)
        {
            _lobbyParallaxHost.AddView(new ParallaxStarfieldView(this),
                new FrameLayout.LayoutParams(
                    ViewGroup.LayoutParams.MatchParent,
                    ViewGroup.LayoutParams.MatchParent));
        }

        _lobbyWelcome = FindViewById<TextView>(Resource.Id.lobby_welcome);
        if (_lobbyWelcome != null)
            _lobbyWelcome.Visibility = ViewStates.Gone;

        _loadingTitle = FindViewById<TextView>(Resource.Id.loading_title);
        _loadingServer = FindViewById<TextView>(Resource.Id.loading_server);
        _loadingStatus = FindViewById<TextView>(Resource.Id.loading_status);
        _loadingPct = FindViewById<TextView>(Resource.Id.loading_pct);
        _loadingProgress = FindViewById<ProgressBar>(Resource.Id.loading_progress);
        _loadingCancelBtn = FindViewById<Button>(Resource.Id.btn_loading_cancel);
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
        _observeFps = FindViewById<TextView>(Resource.Id.observe_fps);
        _observeDiag = FindViewById<TextView>(Resource.Id.observe_diag);
        _observeDiagScroll = FindViewById(Resource.Id.observe_diag_scroll);
        _observeCopyBtn = FindViewById<Button>(Resource.Id.btn_observe_copy);
        _joinDebug = FindViewById<TextView>(Resource.Id.join_debug);
        _debugToggle = FindViewById<TextView>(Resource.Id.debug_toggle);
        _copyLogBtn = FindViewById<Button>(Resource.Id.btn_copy_log);
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

        foreach (var b in new[] { _loginBtn, _logoutBtn, _connectBtn, _disconnectBtn, _observeLeaveBtn, _readyBtn, _observeBtn, _addServerBtn, _refreshHubBtn, _loadingCancelBtn, _copyLogBtn, _observeCopyBtn })
            ClearMaterialTint(b);
        ClearMaterialTint(FindViewById<Button>(Resource.Id.btn_touch_up));
        ClearMaterialTint(FindViewById<Button>(Resource.Id.btn_touch_down));
        ClearMaterialTint(FindViewById<Button>(Resource.Id.btn_touch_left));
        ClearMaterialTint(FindViewById<Button>(Resource.Id.btn_touch_right));
        ClearMaterialTint(FindViewById<Button>(Resource.Id.btn_ghost_return));
        ClearMaterialTint(FindViewById<Button>(Resource.Id.btn_warps));
        ClearMaterialTint(FindViewById<Button>(Resource.Id.btn_ghost_follow));
        ClearMaterialTint(FindViewById<Button>(Resource.Id.btn_ghost_roles));
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
                if (_copyLogBtn != null)
                    _copyLogBtn.Visibility = _debugOpen ? ViewStates.Visible : ViewStates.Gone;
                _debugToggle.Text = _debugOpen ? "Скрыть журнал" : GetString(Resource.String.debug_toggle);
            };
        }

        if (_copyLogBtn != null)
            _copyLogBtn.Click += (_, _) => CopyDiagToClipboard();
        if (_observeCopyBtn != null)
            _observeCopyBtn.Click += (_, _) => CopyDiagToClipboard();
        if (_observeHud != null)
        {
            _observeHud.Click += (_, _) =>
            {
                _observeDiagOpen = !_observeDiagOpen;
                if (_observeDiagScroll != null)
                    _observeDiagScroll.Visibility = _observeDiagOpen ? ViewStates.Visible : ViewStates.Gone;
                RefreshObserveDiag(force: true);
            };
            _observeHud.LongClick += (_, e) =>
            {
                CopyDiagToClipboard();
                e.Handled = true;
            };
        }
        if (_joinDebug != null)
        {
            _joinDebug.LongClick += (_, e) =>
            {
                CopyDiagToClipboard();
                e.Handled = true;
            };
        }

        if (_loginBtn != null)
            _loginBtn.Click += async (_, _) => await RunLoginAsync();
        if (_logoutBtn != null)
            _logoutBtn.Click += (_, _) => Logout();
        if (_connectBtn != null)
            _connectBtn.Click += async (_, _) => await RunConnectAsync();
        if (_loadingCancelBtn != null)
        {
            _loadingCancelBtn.Click += (_, _) =>
            {
                _connectCts?.Cancel();
                if (_loadingStatus != null)
                    _loadingStatus.Text = "Отмена…";
            };
        }
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
                if (!_connect.InLobby && !_connect.Session.IsConnected)
                {
                    Toast.MakeText(this, "Нет соединения с сервером", ToastLength.Short)?.Show();
                    return;
                }

                // Enter observe UI first so orientation/recreate cannot drop us to hub mid-command.
                _uiObserving = true;
                s_uiObserving = true;
                EnsureGl();
                _glView?.Renderer.SetGhostMode(true);
                // Landscape only once we are already on the server (lobby→observe).
                ApplyOrientation(landscape: true);
                RenderStatus();

                if (!_connect.Observe())
                {
                    _uiObserving = false;
                    s_uiObserving = false;
                    Toast.MakeText(this, "Не удалось отправить observe", ToastLength.Short)?.Show();
                    RenderStatus();
                    return;
                }

                Toast.MakeText(this, "Наблюдение — джойстик / drag / ✈ варп", ToastLength.Short)?.Show();
                EnsureJoystick();
                ApplyObserveImmersive(true);
                RefreshGhostActionButtons();
                RenderStatus();
            };

        WireZoom(Resource.Id.btn_zoom_in, 1.15f);
        WireZoom(Resource.Id.btn_zoom_out, 1f / 1.15f);

        EnsureJoystick();

        _connect.Session.WarpCycled += name =>
        {
            RunOnUiThread(() =>
                Toast.MakeText(this, $"→ {name}", ToastLength.Short)?.Show());
        };

        var warpsBtn = FindViewById<Button>(Resource.Id.btn_warps);
        if (warpsBtn != null)
        {
            warpsBtn.Click += (_, _) => ShowGhostTargetPicker(places: true);
        }

        var returnBtn = FindViewById<Button>(Resource.Id.btn_ghost_return);
        if (returnBtn != null)
        {
            returnBtn.Click += (_, _) =>
            {
                try
                {
                    if (_connect.Session.ReturnToBody())
                        Toast.MakeText(this, "Возврат в тело…", ToastLength.Short)?.Show();
                    else
                        Toast.MakeText(this, "Нельзя вернуться в тело", ToastLength.Short)?.Show();
                }
                catch (Exception ex)
                {
                    Toast.MakeText(this, ex.Message, ToastLength.Short)?.Show();
                }
            };
        }

        var followBtn = FindViewById<Button>(Resource.Id.btn_ghost_follow);
        if (followBtn != null)
        {
            followBtn.Click += (_, _) => ShowGhostTargetPicker(places: false);
        }

        var rolesBtn = FindViewById<Button>(Resource.Id.btn_ghost_roles);
        if (rolesBtn != null)
        {
            rolesBtn.Click += (_, _) =>
            {
                try
                {
                    if (_connect.Session.OpenGhostRoles())
                        Toast.MakeText(this, "ghostroles…", ToastLength.Short)?.Show();
                    else
                        Toast.MakeText(this, "Роли недоступны", ToastLength.Short)?.Show();
                }
                catch (Exception ex)
                {
                    Toast.MakeText(this, ex.Message, ToastLength.Short)?.Show();
                }
            };
        }

        _connect.Session.GhostUiChanged += () => RunOnUiThread(() =>
        {
            RefreshGhostActionButtons();
            PushWorldToGl();
        });

        _connect.Session.OnTextureLoadBurst = () =>
        {
            try { _glView?.Renderer.ArmTextureLoadBurst(); }
            catch { /* ignore */ }
        };

        WireObserveChat();
    }

    List<GhostWarpEntry>? _warpPickerItems;

    void ShowGhostTargetPicker(bool places)
    {
        try
        {
            _connect.Session.RequestGhostWarps();
            var all = _connect.Session.GhostWarps;
            var items = places
                ? all.Where(w => w.IsWarpPoint || w.Category == "place").ToList()
                : all.Where(w => !w.IsWarpPoint)
                    .OrderBy(w => w.Category == "antag" ? 0 : 1)
                    .ThenBy(w => w.DisplayName, StringComparer.OrdinalIgnoreCase)
                    .ToList();

            if (items.Count == 0)
            {
                Toast.MakeText(this, places ? "Запрос локаций…" : "Запрос игроков…", ToastLength.Short)?.Show();
                return;
            }

            _warpPickerItems = items;
            var overlay = FindViewById<FrameLayout>(Resource.Id.observe_warp_overlay);
            var list = FindViewById<ListView>(Resource.Id.observe_warp_list);
            var title = FindViewById<TextView>(Resource.Id.observe_warp_title);
            var close = FindViewById<Button>(Resource.Id.btn_observe_warp_close);
            var ghostnado = FindViewById<Button>(Resource.Id.btn_observe_warp_ghostnado);
            if (overlay is null || list is null)
                return;

            if (title != null)
                title.Text = places ? "Варп — локации" : "Следить — игроки";

            if (ghostnado != null)
            {
                ghostnado.Visibility = places ? ViewStates.Gone : ViewStates.Visible;
                ghostnado.Click -= OnGhostnadoClick;
                ghostnado.Click += OnGhostnadoClick;
            }

            if (close != null)
            {
                close.Click -= OnWarpCloseClick;
                close.Click += OnWarpCloseClick;
            }

            overlay.Click -= OnWarpCloseClick;
            overlay.Click += OnWarpCloseClick;

            var labels = items.Select(w =>
            {
                var prefix = w.Category switch
                {
                    "antag" => "◆ ",
                    "place" => "○ ",
                    _ => "· ",
                };
                return string.IsNullOrEmpty(w.Subtitle)
                    ? prefix + w.DisplayName
                    : $"{prefix}{w.DisplayName}  ({w.Subtitle})";
            }).ToList();

            list.Adapter = new ArrayAdapter<string>(
                this,
                Android.Resource.Layout.SimpleListItem1,
                Android.Resource.Id.Text1,
                labels);
            list.ItemClick -= OnWarpItemClick;
            list.ItemClick += OnWarpItemClick;
            overlay.Visibility = ViewStates.Visible;
        }
        catch (Exception ex)
        {
            Toast.MakeText(this, ex.Message, ToastLength.Short)?.Show();
        }
    }

    void OnWarpCloseClick(object? sender, EventArgs e)
    {
        var overlay = FindViewById<FrameLayout>(Resource.Id.observe_warp_overlay);
        if (overlay != null)
            overlay.Visibility = ViewStates.Gone;
        _warpPickerItems = null;
    }

    void OnGhostnadoClick(object? sender, EventArgs e)
    {
        try
        {
            if (_connect.Session.Ghostnado())
                Toast.MakeText(this, "Ghostnado…", ToastLength.Short)?.Show();
            else
                Toast.MakeText(this, "Ghostnado недоступен", ToastLength.Short)?.Show();
        }
        catch (Exception ex)
        {
            Toast.MakeText(this, ex.Message, ToastLength.Short)?.Show();
        }

        OnWarpCloseClick(sender, e);
    }

    void OnWarpItemClick(object? sender, AdapterView.ItemClickEventArgs e)
    {
        var items = _warpPickerItems;
        if (items is null || e.Position < 0 || e.Position >= items.Count)
            return;
        var target = items[e.Position];
        if (_connect.Session.WarpTo(target.Entity))
            Toast.MakeText(this, $"→ {target.DisplayName}", ToastLength.Short)?.Show();
        OnWarpCloseClick(sender, EventArgs.Empty);
    }

    void RefreshGhostActionButtons()
    {
        var s = _connect.Session;
        var ret = FindViewById<Button>(Resource.Id.btn_ghost_return);
        if (ret != null)
        {
            ret.Enabled = s.CanReturnToBody;
            ret.Alpha = s.CanReturnToBody ? 1f : 0.45f;
        }

        var roles = FindViewById<Button>(Resource.Id.btn_ghost_roles);
        if (roles != null)
        {
            roles.Enabled = s.CanTakeGhostRoles;
            roles.Alpha = s.CanTakeGhostRoles ? 1f : 0.45f;
            roles.Text = s.GhostRoleCount > 0 ? $"Роли ({s.GhostRoleCount})" : "Роли";
        }
    }

    void WireObserveChat()
    {
        var input = FindViewById<EditText>(Resource.Id.observe_chat_input);
        var send = FindViewById<Button>(Resource.Id.btn_observe_chat_send);
        var channelBtn = FindViewById<Button>(Resource.Id.btn_observe_chat_channel);
        var filterBtn = FindViewById<Button>(Resource.Id.btn_observe_chat_filter);
        var panel = FindViewById<LinearLayout>(Resource.Id.observe_chat_panel);
        var scroll = FindViewById<ScrollView>(Resource.Id.observe_chat_scroll);

        void Send()
        {
            if (input is null) return;
            var text = input.Text?.Trim();
            if (string.IsNullOrEmpty(text)) return;
            var cmd = ChatChannelCmds[Math.Clamp(_chatChannelIdx, 0, ChatChannelCmds.Length - 1)];
            if (_connect.Session.SendChat(text, cmd))
                input.Text = "";
        }

        if (send != null)
            send.Click += (_, _) => Send();
        if (input != null)
        {
            input.EditorAction += (_, e) =>
            {
                if (e.ActionId == Android.Views.InputMethods.ImeAction.Send
                    || e.Event?.KeyCode == Keycode.Enter)
                    Send();
            };
        }

        if (channelBtn != null)
        {
            channelBtn.Click += (_, _) =>
            {
                _chatChannelIdx = (_chatChannelIdx + 1) % ChatChannelLabels.Length;
                channelBtn.Text = ChatChannelLabels[_chatChannelIdx];
                if (input != null)
                {
                    input.Hint = _chatChannelIdx switch
                    {
                        1 => "LOOC…",
                        2 => "OOC…",
                        3 => "шёпот…",
                        4 => "действие…",
                        _ => "E чтобы говорить…",
                    };
                }
            };
        }

        if (filterBtn != null && panel != null && scroll != null)
        {
            filterBtn.Click += (_, _) =>
            {
                _chatExpanded = !_chatExpanded;
                var lp = panel.LayoutParameters as FrameLayout.LayoutParams;
                if (lp is null) return;
                lp.Height = (int)(Resources!.DisplayMetrics!.Density * (_chatExpanded ? 180f : 44f));
                panel.LayoutParameters = lp;
                scroll.Visibility = _chatExpanded ? ViewStates.Visible : ViewStates.Gone;
            };
    }
    }

    VirtualJoystickView? _joystick;

    void EnsureJoystick()
    {
        var host = FindViewById<FrameLayout>(Resource.Id.joystick_host);
        if (host is null || _joystick != null)
            return;
        _joystick = new VirtualJoystickView(this);
        host.AddView(_joystick, new FrameLayout.LayoutParams(
            ViewGroup.LayoutParams.MatchParent,
            ViewGroup.LayoutParams.MatchParent));
        _joystick.AxisChanged += (x, y) =>
        {
            _flightX = x;
            _flightY = y;
        };
    }

    void WireZoom(int id, float factor)
    {
        var btn = FindViewById<Button>(id);
        if (btn is null) return;
        btn.Click += (_, _) =>
        {
            _connect.Session.AdjustZoom(factor);
            _glView?.Renderer.SetZoom(_connect.Session.Zoom);
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

        // Select before building rows so first paint shows the selected style.
        if (_selected is null && all.Count > 0)
            SelectServer(all.FirstOrDefault(s => s.Favorite) ?? all[0]);

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
                ContentDescription = $"Сервер {server.Name}. {server.SummaryLine}",
            };
            row.SetMinimumHeight((int)TypedValue.ApplyDimension(
                ComplexUnitType.Dip, 72, Resources!.DisplayMetrics));
            row.SetBackgroundResource(selected
                ? Resource.Drawable.hub_server_row_selected
                : Resource.Drawable.hub_server_row);
            // Extra start pad when selected so text clears the beige accent bar.
            var startPad = selected ? pad + (int)TypedValue.ApplyDimension(
                ComplexUnitType.Dip, 6, Resources.DisplayMetrics) : pad;
            row.SetPadding(startPad, pad, pad, pad);
            row.Elevation = TypedValue.ApplyDimension(
                ComplexUnitType.Dip, selected ? 6 : 1, Resources.DisplayMetrics);
            var lp = new LinearLayout.LayoutParams(
                ViewGroup.LayoutParams.MatchParent,
                ViewGroup.LayoutParams.WrapContent);
            lp.BottomMargin = pad / 2;
            row.LayoutParameters = lp;

            var head = new LinearLayout(this) { Orientation = Orientation.Horizontal };
            head.SetGravity(GravityFlags.CenterVertical);

            var favBtn = new TextView(this)
            {
                Text = server.Favorite ? "★" : "☆",
                TextSize = 20,
                Clickable = true,
                Focusable = true,
                Gravity = GravityFlags.Center,
                ContentDescription = server.Favorite
                    ? $"Убрать {server.Name} из избранного"
                    : $"Добавить {server.Name} в избранное",
            };
            favBtn.SetMinimumWidth((int)TypedValue.ApplyDimension(
                ComplexUnitType.Dip, 48, Resources.DisplayMetrics));
            favBtn.SetMinimumHeight((int)TypedValue.ApplyDimension(
                ComplexUnitType.Dip, 48, Resources.DisplayMetrics));
            favBtn.SetTextColor(Color.ParseColor(server.Favorite ? "#E8C96A" : "#A8A295"));
            favBtn.SetPadding(0, 0, pad / 2, 0);

            var textCol = new LinearLayout(this)
            {
                Orientation = Orientation.Vertical,
                LayoutParameters = new LinearLayout.LayoutParams(0, ViewGroup.LayoutParams.WrapContent, 1f),
            };

            var title = new TextView(this)
            {
                Text = server.Name,
                TextSize = selected ? 15 : 14,
            };
            title.SetTypeface(null, selected ? TypefaceStyle.Bold : TypefaceStyle.Normal);
            title.SetTextColor(Color.ParseColor(selected ? "#FFF8E7" : "#F3F0E8"));

            var meta = new TextView(this)
            {
                Text = server.SummaryLine,
                TextSize = 11,
            };
            meta.SetTextColor(Color.ParseColor(selected ? "#E8D9B8" : "#D4C5A9"));

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

            if (selected)
            {
                var selectedBadge = new TextView(this)
                {
                    Text = "ВЫБРАН",
                    TextSize = 9,
                    Gravity = GravityFlags.Center,
                };
                selectedBadge.SetTextColor(Color.ParseColor("#1E232A"));
                selectedBadge.SetBackgroundResource(Resource.Drawable.ms_badge);
                selectedBadge.SetPadding(pad / 2, pad / 4, pad / 2, pad / 4);
                var badgeLp = new LinearLayout.LayoutParams(
                    ViewGroup.LayoutParams.WrapContent,
                    ViewGroup.LayoutParams.WrapContent)
                {
                    LeftMargin = pad / 2,
                    RightMargin = pad / 2,
                };
                selectedBadge.LayoutParameters = badgeLp;
                head.AddView(favBtn);
                head.AddView(textCol);
                head.AddView(selectedBadge);
            }
            else
            {
                head.AddView(favBtn);
                head.AddView(textCol);
            }

            var expandBtn = new TextView(this)
            {
                Text = expanded ? "▲" : "▼",
                TextSize = 14,
                Clickable = true,
                Focusable = true,
                Gravity = GravityFlags.Center,
                ContentDescription = expanded
                    ? $"Свернуть описание {server.Name}"
                    : $"Развернуть описание {server.Name}",
            };
            expandBtn.SetMinimumWidth((int)TypedValue.ApplyDimension(
                ComplexUnitType.Dip, 48, Resources.DisplayMetrics));
            expandBtn.SetMinimumHeight((int)TypedValue.ApplyDimension(
                ComplexUnitType.Dip, 48, Resources.DisplayMetrics));
            expandBtn.SetTextColor(Color.ParseColor("#D4C5A9"));
            expandBtn.SetPadding(pad / 2, 0, 0, 0);

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

    void ApplyOrientation(bool landscape)
    {
        s_forceLandscape = landscape;
        var want = landscape
            ? ScreenOrientation.Landscape
            : ScreenOrientation.Portrait;
        if (RequestedOrientation == want)
        {
            _landscapeLocked = landscape;
            return;
        }

        _landscapeLocked = landscape;
        RequestedOrientation = want;
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

    GlesClearRenderer.EntitySprite[] _spriteScratch = new GlesClearRenderer.EntitySprite[8192];
    GlesClearRenderer.TileSprite[] _tileScratch = new GlesClearRenderer.TileSprite[12000];
    readonly GlesClearRenderer.SpeechBubbleSprite[] _bubbleScratch = new GlesClearRenderer.SpeechBubbleSprite[64];

    void UpdateObserveOverlay()
    {
        // Keep HUD light — Format() allocates + locks the GL gate every tick.
        if (_observeHud != null)
        {
            var s = _connect.Session;
            _observeHud.Text = $"{s.UserName ?? "ghost"} · z{s.Zoom:0.0}";
        }

        if (_observeFps != null && _glView != null)
            _observeFps.Text = $"{_glView.Renderer.Fps:0} FPS";

        // Diag strings only when the panel is open.
        if (_observeDiagOpen)
            RefreshObserveDiag(force: false);
        RefreshObserveChatHistory();
    }

    void RefreshObserveDiag(bool force)
    {
        if (!_observeDiagOpen || _observeDiag is null)
            return;
        _diagUiTick++;
        if (!force && _diagUiTick % 4 != 0)
            return;
        var gles = _glView?.Renderer.FormatDiag() ?? "(no gles)";
        var world = _connect.Session.LastWorld;
        _observeDiag.Text =
            $"{gles}\n\n" +
            $"world ents={world?.Entities.Count ?? 0} tiles={world?.Tiles?.Count ?? 0}\n" +
            $"hint={_connect.Session.LastEyeHint}\n" +
            $"detail={world?.Detail}\n\n" +
            DiagLog.Format(40);
    }

    long _lastClipboardCopyMs;

    void CopyDiagToClipboard()
    {
        try
        {
            var now = Environment.TickCount64;
            if (now - _lastClipboardCopyMs < 1500)
                return; // debounce multi-tap / long-press storms
            _lastClipboardCopyMs = now;

            var gles = _glView?.Renderer.FormatDiag();
            var report = _connect.BuildClipboardReport(gles);
            var cm = (ClipboardManager?)GetSystemService(ClipboardService);
            if (cm is null)
            {
                Toast.MakeText(this, Resource.String.toast_copy_fail, ToastLength.Short)?.Show();
                return;
            }

            cm.PrimaryClip = ClipData.NewPlainText("ss14-diag", report);
            Toast.MakeText(this, Resource.String.toast_copied, ToastLength.Short)?.Show();
            DiagLog.Info($"clipboard copied ({report.Length} chars)");
        }
        catch (Exception ex)
        {
            DiagLog.Error($"clipboard FAIL: {ex.Message}");
            Toast.MakeText(this, $"{GetString(Resource.String.toast_copy_fail)}: {ex.Message}", ToastLength.Long)?.Show();
        }
    }

    void RefreshObserveChatHistory()
    {
        var history = FindViewById<TextView>(Resource.Id.observe_chat_history);
        if (history is null)
            return;
        var ver = _connect.Session.ChatVersion;
        if (ver == _lastChatVersion)
            return;
        _lastChatVersion = ver;

        var chat = _connect.Session.ChatLines;
        if (chat.Count == 0)
        {
            history.Text = "чат…";
            return;
        }

        // Last ~40 lines — enough for PC-like panel without Spannable thrash.
        var start = Math.Max(0, chat.Count - 40);
        var sb = new Android.Text.SpannableStringBuilder();
        for (var i = start; i < chat.Count; i++)
        {
            if (sb.Length() > 0) sb.Append('\n');
            var c = chat[i];
            var lineStart = sb.Length();
            sb.Append(c.Text);
            sb.SetSpan(
                new Android.Text.Style.ForegroundColorSpan(new Color(c.Argb)),
                lineStart,
                sb.Length(),
                Android.Text.SpanTypes.ExclusiveExclusive);
        }

        history.SetText(sb, TextView.BufferType.Spannable);
        var scroll = FindViewById<ScrollView>(Resource.Id.observe_chat_scroll);
        scroll?.Post(() => scroll.FullScroll(FocusSearchDirection.Down));
    }

    void PushWorldToGl(bool forceEntities = true)
    {
        if (_glView is null)
            return;
        var s = _connect.Session;
        _glView.Renderer.SetContentFilesRoot(s.ContentFilesRoot);
        _glView.Renderer.SetTextureFetcher(s.TextureFetcher);
        _glView.Renderer.SetCamera(s.CamX, s.CamY);
        _glView.Renderer.SetCameraRotation(s.CamRotation);
        _glView.Renderer.SetZoom(s.Zoom);
        _glView.Renderer.SetFullbright(true);
        _glView.Renderer.SetDrawFov(false);
        var world = s.LastWorld;
        if (world is null)
        {
            _glView.Renderer.SetEntities(Array.Empty<GlesClearRenderer.EntitySprite>(), 0);
            _glView.Renderer.SetTiles(Array.Empty<GlesClearRenderer.TileSprite>(), 0);
            _glView.Renderer.SetSpeechBubbles(Array.Empty<GlesClearRenderer.SpeechBubbleSprite>(), 0);
            _audioPlayer?.Tick(Array.Empty<WorldAudioCue>());
            return;
        }

        var worldChanged = world.ToSequence != _lastPushedWorldTick
                           || s.WorldPushEpoch != _lastPushedWorldEpoch;
        if (!forceEntities && !worldChanged)
        {
            // Camera-only update — skip entity/tile copy & audio.
            return;
        }

        _lastPushedWorldTick = world.ToSequence;
        _lastPushedWorldEpoch = s.WorldPushEpoch;

        // WorldStateCache already sorts by DrawDepth — avoid re-OrderBy on UI thread.
        var showGhosts = s.ShowOtherGhosts;
        if (_spriteScratch.Length < world.Entities.Count)
            Array.Resize(ref _spriteScratch, Math.Max(world.Entities.Count, _spriteScratch.Length * 2));
        var n = 0;
        var limit = world.Entities.Count;
        for (var i = 0; i < world.Entities.Count && n < limit; i++)
        {
            var e = world.Entities[i];
            if (!showGhosts && !e.IsControlled && (e.IsGhost || LooksLikeOtherGhost(e)))
                continue;
            _spriteScratch[n++] = new GlesClearRenderer.EntitySprite
            {
                X = e.X,
                Y = e.Y,
                Rotation = e.Rotation,
                RsiPath = e.RsiPath,
                StateName = e.StateName,
                DrawDepth = e.DrawDepth,
                R = e.R,
                G = e.G,
                B = e.B,
                IsControlled = e.IsControlled,
                NoRotation = e.NoRotation,
                Label = e.Label,
                DirOverride = e.DirOverride,
                ScaleX = e.ScaleX,
                ScaleY = e.ScaleY,
                RotationOffset = e.RotationOffset,
            };
        }

        _glView.Renderer.SetEntities(_spriteScratch, n);

        var tiles = world.Tiles ?? Array.Empty<WorldTileDraw>();
        var tn = Math.Min(tiles.Count, _tileScratch.Length);
        for (var i = 0; i < tn; i++)
        {
            var t = tiles[i];
            _tileScratch[i] = new GlesClearRenderer.TileSprite
            {
                X = t.X,
                Y = t.Y,
                R = t.R,
                G = t.G,
                B = t.B,
                RsiPath = t.RsiPath,
                StateName = t.StateName,
                Rotation = t.Rotation,
                Variant = t.Variant,
                RotationMirroring = t.RotationMirroring,
            };
        }

        _glView.Renderer.SetTiles(_tileScratch, tn);

        var bubbles = s.SnapshotSpeechBubbles();
        var bn = Math.Min(bubbles.Count, _bubbleScratch.Length);
        for (var i = 0; i < bn; i++)
        {
            var b = bubbles[i];
            _bubbleScratch[i] = new GlesClearRenderer.SpeechBubbleSprite
            {
                X = b.X,
                Y = b.Y,
                Text = b.Text,
                Argb = b.Argb,
                Alpha = b.Alpha,
                StackOffset = b.StackOffset,
            };
        }

        _glView.Renderer.SetSpeechBubbles(_bubbleScratch, bn);

        _audioPlayer ??= new AndroidAudioPlayer();
        _audioPlayer.SetContentRoot(s.ContentFilesRoot);
        _audioPlayer.SetFetcher(s.TextureFetcher);
        _audioPlayer.SetEar(s.CamX, s.CamY);
        _audioPlayer.Tick(world.Audio ?? Array.Empty<WorldAudioCue>());
        foreach (var (path, vol) in s.DrainChatAudio())
            _audioPlayer.PlayGlobalOneShot(path, vol);
    }

    static bool LooksLikeOtherGhost(WorldEntityDraw e)
    {
        var p = (e.RsiPath ?? "") + " " + (e.StateName ?? "") + " " + (e.Label ?? "");
        return p.Contains("Ghost", StringComparison.OrdinalIgnoreCase)
               || p.Contains("Observer", StringComparison.OrdinalIgnoreCase);
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
        _glView.Renderer.SetTextureFetcher(_connect.Session.TextureFetcher);
    }

    void LeaveServer()
    {
        _uiObserving = false;
        s_uiObserving = false;
        _flightX = 0;
        _flightY = 0;
        if (_immersiveApplied)
        {
            _immersiveApplied = false;
            ApplyObserveImmersive(false);
        }
        _connect.Disconnect();
        _glView?.Renderer.SetGhostMode(false);
        s_forceLandscape = false;
        ApplyOrientation(landscape: false);
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
                // Drag = free-flight pan (screen px → world px, Y flipped for station coords).
                var sens = 2.1f / Math.Max(0.35f, _connect.Session.Zoom);
                _connect.PanCamera(-(x - _lastTouchX) * sens, (y - _lastTouchY) * sens);
                _lastTouchX = x;
                _lastTouchY = y;
                _glView?.Renderer.SetCamera(_connect.Session.CamX, _connect.Session.CamY);
                _glView?.Renderer.SetZoom(_connect.Session.Zoom);
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

        // Landscape locked from the moment content loading starts (and lobby/observe).
        s_forceLandscape = true;
        ApplyOrientation(landscape: true);
        if (_loadingTitle != null)
            _loadingTitle.Text = "ПОДКЛЮЧЕНИЕ";
        if (_loadingServer != null)
            _loadingServer.Text = _selected.Name;
        if (_loadingStatus != null)
            _loadingStatus.Text = "Подготовка…";
        if (_loadingProgress != null)
            _loadingProgress.Progress = 0;
        if (_loadingPct != null)
            _loadingPct.Text = "0%";
        if (_screenLoading != null)
            _screenLoading.Visibility = ViewStates.Visible;
        if (_screenHome != null)
            _screenHome.Visibility = ViewStates.Gone;

        RenderStatus();
        try
        {
            await _connect.RunAsync(_connectCts.Token);
            if (_connect.InLobby || _connect.Observing)
            {
                ApplyOrientation(landscape: true);
                _authUiStatus = $"В сети: {_connect.Session.UserName}";
                if (_charName != null && string.IsNullOrWhiteSpace(_charName.Text))
                    _charName.Text = _connect.Session.UserName;
            }
            else
            {
                ApplyOrientation(landscape: false);
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
        // Orientation recreate must NOT tear down the live game session.
        if (!IsChangingConfigurations)
        {
            _audioPlayer?.Dispose();
            _audioPlayer = null;
            _connect.ProgressChanged -= OnProgressChanged;
            _connect.DebugChanged -= OnDebugChanged;
            _connect.Disconnect();
            s_connect = null;
            s_uiObserving = false;
            _uiObserving = false;
        }

        _host?.OnLifecycle(PlatformLifecycle.Destroyed);
        base.OnDestroy();
    }

    void RenderStatus(bool skipWorldPush = false)
    {
        // Sticky UI observe so a brief blip doesn't dump to hub — but exit if UDP died.
        if (_uiObserving && !_connect.Session.IsConnected)
        {
            _uiObserving = false;
            s_uiObserving = false;
            if (_authUiStatus.StartsWith("В сети", StringComparison.Ordinal)
                || string.IsNullOrWhiteSpace(_authUiStatus))
                _authUiStatus = string.IsNullOrWhiteSpace(_connect.Session.Detail)
                    ? "Соединение потеряно"
                    : $"Отключено: {_connect.Session.Detail}";
        }
        else if (_uiObserving && !_connect.Session.IsObserving && _connect.Session.IsConnected)
            _connect.Session.IsObserving = true;

        var observing = _uiObserving || _connect.Observing;
        var lobby = _connect.InLobby && !observing;
        var loading = _connect.Busy && !lobby && !observing;
        var fullscreenChrome = observing || lobby || loading;
        if (fullscreenChrome != _immersiveApplied)
        {
            _immersiveApplied = fullscreenChrome;
            ApplyObserveImmersive(fullscreenChrome);
        }

        if (_screenLoading != null)
            _screenLoading.Visibility = loading ? ViewStates.Visible : ViewStates.Gone;
        if (_screenHome != null)
            _screenHome.Visibility = lobby || observing || loading ? ViewStates.Gone : ViewStates.Visible;
        if (_screenLobby != null)
            _screenLobby.Visibility = lobby ? ViewStates.Visible : ViewStates.Gone;
        if (_screenObserve != null)
            _screenObserve.Visibility = observing ? ViewStates.Visible : ViewStates.Gone;

        s_uiObserving = _uiObserving;

        // Landscape while loading / lobby / observe. Portrait only on hub home.
        if (loading || _connect.Busy || lobby || observing)
            ApplyOrientation(landscape: true);
        else
            ApplyOrientation(landscape: false);

        if (loading)
        {
            if (_loadingServer != null && _selected is not null)
                _loadingServer.Text = _selected.Name;
            if (_loadingStatus != null)
                _loadingStatus.Text = string.IsNullOrWhiteSpace(_connect.Summary)
                    ? "Подключение…"
                    : _connect.Summary;
            if (_connect.LastProgress is { } lp)
                ApplyProgress(lp);
        }

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

        if (_lobbyWelcome != null)
            _lobbyWelcome.Visibility = ViewStates.Gone;

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
            _lobbyContentStatus.Visibility = ViewStates.Gone;
        }

        if (observing)
        {
            UpdateObserveOverlay();
            if (!skipWorldPush)
                PushWorldToGl();
        }

        if (_lobbyPlayers != null)
        {
            var chat = _connect.Session.ChatLines;
            if (_lobbyPlayerCount != null)
                _lobbyPlayerCount.Text = _connect.Session.Players.Count.ToString();
            if (chat.Count == 0)
            {
                _lobbyPlayers.Text = lobby ? "ожидание чата…" : "—";
                _lobbyPlayers.SetTextColor(Color.ParseColor("#F3F0E8"));
            }
            else
            {
                var take = chat.Count > 80 ? chat.Skip(chat.Count - 80).ToList() : chat;
                var sb = new Android.Text.SpannableStringBuilder();
                for (var i = 0; i < take.Count; i++)
                {
                    if (i > 0) sb.Append('\n');
                    var c = take[i];
                    var ch = c.Channel.Length > 8 ? c.Channel[..8] : c.Channel;
                    var line = $"[{ch}] {c.Text}";
                    var start = sb.Length();
                    sb.Append(line);
                    var color = new Color(c.Argb);
                    sb.SetSpan(
                        new Android.Text.Style.ForegroundColorSpan(color),
                        start,
                        sb.Length(),
                        Android.Text.SpanTypes.ExclusiveExclusive);
                }

                _lobbyPlayers.SetText(sb, TextView.BufferType.Spannable);
            }
        }

        if (_lobbyDetail != null)
            _lobbyDetail.Text = _connect.Session.Detail;

        if (_joinDebug != null)
            _joinDebug.Text = string.IsNullOrWhiteSpace(_connect.DebugLog) ? _connect.Summary : _connect.DebugLog;

        if (_connect.LastProgress is { } prog && _connect.Busy)
            ApplyProgress(prog);

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

        if (_loadingProgress != null)
            _loadingProgress.Progress = p.Percent;
        if (_loadingPct != null)
        {
            var mb = p.BytesWritten > 0 ? $" · {p.BytesWritten / (1024.0 * 1024.0):0.0} MB" : "";
            _loadingPct.Text = $"{p.Percent}%  ·  {p.Stage}  {p.Done}/{p.Total}{mb}";
        }

        if (_loadingStatus != null)
            _loadingStatus.Text = _connect.Summary;
        if (_loadingTitle != null)
        {
            _loadingTitle.Text = p.Stage switch
            {
                "textures" => "ТЕКСТУРЫ",
                "prototypes" => "ПРОТОТИПЫ",
                "assemblies" => "СБОРКИ",
                "manifest" or "index" => "МАНИФЕСТ",
                "done" => "ГОТОВО",
                _ => "ПОДКЛЮЧЕНИЕ",
            };
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
