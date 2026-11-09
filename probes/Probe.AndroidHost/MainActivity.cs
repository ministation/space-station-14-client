using Android.Content;
using Android.Content.PM;
using Android.Graphics;
using Android.Util;
using Android.Views;
using Android.Widget;
using Port.Client;
using Port.Client.Bootstrap;
using Port.Client.Content;
using Port.Client.Rendering;
using Port.Client.Ui;
using Port.Content;
using Port.Net;
using Port.Platform.Android;
using Port.Platform.Android.Audio;
using Port.Platform.Android.Graphics;
using Port.Platform.Android.Input;
using Port.Platform.Android.Ui;
using System.Timers;
using System.Xml.Linq;
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
    TextView? _loadingSpeed;
    TextView? _loadingMessage;
    ProgressBar? _loadingProgress;
    Button? _loadingCancelBtn;
    FrameLayout? _lobbyParallaxHost;
    TextView? _lobbyWelcome;
    EditText? _authUsername;
    EditText? _authPassword;
    EditText? _authTfa;
    EditText? _charName;
    EditText? _customServer;
    EditText? _hubSearch;
    LinearLayout? _serverList;
    LinearLayout? _favoriteList;
    View? _homeEmpty;
    LinearLayout? _newsList;
    TextView? _newsStatus;
    TextView? _hubStatus;
    ProgressBar? _hubSpinner;
    TextView? _versionText;
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
    GlesClydeBackend? _clyde;
    ClydeRenderSystem? _renderSystem;
    AndroidUiHost? _uiHost;
    ContentClientLoadSystem? _contentClient;
    ContentClientGameplaySystem? _contentGameplay;
    ContentEntryPointSystem? _contentEntryPoint;
    ContentClientSystemHost? _contentSystems;
    AndroidInputBridge? _inputBridge;
    readonly AndroidUiBinder _uiBinder = new();
    AndroidAudioPlayer? _audioPlayer;

    AndroidPlatformHost? _host;
    ClientLoop? _clientLoop;
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
    string _authUiStatus = "Не выполнен вход";
    string _hubSearchQuery = "";
    bool _loginBusy;
    bool _hubBusy;
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

    // ---- Launcher chrome (SS14.Launcher style) ----
    const int TabHome = 0;
    const int TabServers = 1;
    const int TabNews = 2;
    const int TabOptions = 3;
    const int TabLogin = 4; // MainWindowLogin replaces the tab content when logged out.
    int _tabIndex = TabHome;
    int _lastLauncherTab = TabHome;
    bool _skipLogin;
    Button? _connectBtn; // legacy hidden; per-row Connect is the real entry point.
    TextView? _loginError;
    CheckBox? _authShowPass;
    long _speedLastBytes;
    long _speedLastMs;
    double _speedEma;
    bool _newsLoaded;
    bool _newsBusy;
    bool _verboseLog;
    Android.Content.ISharedPreferences _prefs = null!;

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
        // Full-client foundation: YAML+meta sprites. Content.Client type-load is heavy on Android
        // and not required for observe sprites (WorldStateCache + PrototypeSpriteIndex).
        ClientFeatureFlags.AuthoritativeSprites = true;
        ClientFeatureFlags.StrictRsiStates = true;
        ClientFeatureFlags.LoadContentClientAssemblies = false;
        ClientFeatureFlags.ReflectContentClientInSerializer = false;
        // Never auto-run Content.Client EntryPoint/System ctors — they hard-crash observe on device.
        ClientFeatureFlags.RunContentEntryPointBootstrap = false;
        ClientFeatureFlags.RunContentSystemHost = false;
        _renderSystem = new ClydeRenderSystem();
        var boot = ClientBootstrap.CreateDefaultLoop(render: _renderSystem);
        _clientLoop = boot.Loop;
        _uiHost = boot.Ui;
        _contentClient = boot.ContentClient;
        _contentGameplay = boot.Gameplay;
        _contentEntryPoint = boot.EntryPoint;
        _contentSystems = boot.Systems;
        _contentClient.Log = msg => DiagLog.Info(msg);
        _contentGameplay.Log = msg => DiagLog.Info(msg);
        _contentEntryPoint.Log = msg => DiagLog.Info(msg);
        _contentSystems.Log = msg => DiagLog.Info(msg);
        _contentClient.AssembliesDirectorySource = () => _connect.Session.AssembliesDirectory;
        _inputBridge = new AndroidInputBridge(_uiHost.Input);
        ContentAssemblyHost.EnsureAssemblyResolveHook();
        _clientLoop.Start();
        // Landscape locked from loading onward. Portrait only on launcher pages.
        if (s_forceLandscape || s_connect?.Busy == true || s_connect?.InLobby == true
            || s_connect?.Observing == true || s_uiObserving)
            RequestedOrientation = ScreenOrientation.Landscape;
        else
            RequestedOrientation = ScreenOrientation.Portrait;
        SetContentView(Resource.Layout.activity_main);
        ApplySafeAreaInsets();

        _prefs = GetSharedPreferences("launcher_prefs", FileCreationMode.Private)!;
        _skipLogin = _prefs.GetBoolean("skip_login", false);
        _verboseLog = _prefs.GetBoolean("verbose_log", false);
        _lastLauncherTab = Math.Clamp(_prefs.GetInt("last_tab", TabHome), TabHome, TabOptions);

        var paths = AndroidContentPaths.FromContext(this);
        _host = new AndroidPlatformHost(paths);
        _host.EnsureDirectories();
        _host.OnLifecycle(PlatformLifecycle.Created);
        _connect = s_connect ??= new ConnectSession();
        _uiObserving = s_uiObserving;
        _connect.AuthConfigPath = System.IO.Path.Combine(paths.FilesDir, "auth-session.json");
        _connect.ContentRoot = paths.ContentDir;
        _connect.Session.OnContentReady = root =>
        {
            var dir = _connect.Session.AssembliesDirectory;
            if (!string.IsNullOrWhiteSpace(dir))
                _contentClient?.TryLoad(dir);
        };
        ClientHwid.StorageDirectory = paths.UserDataDir;
        _hub = new HubServerCatalog(System.IO.Path.Combine(paths.FilesDir, "hub-favorites.json"));
        _connect.ProgressChanged -= OnProgressChanged;
        _connect.ProgressChanged += OnProgressChanged;
        _connect.DebugChanged -= OnDebugChanged;
        _connect.DebugChanged += OnDebugChanged;

        BindViews();
        WireButtons();
        LauncherTheme.StyleButtons(FindViewById(Resource.Id.root)!);
        RebuildServerList();
        RebuildFavoriteList();

        var existing = AuthSessionConfig.TryLoad(_connect.AuthConfigPath);
        if (existing?.HasRequiredFields == true)
        {
            _authUiStatus = existing.StatusLine();
            if (_charName != null && string.IsNullOrWhiteSpace(_charName.Text))
                _charName.Text = existing.UserName;
        }

        SetVersionText();

        _uiTimer = new Timer(50);
        _uiTimer.Elapsed += (_, _) =>
        {
            try
            {
                _host?.Clock.Pulse();
                _clientLoop?.FrameUpdate(0.05f);
                var observing = _uiObserving || _connect.Observing;
                if (observing)
                {
                    _connect.Session.SetFlightInput(_flightX, _flightY);
                    _connect.Session.TickFlight(0.05f);
                }

                RunOnUiThread(() =>
                {
                    try
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
                            _uiTick++;
                            RenderStatus();
                            // Rotate ConnectingOverlay random message every ~8 s.
                            if (_uiTick % 160 == 0 && _screenLoading?.Visibility == ViewStates.Visible)
                                _ = UpdateLoadingMessageAsync();
                        }
                    }
                    catch (Exception ex)
                    {
                        DiagLog.Error($"ui tick FAIL {ex.GetType().Name}: {ex.Message}");
                    }
                });
            }
            catch (Exception ex)
            {
                DiagLog.Error($"timer FAIL {ex.GetType().Name}: {ex.Message}");
            }
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

    public override bool DispatchKeyEvent(KeyEvent? e)
    {
        if (e is not null && (_uiObserving || _connect.Observing))
        {
            var down = e.Action == KeyEventActions.Down;
            if (e.Action is KeyEventActions.Down or KeyEventActions.Up)
                _inputBridge?.HandleKey(e.KeyCode, down);
        }

        return base.DispatchKeyEvent(e);
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
        Window.SetNavigationBarColor(LauncherTheme.StripeA);

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
                Window.SetNavigationBarColor(LauncherTheme.StripeA);
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
        _loadingSpeed = FindViewById<TextView>(Resource.Id.loading_speed);
        _loadingMessage = FindViewById<TextView>(Resource.Id.loading_message);
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
        // Chat history stays on Android Spannable (colors); Robust hud holds plain text.
        _uiBinder.BindViews(
            _observeHud,
            _observeFps,
            chatHistory: null,
            FindViewById<EditText>(Resource.Id.observe_chat_input),
            FindViewById<Button>(Resource.Id.btn_observe_chat_channel),
            _observeDiag);
        _observeDiagScroll = FindViewById(Resource.Id.observe_diag_scroll);
        _observeCopyBtn = FindViewById<Button>(Resource.Id.btn_observe_copy);
        _joinDebug = FindViewById<TextView>(Resource.Id.join_debug);
        _copyLogBtn = FindViewById<Button>(Resource.Id.btn_copy_log);
        _downloadLabel = FindViewById<TextView>(Resource.Id.download_label);
        _downloadPct = FindViewById<TextView>(Resource.Id.download_pct);
        _downloadProgress = FindViewById<ProgressBar>(Resource.Id.download_progress);
        _authUsername = FindViewById<EditText>(Resource.Id.auth_username);
        _authPassword = FindViewById<EditText>(Resource.Id.auth_password);
        _authTfa = FindViewById<EditText>(Resource.Id.auth_tfa);
        _authShowPass = FindViewById<CheckBox>(Resource.Id.auth_show_pass);
        _loginError = FindViewById<TextView>(Resource.Id.login_error);
        _hubStatus = FindViewById<TextView>(Resource.Id.hub_status);
        _hubSpinner = FindViewById<ProgressBar>(Resource.Id.hub_spinner);
        _hubSearch = FindViewById<EditText>(Resource.Id.hub_search);
        _serverList = FindViewById<LinearLayout>(Resource.Id.server_list);
        _favoriteList = FindViewById<LinearLayout>(Resource.Id.favorite_list);
        _homeEmpty = FindViewById(Resource.Id.home_empty);
        _newsList = FindViewById<LinearLayout>(Resource.Id.news_list);
        _newsStatus = FindViewById<TextView>(Resource.Id.news_status);
        _versionText = FindViewById<TextView>(Resource.Id.version_text);
        _charName = FindViewById<EditText>(Resource.Id.char_name);
        _customServer = FindViewById<EditText>(Resource.Id.custom_server);
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
    }

    void WireButtons()
    {
        // ---- Launcher tabs ----
        WireTab(Resource.Id.tab_home_btn, TabHome);
        WireTab(Resource.Id.tab_servers_btn, TabServers);
        WireTab(Resource.Id.tab_news_btn, TabNews);
        WireTab(Resource.Id.tab_options_btn, TabOptions);

        FindViewById<Button>(Resource.Id.btn_discord)?.Click += (_, _) =>
            LauncherTheme.OpenUrl(this, LauncherTheme.DiscordUrl);
        FindViewById<Button>(Resource.Id.btn_website)?.Click += (_, _) =>
            LauncherTheme.OpenUrl(this, LauncherTheme.WebsiteUrl);
        FindViewById<Button>(Resource.Id.btn_header_login)?.Click += (_, _) => SelectTab(TabLogin);
        FindViewById<Button>(Resource.Id.btn_go_servers)?.Click += (_, _) => SelectTab(TabServers);
        FindViewById<Button>(Resource.Id.btn_refresh_home)?.Click += async (_, _) => await RefreshHubAsync();
        FindViewById<Button>(Resource.Id.btn_add_fav)?.Click += async (_, _) => await AddFavoriteAsync();
        FindViewById<Button>(Resource.Id.btn_direct_connect)?.Click += async (_, _) => await DirectConnectAsync();

        // ---- Login page ----
        FindViewById<Button>(Resource.Id.btn_forgot)?.Click += (_, _) =>
            LauncherTheme.OpenUrl(this, LauncherTheme.AccountForgotPasswordUrl);
        FindViewById<Button>(Resource.Id.btn_login_resend)?.Click += (_, _) =>
            LauncherTheme.OpenUrl(this, LauncherTheme.AccountResendConfirmationUrl);
        FindViewById<Button>(Resource.Id.btn_login_register)?.Click += (_, _) =>
            LauncherTheme.OpenUrl(this, LauncherTheme.AccountRegisterUrl);
        FindViewById<Button>(Resource.Id.btn_login_skip)?.Click += (_, _) =>
        {
            _skipLogin = true;
            _prefs.Edit()?.PutBoolean("skip_login", true)?.Apply();
            RenderStatus();
        };
        if (_authShowPass != null && _authPassword != null)
            _authShowPass.CheckedChange += (_, e) =>
                _authPassword.InputType = e.IsChecked
                    ? Android.Text.InputTypes.ClassText | Android.Text.InputTypes.TextVariationVisiblePassword
                    : Android.Text.InputTypes.ClassText | Android.Text.InputTypes.TextVariationPassword;

        // ---- Options page ----
        FindViewById<Button>(Resource.Id.btn_options_account)?.Click += (_, _) =>
            LauncherTheme.OpenUrl(this, LauncherTheme.AccountManageUrl);
        FindViewById<Button>(Resource.Id.btn_open_logs)?.Click += (_, _) =>
        {
            if (_joinDebug == null || _copyLogBtn == null)
                return;
            var open = _joinDebug.Visibility != ViewStates.Visible;
            _joinDebug.Visibility = open ? ViewStates.Visible : ViewStates.Gone;
            _copyLogBtn.Visibility = open ? ViewStates.Visible : ViewStates.Gone;
        };
        FindViewById<Button>(Resource.Id.btn_clear_content)?.Click += (_, _) =>
            ConfirmAndClear("Очистить загруженный контент серверов?", () =>
            {
                TryDeleteDir(_connect.ContentRoot);
                Toast.MakeText(this, "Контент очищен", ToastLength.Short)?.Show();
            });
        FindViewById<Button>(Resource.Id.btn_clear_engines)?.Click += (_, _) =>
            ConfirmAndClear("Очистить скачанные сборки движка?", () =>
            {
                TryDeleteDir(_connect.Session.AssembliesDirectory);
                Toast.MakeText(this, "Сборки очищены", ToastLength.Short)?.Show();
            });
        var verboseBox = FindViewById<CheckBox>(Resource.Id.opt_verbose_log);
        if (verboseBox != null)
        {
            verboseBox.Checked = _verboseLog;
            verboseBox.CheckedChange += (_, e) =>
            {
                _verboseLog = e.IsChecked;
                _prefs.Edit()?.PutBoolean("verbose_log", e.IsChecked)?.Apply();
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
            _refreshHubBtn.Click += async (_, e) => await RefreshHubAsync();
        }

        if (_hubSearch != null)
        {
            _hubSearch.TextChanged += (_, _) =>
            {
                _hubSearchQuery = _hubSearch.Text?.Trim() ?? "";
                RebuildServerList();
                RebuildFavoriteList();
            };
        }

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

                try
                {
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
                }
                catch (Exception ex)
                {
                    DiagLog.Error($"observe enter FAIL {ex.GetType().Name}: {ex.Message}");
                    Toast.MakeText(this, $"Observe crash: {ex.GetType().Name}", ToastLength.Long)?.Show();
                }
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

    void WireTab(int id, int index)
    {
        var v = FindViewById<View>(id);
        if (v == null)
            return;
        v.Click += (_, _) => SelectTab(index);
    }

    void ConfirmAndClear(string message, Action action)
    {
        new AlertDialog.Builder(this)
            .SetTitle("Подтверждение")
            .SetMessage(message)
            .SetPositiveButton("Очистить", (s, e) => action())
            .SetNegativeButton("Отмена", (s, e) => { })
            .Show();
    }

    static void TryDeleteDir(string? path)
    {
        if (string.IsNullOrWhiteSpace(path) || !System.IO.Directory.Exists(path))
            return;
        try
        {
            System.IO.Directory.Delete(path, recursive: true);
        }
        catch (Exception ex)
        {
            DiagLog.Error($"clear dir FAIL {path}: {ex.Message}");
        }
    }

    // ---- Launcher tab control ----

    void SelectTab(int index)
    {
        if (index != TabLogin)
            _lastLauncherTab = index;
        var changed = _tabIndex != index;
        _tabIndex = index;

        SetPage(Resource.Id.page_home, TabHome);
        SetPage(Resource.Id.page_servers, TabServers);
        SetPage(Resource.Id.page_news, TabNews);
        SetPage(Resource.Id.page_options, TabOptions);
        SetPage(Resource.Id.page_login, TabLogin);

        FindViewById(Resource.Id.tab_bar)!.Visibility =
            index == TabLogin ? ViewStates.Gone : ViewStates.Visible;
        MarkTab(Resource.Id.tab_home_btn, index == TabHome);
        MarkTab(Resource.Id.tab_servers_btn, index == TabServers);
        MarkTab(Resource.Id.tab_news_btn, index == TabNews);
        MarkTab(Resource.Id.tab_options_btn, index == TabOptions);

        if (!changed)
            return;

        if (index == TabServers)
            RebuildServerList();
        if (index == TabHome)
            RebuildFavoriteList();
        if (index == TabNews && !_newsLoaded)
            _ = RefreshNewsAsync();
        if (index != TabLogin)
            _prefs.Edit()?.PutInt("last_tab", index)?.Apply();
    }

    void SetPage(int id, int index)
    {
        var v = FindViewById<View>(id);
        if (v == null)
            return;
        v.Visibility = _tabIndex == index ? ViewStates.Visible : ViewStates.Gone;
    }

    void MarkTab(int id, bool selected)
    {
        var v = FindViewById<View>(id);
        if (v != null)
            v.Selected = selected;
    }

    // ---- Server table (ServerEntryView style: name | round | players | connect) ----

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
        if (_serverList is null || _hub is null)
            return;

        _serverList.RemoveAllViews();
        var servers = FilterServers(_hub.All);
        foreach (var server in servers)
            BuildServerRow(_serverList, server, _serverList.ChildCount);
    }

    void RebuildFavoriteList()
    {
        if (_favoriteList is null || _hub is null || _homeEmpty is null)
            return;

        _favoriteList.RemoveAllViews();
        var favorites = FilterServers(_hub.All).Where(s => s.Favorite).ToList();
        _homeEmpty.Visibility = favorites.Count == 0 ? ViewStates.Visible : ViewStates.Gone;
        foreach (var server in favorites)
            BuildServerRow(_favoriteList, server, _favoriteList.ChildCount);
    }

    void BuildServerRow(LinearLayout container, HubServerEntry server, int index)
    {
        var d = Resources!.DisplayMetrics!;
        float Dp(float v) => v * d.Density;

        var expanded = _expandedServers.Contains(server.Id);

        var row = new LinearLayout(this)
        {
            Orientation = Orientation.Vertical,
            Clickable = true,
            Focusable = true,
            ContentDescription = $"Сервер {server.Name}. {server.SummaryLine}",
        };
        // Striped list: even rows #262626 over the #1e1e22 panel.
        row.SetBackgroundColor(index % 2 == 1 ? LauncherTheme.StripeB : LauncherTheme.StripeA);
        row.LayoutParameters = new LinearLayout.LayoutParams(
            ViewGroup.LayoutParams.MatchParent,
            ViewGroup.LayoutParams.WrapContent);

        var head = new LinearLayout(this) { Orientation = Orientation.Horizontal };
        head.SetGravity(GravityFlags.CenterVertical);
        head.SetPadding((int)Dp(8), (int)Dp(8), (int)Dp(4), (int)Dp(8));

        // Name cell.
        var nameCell = new LinearLayout(this)
        {
            Orientation = Orientation.Vertical,
            LayoutParameters = new LinearLayout.LayoutParams(0, ViewGroup.LayoutParams.WrapContent, 1f),
        };
        var name = new TextView(this)
        {
            Text = server.Name,
            TextSize = 14,
            MaxLines = 1,
            Ellipsize = Android.Text.TextUtils.TruncateAt.End,
        };
        name.SetTextColor(LauncherTheme.Fg);
        var subLine = server.Online
            ? string.IsNullOrWhiteSpace(server.Map) && string.IsNullOrWhiteSpace(server.Preset)
                ? "онлайн"
                : string.Join(" · ", new[] { server.Map, server.Preset }.Where(s => !string.IsNullOrWhiteSpace(s)))
            : "оффлайн";
        var sub = new TextView(this)
        {
            Text = subLine,
            TextSize = 11,
            MaxLines = 1,
            Ellipsize = Android.Text.TextUtils.TruncateAt.End,
        };
        sub.SetTextColor(LauncherTheme.SubText);
        nameCell.AddView(name);
        nameCell.AddView(sub);
        head.AddView(nameCell);
        head.AddView(CellDivider(Dp));

        // Round cell.
        var round = new TextView(this)
        {
            Text = server.RoundId > 0 ? $"#{server.RoundId}" : "—",
            TextSize = 12,
            Gravity = GravityFlags.Center,
        };
        round.LayoutParameters = new LinearLayout.LayoutParams(
            (int)Dp(72), ViewGroup.LayoutParams.WrapContent);
        round.SetTextColor(LauncherTheme.Fg);
        head.AddView(round);
        head.AddView(CellDivider(Dp));

        // Players cell (mono, right — GridCell.Numeric).
        var players = new TextView(this)
        {
            Text = server.PlayersLabel,
            TextSize = 12,
            Gravity = GravityFlags.Center,
            Typeface = Typeface.MonoSpace,
        };
        players.LayoutParameters = new LinearLayout.LayoutParams(
            (int)Dp(64), ViewGroup.LayoutParams.WrapContent);
        players.SetTextColor(server.Online ? LauncherTheme.Fg : LauncherTheme.SubText);
        head.AddView(players);
        head.AddView(CellDivider(Dp));

        // Connect cell.
        var connect = new Button(this)
        {
            Text = GetString(Resource.String.server_entry_connect),
            TextSize = 12,
            Enabled = server.Online,
        };
        connect.LayoutParameters = new LinearLayout.LayoutParams(
            (int)Dp(88), (int)Dp(32));
        LauncherTheme.StyleAngleButton(connect);
        var connectTarget = server;
        connect.Click += (_, _) =>
        {
            SelectServer(connectTarget);
            _ = RunConnectAsync();
        };
        head.AddView(connect);

        row.AddView(head);

        if (expanded)
        {
            var descText = _descCache.TryGetValue(server.Id, out var cached)
                ? cached
                : (server.Description ?? "Загрузка описания…");
            var body = new LinearLayout(this) { Orientation = Orientation.Vertical };
            body.SetPadding((int)Dp(12), 0, (int)Dp(12), (int)Dp(10));

            var desc = new TextView(this)
            {
                Text = descText,
                TextSize = 12,
            };
            desc.SetTextColor(LauncherTheme.Fg);
            body.AddView(desc);

            var tags = server.Tags is { Count: > 0 }
                ? "Теги: " + string.Join(", ", server.Tags.Take(12))
                : "";
            if (!string.IsNullOrEmpty(tags))
            {
                var tagsView = new TextView(this)
                {
                    Text = tags,
                    TextSize = 11,
                };
                tagsView.SetTextColor(LauncherTheme.SubText);
                body.AddView(tagsView);
            }

            var fetched = new TextView(this)
            {
                Text = $"Адрес: {server.ConnectUri} · hub.spacestation14.com",
                TextSize = 10,
            };
            fetched.SetTextColor(LauncherTheme.SubText);
            body.AddView(fetched);

            // Favorite toggle (star) — as in the launcher's expanded entry.
            var favBtn = new Button(this)
            {
                Text = server.Favorite ? "★ В избранном" : "☆ В избранное",
                TextSize = 11,
                Gravity = GravityFlags.Center,
            };
            favBtn.LayoutParameters = new LinearLayout.LayoutParams(
                ViewGroup.LayoutParams.WrapContent, (int)Dp(32));
            LauncherTheme.StyleAngleButton(favBtn);
            var favTarget = server;
            favBtn.Click += (_, _) =>
            {
                _hub?.ToggleFavorite(favTarget.Id);
                RebuildServerList();
                RebuildFavoriteList();
            };
            body.AddView(favBtn, new LinearLayout.LayoutParams(
                ViewGroup.LayoutParams.WrapContent, (int)Dp(32))
            { TopMargin = (int)Dp(6) });

            row.AddView(body);
        }

        var captured = server;
        row.Click += (_, _) =>
        {
            if (!_expandedServers.Add(captured.Id))
                _expandedServers.Remove(captured.Id);
            RebuildServerList();
            RebuildFavoriteList();
            if (_expandedServers.Contains(captured.Id))
                _ = LoadDescriptionAsync(captured);
        };

        container.AddView(row);
    }

    View CellDivider(Func<float, float> dp)
    {
        var sep = new View(this);
        sep.LayoutParameters = new LinearLayout.LayoutParams(
            (int)dp(2), ViewGroup.LayoutParams.MatchParent)
        { LeftMargin = (int)dp(8), RightMargin = (int)dp(8) };
        sep.SetBackgroundColor(LauncherTheme.ListSep);
        return sep;
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

        RunOnUiThread(() =>
        {
            RebuildServerList();
            RebuildFavoriteList();
        });
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
        if (_hubSpinner != null)
            _hubSpinner.Visibility = ViewStates.Visible;
        if (_refreshHubBtn != null)
            _refreshHubBtn.Enabled = false;
        try
        {
            await _hub.RefreshFromHubAsync(_hubCts.Token);
            RunOnUiThread(() =>
            {
                if (_hubStatus != null)
                    _hubStatus.Text = $"Серверов: {_hub.All.Count} · {_hub.LastRefreshUtc:HH:mm:ss} UTC";
                RebuildServerList();
                RebuildFavoriteList();
            });
        }
        catch (Exception ex)
        {
            RunOnUiThread(() =>
            {
                if (_hubStatus != null)
                    _hubStatus.Text = $"Хаб недоступен: {ex.Message}. Показаны избранные/локальные.";
                RebuildServerList();
                RebuildFavoriteList();
            });
        }
        finally
        {
            _hubBusy = false;
            RunOnUiThread(() =>
            {
                if (_refreshHubBtn != null)
                    _refreshHubBtn.Enabled = true;
                if (_hubSpinner != null)
                    _hubSpinner.Visibility = ViewStates.Gone;
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

    async Task AddFavoriteAsync()
    {
        var result = await LauncherTheme.ShowAddressDialogAsync(this, "Добавить в избранное", withName: true);
        if (result is null || _hub is null)
            return;
        var entry = HubServerEntry.TryParse(result.Value.Address, result.Value.Name);
        if (entry is null)
        {
            Toast.MakeText(this, "Адрес: ss14://host:port", ToastLength.Short)?.Show();
            return;
        }

        _hub.AddCustom(entry);
        RebuildServerList();
        RebuildFavoriteList();
        Toast.MakeText(this, $"Добавлен {entry.Name}", ToastLength.Short)?.Show();
    }

    async Task DirectConnectAsync()
    {
        var result = await LauncherTheme.ShowAddressDialogAsync(this, "Прямое подключение", withName: false);
        if (result is null)
            return;
        var entry = HubServerEntry.TryParse(result.Value.Address);
        if (entry is null)
        {
            Toast.MakeText(this, "Адрес: ss14://host:port", ToastLength.Short)?.Show();
            return;
        }

        SelectServer(entry);
        _descCache[entry.Id] = "Прямое подключение";
        await RunConnectAsync();
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

    // ---- News tab (NewsTabView: headline buttons from the RSS feed) ----

    async Task RefreshNewsAsync()
    {
        if (_newsList is null || _newsStatus is null || _newsBusy)
            return;
        _newsBusy = true;
        _newsStatus.Visibility = ViewStates.Visible;
        _newsStatus.Text = GetString(Resource.String.news_pulling);
        try
        {
            var xml = await LauncherTheme.Http.GetStringAsync(LauncherTheme.NewsFeedUrl);
            var doc = XDocument.Parse(xml);
            var items = doc.Descendants()
                .Where(e => e.Name.LocalName == "item")
                .Take(15)
                .Select(i => (
                    Title: (string?)i.Elements().FirstOrDefault(e => e.Name.LocalName == "title"),
                    Link: (string?)i.Elements().FirstOrDefault(e => e.Name.LocalName == "link"),
                    Date: (string?)i.Elements().FirstOrDefault(e => e.Name.LocalName == "pubDate")))
                .Where(x => !string.IsNullOrWhiteSpace(x.Title))
                .ToList();

            RunOnUiThread(() =>
            {
                _newsList.RemoveAllViews();
                var d = Resources!.DisplayMetrics!;
                if (items.Count == 0)
                {
                    _newsStatus.Visibility = ViewStates.Visible;
                    _newsStatus.Text = "Новостей пока нет";
                    return;
                }

                _newsStatus.Visibility = ViewStates.Gone;
                foreach (var item in items)
                {
                    var btn = new Button(this)
                    {
                        Text = item.Title,
                        TextSize = 13,
                        Gravity = GravityFlags.Start | GravityFlags.CenterVertical,
                    };
                    btn.LayoutParameters = new LinearLayout.LayoutParams(
                        ViewGroup.LayoutParams.MatchParent, ViewGroup.LayoutParams.WrapContent)
                    { BottomMargin = (int)(4 * d.Density) };
                    LauncherTheme.StyleAngleButton(btn);
                    var link = item.Link ?? LauncherTheme.WebsiteUrl;
                    btn.Click += (_, _) => LauncherTheme.OpenUrl(this, link);
                    _newsList.AddView(btn);
                }

                _newsLoaded = true;
            });
        }
        catch (Exception)
        {
            RunOnUiThread(() =>
            {
                _newsStatus.Visibility = ViewStates.Visible;
                _newsStatus.Text = "Новости недоступны — spacestation14.com";
            });
        }
        finally
        {
            _newsBusy = false;
        }
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
        // Robust ObserveHud is source of truth; XML TextViews are bound adapters.
        var hud = _uiHost?.ObserveHud;
        if (hud is not null)
        {
            var s = _connect.Session;
            hud.SetStatus($"{s.UserName ?? "ghost"} · z{s.Zoom:0.0}");
            var fps = _clyde?.Fps ?? _glView?.Renderer.Fps ?? 0f;
            hud.SetFps($"{fps:0} FPS");
            hud.ChatChannelIndex = _chatChannelIdx;
            hud.ChatExpanded = _chatExpanded;
            hud.ChatChannelButton.Text = ChatChannelLabels[Math.Clamp(_chatChannelIdx, 0, ChatChannelLabels.Length - 1)];
        }

        if (_observeDiagOpen)
            RefreshObserveDiag(force: false);
        RefreshObserveChatHistory();
        if (hud is not null)
            _uiBinder.PushFromHud(hud);
    }

    void RefreshObserveDiag(bool force)
    {
        if (!_observeDiagOpen)
            return;
        _diagUiTick++;
        if (!force && _diagUiTick % 4 != 0)
            return;
        var gles = _glView?.Renderer.FormatDiag() ?? "(no gles)";
        var world = _connect.Session.LastWorld;
        var text =
            $"{gles}\n\n" +
            $"world ents={world?.Entities.Count ?? 0} tiles={world?.Tiles?.Count ?? 0}\n" +
            $"hint={_connect.Session.LastEyeHint}\n" +
            $"detail={world?.Detail}\n\n" +
            DiagLog.Format(40);
        _uiHost?.ObserveHud.SetDiag(text, visible: true);
        if (_observeDiag != null)
            _observeDiag.Text = text;
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
        var ver = _connect.Session.ChatVersion;
        if (ver == _lastChatVersion)
            return;
        _lastChatVersion = ver;

        var chat = _connect.Session.ChatLines;
        if (chat.Count == 0)
        {
            _uiHost?.ObserveHud.SetChatHistory("чат…");
            if (history != null)
                history.Text = "чат…";
            return;
        }

        // Last ~40 lines — enough for PC-like panel without Spannable thrash.
        var start = Math.Max(0, chat.Count - 40);
        var plain = new System.Text.StringBuilder();
        var sb = new Android.Text.SpannableStringBuilder();
        for (var i = start; i < chat.Count; i++)
        {
            if (sb.Length() > 0)
            {
                sb.Append('\n');
                plain.Append('\n');
            }
            var c = chat[i];
            var lineStart = sb.Length();
            sb.Append(c.Text);
            plain.Append(c.Text);
            sb.SetSpan(
                new Android.Text.Style.ForegroundColorSpan(new Color(c.Argb)),
                lineStart,
                sb.Length(),
                Android.Text.SpanTypes.ExclusiveExclusive);
        }

        _uiHost?.ObserveHud.SetChatHistory(plain.ToString());
        if (history != null)
            history.SetText(sb, TextView.BufferType.Spannable);
        var scroll = FindViewById<ScrollView>(Resource.Id.observe_chat_scroll);
        scroll?.Post(() => scroll.FullScroll(FocusSearchDirection.Down));
    }

    void PushWorldToGl(bool forceEntities = true)
    {
        if (_glView is null)
            return;
        EnsureGl();
        var s = _connect.Session;
        var clyde = _clyde ?? new GlesClydeBackend(_glView.Renderer);
        _clyde ??= clyde;
        clyde.SetContentRoot(s.ContentFilesRoot);
        clyde.SetTextureFetcher(s.TextureFetcher);
        // Camera is driven by ClydeRenderSystem each tick; keep a direct sync for camera-only frames.
        clyde.Camera = new System.Numerics.Vector2(s.CamX, s.CamY);
        clyde.CameraRotation = s.CamRotation;
        clyde.Zoom = s.Zoom;
        clyde.SetFullbright(true);
        _glView.Renderer.SetDrawFov(false);
        var world = s.LastWorld;
        if (world is null)
        {
            clyde.SetEntities(Array.Empty<GlesClearRenderer.EntitySprite>(), 0);
            clyde.SetTiles(Array.Empty<GlesClearRenderer.TileSprite>(), 0);
            clyde.SetSpeechBubbles(Array.Empty<GlesClearRenderer.SpeechBubbleSprite>(), 0);
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

        clyde.SetEntities(_spriteScratch, n);

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

        clyde.SetTiles(_tileScratch, tn);

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

        clyde.SetSpeechBubbles(_bubbleScratch, bn);

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
        _clyde = new GlesClydeBackend(_glView.Renderer);
        _clyde.SetGhostMode(true);
        _clyde.SetContentRoot(_connect.Session.ContentFilesRoot);
        _clyde.SetTextureFetcher(_connect.Session.TextureFetcher);
        _clyde.SetFullbright(true);
        if (_renderSystem is not null)
        {
            _renderSystem.AttachBackend(_clyde, _clyde);
            _renderSystem.CameraSource = () =>
            {
                var s = _connect.Session;
                return (s.CamX, s.CamY, s.CamRotation, s.Zoom);
            };
        }
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
        _inputBridge?.HandleMotion(ev);
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
                if (_clyde is not null)
                {
                    _clyde.Camera = new System.Numerics.Vector2(_connect.Session.CamX, _connect.Session.CamY);
                    _clyde.Zoom = _connect.Session.Zoom;
                }
                else
                {
                    _glView?.Renderer.SetCamera(_connect.Session.CamX, _connect.Session.CamY);
                    _glView?.Renderer.SetZoom(_connect.Session.Zoom);
                }
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
        SetLoginError(null);

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
                SetLoginError(_authUiStatus);
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
            SetLoginError(_authUiStatus);
        }
        finally
        {
            _loginBusy = false;
            if (_loginBtn != null) _loginBtn.Enabled = true;
            RenderStatus();
        }
    }

    void SetLoginError(string? message)
    {
        if (_loginError is null)
            return;
        if (string.IsNullOrEmpty(message))
        {
            _loginError.Visibility = ViewStates.Gone;
        }
        else
        {
            _loginError.Text = message;
            _loginError.Visibility = ViewStates.Visible;
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
        _speedLastBytes = 0;
        _speedLastMs = 0;
        _speedEma = 0;

        // Landscape locked from the moment content loading starts (and lobby/observe).
        s_forceLandscape = true;
        ApplyOrientation(landscape: true);
        if (_loadingTitle != null)
            _loadingTitle.Text = "Подключение";
        if (_loadingServer != null)
            _loadingServer.Text = _selected.Name;
        if (_loadingStatus != null)
            _loadingStatus.Text = "Подготовка…";
        if (_loadingProgress != null)
            _loadingProgress.Progress = 0;
        if (_loadingPct != null)
            _loadingPct.Text = "";
        if (_loadingSpeed != null)
            _loadingSpeed.Visibility = ViewStates.Gone;
        if (_screenLoading != null)
            _screenLoading.Visibility = ViewStates.Visible;
        _ = UpdateLoadingMessageAsync();

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

    async Task UpdateLoadingMessageAsync()
    {
        if (_loadingMessage is null)
            return;
        var msg = await LauncherTheme.GetRandomMessageAsync();
        if (_loadingMessage is null)
            return;
        _loadingMessage.Text = msg;
    }

    void SetVersionText()
    {
        if (_versionText is null)
            return;
        try
        {
            var pkg = PackageManager?.GetPackageInfo(PackageName ?? "", 0);
            _versionText.Text = $"SS14 Hub {pkg?.VersionName} · beta";
        }
        catch
        {
            _versionText.Text = "SS14 Hub · beta";
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
            if (_renderSystem is not null)
                _renderSystem.CameraSource = null;
            _clyde = null;
            _inputBridge = null;
            _uiHost = null;
            _contentClient = null;
            if (_connect is not null)
                _connect.Session.OnContentReady = null;
            _clientLoop?.Shutdown();
            _clientLoop = null;
            _renderSystem = null;
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
        var fullscreenChrome = observing || lobby;
        if (fullscreenChrome != _immersiveApplied)
        {
            _immersiveApplied = fullscreenChrome;
            ApplyObserveImmersive(fullscreenChrome);
        }

        if (_screenLoading != null)
            _screenLoading.Visibility = loading ? ViewStates.Visible : ViewStates.Gone;
        // The launcher stays visible under the ConnectingOverlay, as on PC.
        if (_screenHome != null)
            _screenHome.Visibility = lobby || observing ? ViewStates.Gone : ViewStates.Visible;
        if (_screenLobby != null)
            _screenLobby.Visibility = lobby ? ViewStates.Visible : ViewStates.Gone;
        if (_screenObserve != null)
            _screenObserve.Visibility = observing ? ViewStates.Visible : ViewStates.Gone;

        s_uiObserving = _uiObserving;

        // Landscape while loading / lobby / observe. Portrait only on launcher pages.
        if (loading || _connect.Busy || lobby || observing)
            ApplyOrientation(landscape: true);
        else
            ApplyOrientation(landscape: false);

        // MainWindowLogin replaces the tabbed content while logged out (unless skipped).
        var loggedIn = AuthSessionConfig.TryLoad(_connect.AuthConfigPath)?.HasRequiredFields == true;
        if (!loggedIn && !_skipLogin)
        {
            if (_tabIndex != TabLogin)
                SelectTab(TabLogin);
        }
        else if (_tabIndex == TabLogin)
        {
            SelectTab(_lastLauncherTab);
        }

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
            _authStatus.Text = loggedIn ? _authUiStatus : GetString(Resource.String.account_not_logged);
        if (_logoutBtn != null)
            _logoutBtn.Visibility = loggedIn ? ViewStates.Visible : ViewStates.Gone;
        var headerLogin = FindViewById<Button>(Resource.Id.btn_header_login);
        if (headerLogin != null)
            headerLogin.Visibility = !loggedIn && _skipLogin ? ViewStates.Visible : ViewStates.Gone;

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
                _lobbyRound.SetTextColor(LauncherTheme.SubText);
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
            _lobbyCharStatus.SetTextColor(ready ? LauncherTheme.Green : LauncherTheme.SubText);
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
                _lobbyPlayers.SetTextColor(LauncherTheme.Fg);
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
        }
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
        {
            var indeterminate = p.Total <= 0;
            _loadingProgress.Indeterminate = indeterminate;
            if (!indeterminate)
                _loadingProgress.Progress = p.Percent;
        }

        // Smoothed download speed for the ConnectingOverlay speed cell.
        double mbps = 0;
        var nowMs = Environment.TickCount64;
        if (_speedLastMs > 0 && p.BytesWritten >= _speedLastBytes && nowMs > _speedLastMs)
        {
            var rate = (p.BytesWritten - _speedLastBytes) / (nowMs - _speedLastMs) / 1024.0 / 1024.0;
            _speedEma = _speedEma <= 0 ? rate : _speedEma * 0.7 + rate * 0.3;
            mbps = _speedEma;
        }
        _speedLastBytes = p.BytesWritten;
        _speedLastMs = nowMs;

        if (_loadingSpeed != null)
        {
            var showSpeed = mbps > 0.01;
            _loadingSpeed.Visibility = showSpeed ? ViewStates.Visible : ViewStates.Gone;
            if (showSpeed)
                _loadingSpeed.Text = $"{mbps:0.0} MB/s";
        }

        if (_loadingPct != null)
        {
            var mb = p.BytesWritten > 0 ? $" · {p.BytesWritten / (1024.0 * 1024.0):0.0} MB" : "";
            _loadingPct.Text = p.Total <= 0
                ? ""
                : $"{p.Percent}%  ·  {p.Stage}  {p.Done}/{p.Total}{mb}";
        }

        if (_loadingStatus != null)
            _loadingStatus.Text = _connect.Summary;
        if (_loadingTitle != null)
        {
            _loadingTitle.Text = p.Stage switch
            {
                "textures" => "Загрузка контента",
                "prototypes" => "Прототипы",
                "assemblies" => "Сборки",
                "manifest" or "index" => "Манифест",
                "done" => "Готово",
                _ => "Подключение",
            };
        }

        if (_connectStatus != null)
            _connectStatus.Text = _connect.Summary;
        if (_joinDebug != null)
            _joinDebug.Text = _connect.DebugLog;
    }
}
