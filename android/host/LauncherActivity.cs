using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Threading.Tasks;
using Android.App;
using Android.Content;
using Android.Graphics;
using Android.Graphics.Drawables;
using Android.OS;
using Android.Views;
using Android.Widget;
using Content.Android.Launcher.Api;
using Content.Android.Launcher.Models;
using Content.Android.Launcher.Services;
using Content.Android.Launcher.Storage;
using Content.Android.Launcher.UI;
using Content.Android.Launcher.Utility;
using Path = System.IO.Path;
using Environment = System.Environment;
using Vector2 = System.Numerics.Vector2;

namespace Content.Android;

[Activity(
    Label = "Space Station 14",
    MainLauncher = true,
    Icon = "@mipmap/ic_launcher",
    RoundIcon = "@mipmap/ic_launcher_round",
    Theme = "@android:style/Theme.DeviceDefault.NoActionBar.Fullscreen",
    ConfigurationChanges =
        global::Android.Content.PM.ConfigChanges.Orientation |
        global::Android.Content.PM.ConfigChanges.Keyboard |
        global::Android.Content.PM.ConfigChanges.KeyboardHidden |
        global::Android.Content.PM.ConfigChanges.ScreenSize |
        global::Android.Content.PM.ConfigChanges.ScreenLayout |
        global::Android.Content.PM.ConfigChanges.UiMode |
        global::Android.Content.PM.ConfigChanges.Navigation |
        global::Android.Content.PM.ConfigChanges.Touchscreen |
        global::Android.Content.PM.ConfigChanges.SmallestScreenSize |
        global::Android.Content.PM.ConfigChanges.Density |
        global::Android.Content.PM.ConfigChanges.FontScale |
        global::Android.Content.PM.ConfigChanges.LayoutDirection |
        global::Android.Content.PM.ConfigChanges.Locale,
    ScreenOrientation = global::Android.Content.PM.ScreenOrientation.SensorLandscape)]
public sealed class LauncherActivity : Activity
{
    private enum Tab
    {
        Home,
        Servers,
        Options
    }

    private readonly LauncherStorage _storage = new();
    private readonly HubClient _hubClient = new();
    private readonly AuthClient _authClient = new();
    private readonly AczClient _aczClient = new();

    private List<ServerListEntry> _servers = new();
    private Tab _activeTab = Tab.Home;
    private string _searchFilter = string.Empty;

    private Button _accountBtn = default!;
    private Button _tabServersBtn = default!;
    private Button _tabFavsBtn = default!;
    private Button _tabDirectBtn = default!;
    private EditText _searchInput = default!;
    private LinearLayout _listLayout = default!;
    private ScrollView _scroll = default!;

    private LinearLayout _overlayBox = default!;
    private TextView _overlayText = default!;
    private LinearLayout? _overlayBar;
    private View? _overlayFill;

    private ServerInfo? _pendingServerInfo;
    private string? _pendingServerAddress;

    private string? _expandedServerAddress;
    private readonly Dictionary<string, ServerInfo> _serverInfoCache = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _loadingServerInfo = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, (LinearLayout Container, TextView Chevron, ServerListEntry Server)> _renderedServerRows = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, bool> _rowAlternates = new(StringComparer.OrdinalIgnoreCase);
    private int _serverRenderLimit = 60;

    private bool _filtersVisible;
    private string _filterLanguage = "all";
    private string _filterRegion = "all";
    private bool _filterHideEmpty;
    private bool _filterHideFull;
    private Button _filterBtn = default!;
    private LinearLayout _filtersPanel = default!;

    protected override void OnCreate(Bundle? savedInstanceState)
    {
        base.OnCreate(savedInstanceState);
        AczClient.InitNative();

        if (Build.VERSION.SdkInt >= BuildVersionCodes.Tiramisu)
        {
            if (CheckSelfPermission(global::Android.Manifest.Permission.PostNotifications) != global::Android.Content.PM.Permission.Granted)
            {
                RequestPermissions(new[] { global::Android.Manifest.Permission.PostNotifications }, 101);
            }
        }

        ContentDownloadService.ProgressChanged += OnServiceProgressChanged;
        ContentDownloadService.DownloadCompleted += OnServiceDownloadCompleted;
        ContentDownloadService.DownloadFailed += OnServiceDownloadFailed;

        ApplyImmersiveMode();
        MigratePadLayout();
        BuildOfficialUi();
        BuildLoadingOverlay();
        _ = RefreshHubServersAsync();


        if (!_storage.Data.DisclaimerAccepted)
        {
            ShowDisclaimerDialog();
        }
        else
        {
            _ = CheckForUpdatesAsync();
        }

        CheckForCrashAndPrompt();
        _ = Task.Run(() => ContentCacheManager.PruneStaleCache(this, _storage));
        TryAutoConnect(Intent);
    }

    /// <summary>
    /// Full pad layout for the game: the editor's default table merged with the
    /// player's saved changes. The launcher always passes it, so the engine can
    /// never lay the pad out from a stale or different copy of the defaults.
    /// Centers are clamped to the physical safe frame the editor draws, because
    /// the engine applies the layout as is and does not clamp it again.
    /// </summary>
    private string BuildPadLayout(ISharedPreferences? prefs)
    {
        var (screenW, screenH) = MobileSafeArea.ScreenSize(this);
        var (frameMode, frameScale) = MobileSafeArea.ReadPrefs(this);
        var insets = MobileSafeArea.MeasureWindow(this, frameMode, frameScale);
        var layout = PadEditorView.BuildLayout(prefs, screenW, screenH, insets);
        CrashReporter.AppendLauncherLog("pad",
            $"screen={screenW}x{screenH} insets={insets.Left},{insets.Top},{insets.Right},{insets.Bottom} " +
            $"frame={frameMode}x{frameScale} layout={layout}");
        return layout;
    }

    /// <summary>
    /// Pad positions saved before the full screen editor were fractions of the
    /// smaller editor view, so the game drew them in the wrong place. Drop them
    /// once so the shared fraction table is exactly what the player sees.
    /// </summary>
    private void MigratePadLayout()
    {
        var prefs = GetSharedPreferences("ss14_mobile_config", FileCreationMode.Private);
        if (prefs == null || prefs.GetInt("mobile_positions_layout", 0) >= PadEditorView.LayoutVersion)
            return;

        // Layout 4: the editor preview is now drawn in the immersive full
        // screen frame, so older fractions (measured inside a smaller dialog)
        // would land slightly off. Layout 5: the base is the physical screen
        // instead of the activity window, so fractions saved against the
        // smaller window would draw the pad scaled up. Drop them once.
        prefs.Edit()?
            .Remove("mobile_positions")?
            .PutInt("mobile_positions_layout", PadEditorView.LayoutVersion)?
            .Apply();
    }

    protected override void OnNewIntent(Intent? intent)
    {
        base.OnNewIntent(intent);
        Intent = intent;
        TryAutoConnect(intent);
    }

    // Прямой запуск из другого приложения: adb/хаб передаёт connect_addr или ss14:// ссылку.
    private void TryAutoConnect(Intent? intent)
    {
        if (intent == null) return;

        var addr = intent.GetStringExtra("connect_addr");
        if (string.IsNullOrWhiteSpace(addr) && intent.Data != null)
            addr = intent.Data.ToString();

        if (string.IsNullOrWhiteSpace(addr)) return;

        global::Android.Util.Log.Info("SS14Launcher", $"Auto-connect to {addr}");
        _ = StartConnectingAsync(addr!);
    }

    protected override void OnResume()
    {
        base.OnResume();
        ApplyImmersiveMode();
        UpdateAccountButton();

        if (ContentDownloadService.IsDownloading)
        {
            Window?.AddFlags(WindowManagerFlags.KeepScreenOn);
            _overlayBox.Visibility = ViewStates.Visible;
            var tot = ContentDownloadService.TotalProgress;
            var cur = ContentDownloadService.CurrentProgress;
            SetOverlayProgress(tot > 0 ? (int)(cur * 100L / tot) : -1);
            _overlayText.Text = ContentDownloadService.CurrentStatus ?? "Downloading content...";
        }

    }

    protected override void OnDestroy()
    {
        base.OnDestroy();
        ContentDownloadService.ProgressChanged -= OnServiceProgressChanged;
        ContentDownloadService.DownloadCompleted -= OnServiceDownloadCompleted;
        ContentDownloadService.DownloadFailed -= OnServiceDownloadFailed;
    }

    /// <summary>
    /// Assign through the attributes property: a field change on the object in place
    /// does not reach the window manager once the window is attached.
    /// </summary>
    private void SetCutoutMode(LayoutInDisplayCutoutMode mode)
    {
        var attributes = Window?.Attributes;
        if (attributes == null)
            return;
        attributes.LayoutInDisplayCutoutMode = mode;
        Window!.Attributes = attributes;
    }

    private void ApplyImmersiveMode()
    {
        try
        {
            if (Window == null) return;
            if (Build.VERSION.SdkInt >= BuildVersionCodes.R)
            {
                // Always: the cutout has to be reported in landscape even when the
                // camera hole is on the long edge. The pad layout is clamped against
                // these values before launch.
                SetCutoutMode(LayoutInDisplayCutoutMode.Always);
                Window.InsetsController?.Hide(WindowInsets.Type.StatusBars() | WindowInsets.Type.NavigationBars());
            }
            else
            {
                if (Build.VERSION.SdkInt >= BuildVersionCodes.P)
                    SetCutoutMode(LayoutInDisplayCutoutMode.ShortEdges);
#pragma warning disable CS0618
                Window.DecorView.SystemUiVisibility = (StatusBarVisibility)(
                    SystemUiFlags.Fullscreen |
                    SystemUiFlags.HideNavigation |
                    SystemUiFlags.ImmersiveSticky |
                    SystemUiFlags.LayoutStable);
#pragma warning restore CS0618
            }
        }
        catch {}
    }


    private LinearLayout _pageHost = default!;
    private LinearLayout _bottomBar = default!;
    private TextView _versionLabel = default!;
    private EditText _loginUser = default!;
    private EditText _loginPass = default!;
    private TextView _loginError = default!;
    private Button _loginSubmit = default!;
    private bool _loginBusy;
    private bool _tfaRequired;
    private bool _tfaBusy;
    private string _tfaUsername = "";
    private string _tfaPassword = "";
    private EditText _tfaCode = default!;
    private TextView _tfaError = default!;
    private Button _tfaConfirm = default!;

    private float Density => Resources?.DisplayMetrics?.Density ?? 1f;
    private int Dp(float value) => (int)(value * Density);

    private void LoadLogo(ImageView imageView)
    {
        try
        {
            var resName = typeof(LauncherActivity).Assembly.GetManifestResourceNames()
                .FirstOrDefault(n => n.EndsWith("logo-long.png", StringComparison.OrdinalIgnoreCase));
            if (resName != null)
            {
                using var stream = typeof(LauncherActivity).Assembly.GetManifestResourceStream(resName);
                if (stream != null)
                {
                    var bmp = BitmapFactory.DecodeStream(stream);
                    imageView.SetImageBitmap(bmp);
                    return;
                }
            }
        }
        catch {}
    }

    private void BuildOfficialUi()
    {
        var root = new LinearLayout(this) { Orientation = Orientation.Vertical };
        root.SetBackgroundColor(LauncherTheme.Background);

        var header = new LinearLayout(this) { Orientation = Orientation.Horizontal };
        header.SetBackgroundColor(LauncherTheme.HeaderBackground);
        header.SetGravity(GravityFlags.CenterVertical);
        header.SetPadding(Dp(4), Dp(4), Dp(6), Dp(4));

        var logo = new ImageView(this);
        logo.SetAdjustViewBounds(true);
        logo.LayoutParameters = new LinearLayout.LayoutParams(Dp(184), Dp(46));
        LoadLogo(logo);
        header.AddView(logo);

        var headerSpacer = new Space(this);
        header.AddView(headerSpacer, new LinearLayout.LayoutParams(0, 1, 1f));

        var right = new LinearLayout(this) { Orientation = Orientation.Vertical };
        right.SetGravity(GravityFlags.End);

        var links = new LinearLayout(this) { Orientation = Orientation.Horizontal };
        links.SetGravity(GravityFlags.End);
        var discord = CreateHeaderLinkButton("Discord", () => OpenUrl("https://discord.gg/ss14"));
        links.AddView(discord, new LinearLayout.LayoutParams(Dp(100), Dp(26)) { RightMargin = Dp(2) });
        var website = CreateHeaderLinkButton("Website", () => OpenUrl("https://spacestation14.com/"));
        links.AddView(website, new LinearLayout.LayoutParams(Dp(100), Dp(26)));
        right.AddView(links);

        _accountBtn = new Button(this) { TextSize = 10f };
        _accountBtn.SetTextColor(LauncherTheme.Foreground);
        _accountBtn.SetPadding(Dp(10), 0, Dp(10), 0);
        _accountBtn.Gravity = GravityFlags.Left | GravityFlags.CenterVertical;
        _accountBtn.Background = CreateAngleButtonBackground(Dp(10), AngleSide.Both);
        _accountBtn.LayoutParameters = new LinearLayout.LayoutParams(Dp(202), Dp(26)) { TopMargin = Dp(2) };
        _accountBtn.Click += (_, _) => OnAccountButtonClicked();
        right.AddView(_accountBtn);

        header.AddView(right);
        root.AddView(header);

        var goldLine = new View(this);
        goldLine.SetBackgroundColor(LauncherTheme.NanoGold);
        root.AddView(goldLine, new LinearLayout.LayoutParams(ViewGroup.LayoutParams.MatchParent, Dp(2)));

        _pageHost = new LinearLayout(this) { Orientation = Orientation.Vertical };
        _pageHost.SetBackgroundColor(LauncherTheme.Background);
        root.AddView(_pageHost, new LinearLayout.LayoutParams(ViewGroup.LayoutParams.MatchParent, 0, 1f));

        var edge = new View(this);
        edge.SetBackgroundColor(LauncherTheme.StripeEdge);
        root.AddView(edge, new LinearLayout.LayoutParams(ViewGroup.LayoutParams.MatchParent, Dp(2)));

        _bottomBar = new LinearLayout(this) { Orientation = Orientation.Horizontal };
        _bottomBar.SetPadding(Dp(4), 0, Dp(4), 0);
        root.AddView(_bottomBar, new LinearLayout.LayoutParams(ViewGroup.LayoutParams.MatchParent, Dp(44)));

        SetContentView(root);
        root.SetOnApplyWindowInsetsListener(new SafeInsetsListener((view, insets) =>
        {
            var top = insets.SystemWindowInsetTop;
            var bottom = insets.SystemWindowInsetBottom;
            var left = insets.SystemWindowInsetLeft;
            var right = insets.SystemWindowInsetRight;
            var cutout = insets.DisplayCutout;
            if (cutout != null)
            {
                top = Math.Max(top, cutout.SafeInsetTop);
                bottom = Math.Max(bottom, cutout.SafeInsetBottom);
                left = Math.Max(left, cutout.SafeInsetLeft);
                right = Math.Max(right, cutout.SafeInsetRight);
            }

            var minBorder = Dp(4);
            view.SetPadding(Math.Max(left, minBorder), Math.Max(top, minBorder),
                Math.Max(right, minBorder), Math.Max(bottom, minBorder));
        }));
        UpdateAccountButton();
    }

    private static LinearLayout.LayoutParams MatchParent() =>
        new(ViewGroup.LayoutParams.MatchParent, ViewGroup.LayoutParams.MatchParent);

    private Drawable CreateAngleButtonBackground(float corner, AngleSide side)
    {
        var states = new StateListDrawable();
        states.AddState(new[] { global::Android.Resource.Attribute.StatePressed }, new AngleDrawable(LauncherTheme.ControlHigh, corner, side));
        states.AddState(new[] { global::Android.Resource.Attribute.StateEnabled }, new AngleDrawable(LauncherTheme.ControlMid, corner, side));
        states.AddState(Array.Empty<int>(), new AngleDrawable(LauncherTheme.ControlMid, corner, side));
        return states;
    }

    private Button CreateAngleButton(string text, float heightDp, int widthDp)
    {
        var button = new Button(this) { Text = text, TextSize = 12f };
        button.SetTextColor(LauncherTheme.Foreground);
        button.SetPadding(Dp(10), 0, Dp(10), 0);
        button.Background = CreateAngleButtonBackground(Dp(10), AngleSide.Both);
        button.LayoutParameters = new LinearLayout.LayoutParams(Dp(widthDp), Dp(heightDp)) { RightMargin = Dp(4) };
        return button;
    }

    private Button CreateHeaderLinkButton(string text, Action onClick)
    {
        var button = new Button(this) { Text = text, TextSize = 10f };
        button.SetTextColor(LauncherTheme.Foreground);
        button.SetPadding(Dp(8), 0, Dp(8), 0);
        button.Background = CreateAngleButtonBackground(Dp(10), AngleSide.Both);
        button.Click += (_, _) => onClick();
        return button;
    }

    private void UpdateAccountButton()
    {
        if (_accountBtn == null)
            return;

        var account = _storage.GetActiveAccount();
        _accountBtn.Text = account != null && !string.IsNullOrEmpty(account.Token)
            ? account.Username + " ▼"
            : "Log in ▼";
        _accountBtn.SetTextColor(account != null ? Color.ParseColor("#86EFAC") : LauncherTheme.SubText);
        RebuildShell();
    }

    private void RebuildShell()
    {
        if (_pageHost == null || _bottomBar == null)
            return;

        _pageHost.RemoveAllViews();
        _bottomBar.RemoveAllViews();
        _bottomBar.Background = new StripedBackgroundDrawable(Density);

        if (_tfaRequired)
        {
            _pageHost.AddView(BuildTfaPage(), MatchParent());
            AddLoginChrome();
            return;
        }

        var account = _storage.GetActiveAccount();
        if (account == null || string.IsNullOrEmpty(account.Token))
        {
            _pageHost.AddView(BuildLoginPage(), MatchParent());
            AddLoginChrome();
            return;
        }

        View page = _activeTab switch
        {
            Tab.Home => BuildHomePage(),
            Tab.Options => BuildOptionsPage(),
            _ => BuildServersPage()
        };
        _pageHost.AddView(page, MatchParent());
        BuildTabBar();
    }

    private void AddLoginChrome()
    {
        var logs = CreateFooterTextButton("Logs", () => ShowCrashReporterDialog());
        _bottomBar.AddView(logs, new LinearLayout.LayoutParams(Dp(150), Dp(30)) { LeftMargin = Dp(4), TopMargin = Dp(4) });
        var spacer = new Space(this);
        _bottomBar.AddView(spacer, new LinearLayout.LayoutParams(0, 1, 1f));
        var version = new TextView(this) { Text = GetCurrentAppVersion(), TextSize = 10f };
        version.SetTextColor(LauncherTheme.SubText);
        version.Gravity = GravityFlags.CenterVertical;
        version.SetPadding(0, 0, Dp(6), 0);
        _bottomBar.AddView(version, new LinearLayout.LayoutParams(ViewGroup.LayoutParams.WrapContent, ViewGroup.LayoutParams.MatchParent));
    }

    private TextView CreateFooterTextButton(string text, Action onClick)
    {
        var view = new TextView(this) { Text = text, TextSize = 10f };
        view.SetTextColor(LauncherTheme.Foreground);
        view.Gravity = GravityFlags.CenterVertical;
        view.SetPadding(Dp(6), 0, Dp(6), 0);
        view.Click += (_, _) => onClick();
        return view;
    }

    private void SetOverlayProgress(int percent)
    {
        if (_overlayFill == null)
            return;

        void Apply()
        {
            var width = _overlayBar?.Width ?? 0;
            if (width <= 0)
                return;

            var lp = _overlayFill.LayoutParameters;
            if (lp == null)
                return;

            lp.Width = percent < 0 ? 0 : Math.Max(0, (int)(width * (percent / 100.0)));
            _overlayFill.LayoutParameters = lp;
        }

        if (_overlayBar?.Width > 0)
            Apply();
        else
            _overlayBar?.Post(Apply);
    }

    private void BuildLoadingOverlay()
    {
        _overlayBox = new LinearLayout(this)
        {
            Orientation = Orientation.Vertical,
            Visibility = ViewStates.Gone
        };
        _overlayBox.SetBackgroundColor(Color.Argb(204, 0x18, 0x18, 0x1A));
        _overlayBox.SetGravity(GravityFlags.Center);
        _overlayBox.Clickable = true;

        var modal = new LinearLayout(this) { Orientation = Orientation.Vertical };
        modal.Background = LauncherTheme.CreateBox(LauncherTheme.PopupBackground, 0, LauncherTheme.StripeEdge, 1);
        modal.SetPadding(Dp(18), Dp(14), Dp(18), Dp(12));
        _overlayBox.AddView(modal, new LinearLayout.LayoutParams(Dp(420), ViewGroup.LayoutParams.WrapContent));

        var title = new TextView(this) { Text = "Connecting" };
        title.SetTypeface(Typeface.Default, TypefaceStyle.Bold);
        title.TextSize = 16f;
        title.SetTextColor(LauncherTheme.Foreground);
        title.Gravity = GravityFlags.Center;
        modal.AddView(title, new LinearLayout.LayoutParams(ViewGroup.LayoutParams.MatchParent, ViewGroup.LayoutParams.WrapContent));

        _overlayText = new TextView(this) { Text = "Connecting...", TextSize = 11f };
        _overlayText.SetTextColor(LauncherTheme.SubText);
        _overlayText.Gravity = GravityFlags.Center;
        _overlayText.SetPadding(0, Dp(10), 0, 0);
        modal.AddView(_overlayText, new LinearLayout.LayoutParams(ViewGroup.LayoutParams.MatchParent, ViewGroup.LayoutParams.WrapContent));

        _overlayBar = new LinearLayout(this) { Orientation = Orientation.Horizontal };
        var barBackground = new global::Android.Graphics.Drawables.GradientDrawable();
        barBackground.SetColor(Color.ParseColor("#3A3A40"));
        barBackground.SetCornerRadius(Dp(3));
        _overlayBar.Background = barBackground;

        _overlayFill = new View(this);
        var fillDrawable = new global::Android.Graphics.Drawables.GradientDrawable();
        fillDrawable.SetColor(Color.ParseColor("#2E9E5B"));
        fillDrawable.SetCornerRadius(Dp(3));
        _overlayFill.Background = fillDrawable;
        _overlayBar.AddView(_overlayFill, new LinearLayout.LayoutParams(0, ViewGroup.LayoutParams.MatchParent));

        modal.AddView(_overlayBar, new LinearLayout.LayoutParams(ViewGroup.LayoutParams.MatchParent, Dp(16))
        {
            TopMargin = Dp(12)
        });

        var bottom = new LinearLayout(this) { Orientation = Orientation.Horizontal };
        bottom.SetGravity(GravityFlags.CenterVertical);
        bottom.SetPadding(0, Dp(12), 0, 0);
        var cancel = CreateAngleButton("Cancel", 28, 96);
        cancel.Click += (_, _) =>
        {
            Window?.ClearFlags(WindowManagerFlags.KeepScreenOn);
            _overlayBox.Visibility = ViewStates.Gone;
        };
        bottom.AddView(cancel, new LinearLayout.LayoutParams(Dp(96), Dp(28)));
        modal.AddView(bottom, new LinearLayout.LayoutParams(ViewGroup.LayoutParams.MatchParent, ViewGroup.LayoutParams.WrapContent));

        AddContentView(_overlayBox, new FrameLayout.LayoutParams(ViewGroup.LayoutParams.MatchParent, ViewGroup.LayoutParams.MatchParent));
    }

    private void BuildTabBar()
    {
        _bottomBar.RemoveAllViews();
        var tabs = new[] { (Tab.Home, "Home"), (Tab.Servers, "Servers"), (Tab.Options, "Options") };
        foreach (var (tab, label) in tabs)
        {
            var tabView = new TextView(this) { Text = label, TextSize = 13f };
            tabView.Gravity = GravityFlags.Center;
            tabView.SetPadding(Dp(18), 0, Dp(18), 0);
            tabView.SetTextColor(_activeTab == tab ? LauncherTheme.Foreground : LauncherTheme.SubText);
            if (_activeTab == tab)
                tabView.Background = new ColorDrawable(Color.Argb(204, 0x3E, 0x6C, 0x45));
            tabView.Click += (_, _) => SwitchTab(tab);
            _bottomBar.AddView(tabView, new LinearLayout.LayoutParams(ViewGroup.LayoutParams.WrapContent, ViewGroup.LayoutParams.MatchParent));
        }

        var spacer = new Space(this);
        _bottomBar.AddView(spacer, new LinearLayout.LayoutParams(0, 1, 1f));

        _versionLabel = new TextView(this) { Text = GetCurrentAppVersion(), TextSize = 10f };
        _versionLabel.SetTextColor(LauncherTheme.SubText);
        _versionLabel.Gravity = GravityFlags.CenterVertical;
        _versionLabel.SetPadding(Dp(8), 0, Dp(8), 0);
        _versionLabel.Click += (_, _) => _ = CheckForUpdatesAsync(true);
        _bottomBar.AddView(_versionLabel, new LinearLayout.LayoutParams(ViewGroup.LayoutParams.WrapContent, ViewGroup.LayoutParams.MatchParent));
    }

    private void SwitchTab(Tab tab)
    {
        _activeTab = tab;
        RebuildShell();
    }

    private void OnAccountButtonClicked()
    {
        var account = _storage.GetActiveAccount();
        if (account == null || string.IsNullOrEmpty(account.Token))
        {
            if (_loginUser != null)
            {
                _loginUser.RequestFocus();
                ShowKeyboard(_loginUser);
            }
            return;
        }

        var list = new LinearLayout(this) { Orientation = Orientation.Vertical };
        list.SetBackgroundColor(LauncherTheme.PopupBackground);
        list.SetPadding(Dp(2), Dp(4), Dp(2), Dp(4));

        var popup = new PopupWindow(list, Dp(220), ViewGroup.LayoutParams.WrapContent, true);
        popup.SetBackgroundDrawable(new ColorDrawable(Color.Transparent));
        popup.OutsideTouchable = true;
        popup.Elevation = Dp(8);

        var user = new TextView(this) { Text = account.Username, TextSize = 10f };
        user.SetTextColor(LauncherTheme.SubText);
        user.SetPadding(Dp(12), Dp(6), Dp(12), Dp(6));
        list.AddView(user, new LinearLayout.LayoutParams(ViewGroup.LayoutParams.MatchParent, ViewGroup.LayoutParams.WrapContent));

        var line = new View(this);
        line.SetBackgroundColor(Color.Argb(170, 0x57, 0x5B, 0x7F));
        list.AddView(line, new LinearLayout.LayoutParams(ViewGroup.LayoutParams.MatchParent, Dp(2)));

        void AddItem(string text, Action action)
        {
            var item = new TextView(this) { Text = text, TextSize = 12f };
            item.SetTextColor(LauncherTheme.Foreground);
            item.SetPadding(Dp(12), Dp(9), Dp(12), Dp(9));
            item.Clickable = true;
            item.Click += (_, _) =>
            {
                popup.Dismiss();
                action();
            };
            list.AddView(item, new LinearLayout.LayoutParams(ViewGroup.LayoutParams.MatchParent, ViewGroup.LayoutParams.WrapContent));
        }

        AddItem("Log out", PerformLogout);
        AddItem("Switch account", PerformLogout);
        AddItem("Account settings (web)", () => OpenUrl("https://account.spacestation14.com/Identity/Account/Manage"));

        popup.ShowAsDropDown(_accountBtn, Dp(0), Dp(4));
    }

    private void PerformLogout()
    {
        var account = _storage.GetActiveAccount();
        if (account != null)
            _ = _authClient.LogoutAsync(account.Token);
        if (!string.IsNullOrEmpty(account.UserId))
            _storage.RemoveAccount(account.UserId);
        try
        {
            var authFile = Path.Combine(FilesDir?.AbsolutePath ?? "", "auth.json");
            if (File.Exists(authFile))
                File.Delete(authFile);
        }
        catch
        {
        }

        UpdateAccountButton();
    }

    private View BuildLoginPage()
    {
        var page = new LinearLayout(this) { Orientation = Orientation.Vertical };
        page.SetGravity(GravityFlags.CenterHorizontal);
        page.SetPadding(Dp(16), Dp(10), Dp(16), Dp(6));

        var title = new TextView(this) { Text = "Log in", TextSize = 20f };
        title.SetTypeface(Typeface.Default, TypefaceStyle.Bold);
        title.SetTextColor(LauncherTheme.NanoGold);
        title.Gravity = GravityFlags.Center;
        page.AddView(title, new LinearLayout.LayoutParams(ViewGroup.LayoutParams.MatchParent, ViewGroup.LayoutParams.WrapContent));

        var form = new LinearLayout(this) { Orientation = Orientation.Vertical };
        form.SetGravity(GravityFlags.CenterHorizontal);
        page.AddView(form, new LinearLayout.LayoutParams(ViewGroup.LayoutParams.MatchParent, 0, 1f));

        _loginUser = new EditText(this) { Hint = "Username" };
        _loginUser.SetSingleLine(true);
        _loginUser.TextSize = 12f;
        _loginUser.SetTextColor(LauncherTheme.Foreground);
        _loginUser.SetHintTextColor(LauncherTheme.SubText);
        _loginUser.Background = LauncherTheme.CreateBox(LauncherTheme.PopupBackground, 0, LauncherTheme.StripeEdge, 1);
        _loginUser.SetPadding(Dp(8), 0, Dp(8), 0);
        form.AddView(_loginUser, new LinearLayout.LayoutParams(Dp(300), Dp(32)) { TopMargin = Dp(8) });

        _loginPass = new EditText(this) { Hint = "Password" };
        _loginPass.SetSingleLine(true);
        _loginPass.TextSize = 12f;
        _loginPass.InputType = global::Android.Text.InputTypes.ClassText | global::Android.Text.InputTypes.TextVariationPassword;
        _loginPass.SetTextColor(LauncherTheme.Foreground);
        _loginPass.SetHintTextColor(LauncherTheme.SubText);
        _loginPass.Background = LauncherTheme.CreateBox(LauncherTheme.PopupBackground, 0, LauncherTheme.StripeEdge, 1);
        _loginPass.SetPadding(Dp(8), 0, Dp(8), 0);
        form.AddView(_loginPass, new LinearLayout.LayoutParams(Dp(300), Dp(32)) { TopMargin = Dp(4) });

        var showPassword = new CheckBox(this) { Text = "Show password", TextSize = 11f };
        showPassword.SetTextColor(LauncherTheme.Foreground);
        showPassword.ButtonTintList = global::Android.Content.Res.ColorStateList.ValueOf(LauncherTheme.NanoGold);
        showPassword.CheckedChange += (_, args) =>
        {
            _loginPass.InputType = args.IsChecked
                ? global::Android.Text.InputTypes.ClassText | global::Android.Text.InputTypes.TextVariationVisiblePassword
                : global::Android.Text.InputTypes.ClassText | global::Android.Text.InputTypes.TextVariationPassword;
        };
        form.AddView(showPassword, new LinearLayout.LayoutParams(ViewGroup.LayoutParams.WrapContent, Dp(28)) { TopMargin = Dp(2) });

        _loginError = new TextView(this) { TextSize = 11f };
        _loginError.SetTextColor(Color.ParseColor("#FF6B6B"));
        _loginError.Gravity = GravityFlags.Center;
        _loginError.Visibility = ViewStates.Gone;
        form.AddView(_loginError, new LinearLayout.LayoutParams(Dp(300), ViewGroup.LayoutParams.WrapContent));

        var buttons = new LinearLayout(this) { Orientation = Orientation.Horizontal };
        buttons.SetGravity(GravityFlags.Center);
        _loginSubmit = CreateAngleButton("Log In", 32, 100);
        _loginSubmit.Click += async (_, _) => await SubmitLoginAsync();
        buttons.AddView(_loginSubmit);
        var forgot = CreateAngleButton("Forgot password", 32, 150);
        forgot.Click += (_, _) => OpenUrl("https://account.spacestation14.com/Identity/Account/ForgotPassword");
        buttons.AddView(forgot);
        form.AddView(buttons, new LinearLayout.LayoutParams(ViewGroup.LayoutParams.WrapContent, ViewGroup.LayoutParams.WrapContent) { TopMargin = Dp(4) });

        var footer = new LinearLayout(this) { Orientation = Orientation.Vertical };
        footer.SetGravity(GravityFlags.CenterHorizontal);
        var register = CreateAngleButton("Register", 32, 150);
        register.Click += (_, _) => OpenUrl("https://account.spacestation14.com/Identity/Account/Register");
        footer.AddView(register, new LinearLayout.LayoutParams(Dp(150), Dp(32)));
        page.AddView(footer, new LinearLayout.LayoutParams(ViewGroup.LayoutParams.MatchParent, ViewGroup.LayoutParams.WrapContent) { BottomMargin = Dp(4) });

        return page;
    }

    private async Task SubmitLoginAsync()
    {
        if (_loginBusy || _loginUser == null || _loginPass == null)
            return;

        if (_loginError != null)
            _loginError.Visibility = ViewStates.Gone;

        var username = _loginUser.Text ?? "";
        var password = _loginPass.Text ?? "";
        var result = await AuthenticateAccountAsync(username, password, null);
        if (result.Success && result.Account != null)
        {
            SaveAccount(result.Account);
            return;
        }

        if (result.TfaRequired)
        {
            _tfaRequired = true;
            _tfaUsername = username;
            _tfaPassword = password;
            RebuildShell();
            return;
        }

        ShowLoginError(result.ErrorMessage);
    }

    private async Task<AuthResult> AuthenticateAccountAsync(string username, string password, string? tfaCode)
    {
        _loginBusy = true;
        if (CurrentFocus != null)
            HideKeyboard(CurrentFocus);
        try
        {
            return await _authClient.AuthenticateAsync(username, password, tfaCode);
        }
        finally
        {
            _loginBusy = false;
        }
    }

    private void SaveAccount(AccountInfo account)
    {
        _tfaRequired = false;
        _tfaUsername = "";
        _tfaPassword = "";
        _storage.AddOrUpdateAccount(account);
        try
        {
            var authFile = Path.Combine(FilesDir?.AbsolutePath ?? "", "auth.json");
            var json = "{\"token\":\"" + account.Token + "\",\"username\":\"" + account.Username + "\",\"userId\":\"" + account.UserId + "\"}";
            File.WriteAllText(authFile, json);
        }
        catch
        {
        }

        Toast.MakeText(this, "Logged in as " + account.Username, ToastLength.Short)?.Show();
        UpdateAccountButton();
        _ = RefreshHubServersAsync();
    }

    private void ShowLoginError(string? message)
    {
        if (_loginError == null)
            return;

        _loginError.Text = string.IsNullOrWhiteSpace(message) ? "Login failed." : message;
        _loginError.Visibility = ViewStates.Visible;
    }

    private View BuildTfaPage()
    {
        var page = new LinearLayout(this) { Orientation = Orientation.Vertical };
        page.SetGravity(GravityFlags.CenterHorizontal);
        page.SetPadding(Dp(16), Dp(10), Dp(16), Dp(6));

        var title = new TextView(this) { Text = "Two Factor Authentication", TextSize = 20f };
        title.SetTypeface(Typeface.Default, TypefaceStyle.Bold);
        title.SetTextColor(LauncherTheme.NanoGold);
        title.Gravity = GravityFlags.Center;
        page.AddView(title, new LinearLayout.LayoutParams(ViewGroup.LayoutParams.MatchParent, ViewGroup.LayoutParams.WrapContent));

        var message = new TextView(this)
        {
            Text = "Enter the 6 digit code from your authenticator app.",
            TextSize = 11f
        };
        message.SetTextColor(LauncherTheme.SubText);
        message.Gravity = GravityFlags.Center;
        message.SetPadding(Dp(20), Dp(6), Dp(20), 0);
        page.AddView(message);

        var form = new LinearLayout(this) { Orientation = Orientation.Vertical };
        form.SetGravity(GravityFlags.CenterHorizontal);
        page.AddView(form, new LinearLayout.LayoutParams(ViewGroup.LayoutParams.MatchParent, 0, 1f));

        _tfaCode = new EditText(this) { Hint = "000000" };
        _tfaCode.SetSingleLine(true);
        _tfaCode.InputType = global::Android.Text.InputTypes.ClassNumber;
        _tfaCode.SetFilters(new global::Android.Text.IInputFilter[] { new global::Android.Text.InputFilterLengthFilter(6) });
        _tfaCode.TextSize = 16f;
        _tfaCode.Gravity = GravityFlags.Center;
        _tfaCode.SetTextColor(LauncherTheme.Foreground);
        _tfaCode.SetHintTextColor(LauncherTheme.SubText);
        _tfaCode.Background = LauncherTheme.CreateBox(LauncherTheme.PopupBackground, 0, LauncherTheme.StripeEdge, 1);
        _tfaCode.SetPadding(Dp(8), 0, Dp(8), 0);
        form.AddView(_tfaCode, new LinearLayout.LayoutParams(Dp(300), Dp(36)) { TopMargin = Dp(10) });

        _tfaError = new TextView(this) { TextSize = 11f };
        _tfaError.SetTextColor(Color.ParseColor("#FF6B6B"));
        _tfaError.Gravity = GravityFlags.Center;
        _tfaError.Visibility = ViewStates.Gone;
        form.AddView(_tfaError, new LinearLayout.LayoutParams(Dp(300), ViewGroup.LayoutParams.WrapContent) { TopMargin = Dp(4) });

        var buttons = new LinearLayout(this) { Orientation = Orientation.Horizontal };
        buttons.SetGravity(GravityFlags.Center);
        _tfaConfirm = CreateAngleButton("Confirm", 32, 110);
        _tfaConfirm.Click += async (_, _) => await ConfirmTfaAsync();
        buttons.AddView(_tfaConfirm);
        var cancel = CreateAngleButton("Cancel", 32, 110);
        cancel.Click += (_, _) =>
        {
            _tfaRequired = false;
            RebuildShell();
        };
        buttons.AddView(cancel);
        form.AddView(buttons, new LinearLayout.LayoutParams(ViewGroup.LayoutParams.WrapContent, ViewGroup.LayoutParams.WrapContent) { TopMargin = Dp(8) });

        var recovery = CreateAngleButton("Recovery code", 32, 150);
        recovery.Click += (_, _) => OpenUrl("https://account.spacestation14.com/Identity/Account/Manage");
        form.AddView(recovery, new LinearLayout.LayoutParams(Dp(150), Dp(32)) { TopMargin = Dp(6) });

        return page;
    }

    private async Task ConfirmTfaAsync()
    {
        if (_tfaBusy || _tfaCode == null)
            return;

        var code = (_tfaCode.Text ?? "").Trim();
        if (code.Length != 6)
        {
            ShowTfaError("Enter the 6 digit code.");
            return;
        }

        if (_tfaError != null)
            _tfaError.Visibility = ViewStates.Gone;

        _tfaBusy = true;
        if (_tfaConfirm != null)
            _tfaConfirm.Enabled = false;
        try
        {
            var result = await AuthenticateAccountAsync(_tfaUsername, _tfaPassword, code);
            if (result.Success && result.Account != null)
            {
                SaveAccount(result.Account);
                return;
            }

            ShowTfaError(result.TfaRequired
                ? "Two factor authentication required."
                : result.ErrorMessage ?? "Login failed.");
        }
        finally
        {
            _tfaBusy = false;
            if (_tfaConfirm != null)
                _tfaConfirm.Enabled = true;
        }
    }

    private void ShowTfaError(string? message)
    {
        if (_tfaError == null)
            return;

        _tfaError.Text = string.IsNullOrWhiteSpace(message) ? "Login failed." : message;
        _tfaError.Visibility = ViewStates.Visible;
    }

    private View BuildHomePage()
    {
        var page = new LinearLayout(this) { Orientation = Orientation.Vertical };

        var heading = new LinearLayout(this) { Orientation = Orientation.Horizontal };
        heading.SetGravity(GravityFlags.CenterVertical);
        heading.SetPadding(Dp(4), Dp(2), Dp(4), Dp(2));
        var title = new TextView(this) { Text = "Favorite Servers", TextSize = 20f };
        title.SetTypeface(Typeface.Default, TypefaceStyle.Bold);
        title.SetTextColor(LauncherTheme.NanoGold);
        heading.AddView(title);
        var spacer = new Space(this);
        heading.AddView(spacer, new LinearLayout.LayoutParams(0, 1, 1f));
        var refresh = CreateAngleButton("Refresh", 26, 80);
        refresh.Click += async (_, _) => await RefreshHubServersAsync();
        heading.AddView(refresh);
        page.AddView(heading);

        var sep = new View(this);
        sep.SetBackgroundColor(LauncherTheme.Separator);
        page.AddView(sep, new LinearLayout.LayoutParams(ViewGroup.LayoutParams.MatchParent, 1));

        var scroll = new ScrollView(this);
        _listLayout = new LinearLayout(this) { Orientation = Orientation.Vertical };
        _listLayout.SetBackgroundColor(LauncherTheme.RowBase);
        scroll.AddView(_listLayout);
        page.AddView(scroll, new LinearLayout.LayoutParams(ViewGroup.LayoutParams.MatchParent, 0, 1f));

        var footer = new LinearLayout(this) { Orientation = Orientation.Horizontal };
        footer.SetGravity(GravityFlags.CenterHorizontal);
        footer.SetPadding(0, Dp(4), 0, Dp(2));
        var direct = CreateAngleButton("Direct Connect", 30, 130);
        direct.Click += (_, _) => ShowDirectConnectDialog();
        footer.AddView(direct, new LinearLayout.LayoutParams(Dp(130), Dp(30)) { RightMargin = Dp(6) });
        page.AddView(footer);

        RenderServerList();
        return page;
    }

    private View BuildServersPage()
    {
        var page = new LinearLayout(this) { Orientation = Orientation.Vertical };

        var header = new LinearLayout(this) { Orientation = Orientation.Horizontal };
        header.SetGravity(GravityFlags.CenterVertical);
        var headerChevron = new Space(this);
        header.AddView(headerChevron, new LinearLayout.LayoutParams(Dp(24), ViewGroup.LayoutParams.MatchParent));
        header.AddView(CreateTableCell("Server Name", 1f, GravityFlags.Left, 10f, false));
        header.AddView(CreateTableHeaderSeparator());
        header.AddView(CreateTableCell("Time", 0f, GravityFlags.Center, 10f, false, Dp(108)));
        header.AddView(CreateTableHeaderSeparator());
        header.AddView(CreateTableCell("Players", 0f, GravityFlags.Center, 10f, false, Dp(128)));
        header.AddView(CreateTableHeaderSeparator());
        header.AddView(CreateTableCell("", 0f, GravityFlags.Center, 10f, false, Dp(84)));
        header.SetPadding(0, 0, Dp(30), 0);
        page.AddView(header, new LinearLayout.LayoutParams(ViewGroup.LayoutParams.MatchParent, Dp(24)));
        var headerLine = new View(this);
        headerLine.SetBackgroundColor(Color.Argb(170, 0x57, 0x5B, 0x7F));
        page.AddView(headerLine, new LinearLayout.LayoutParams(ViewGroup.LayoutParams.MatchParent, Dp(2)));

        var sep = new View(this);
        sep.SetBackgroundColor(LauncherTheme.Separator);
        page.AddView(sep, new LinearLayout.LayoutParams(ViewGroup.LayoutParams.MatchParent, 1));

        _filtersPanel = BuildFiltersPanel(Density);
        _filtersPanel.Visibility = _filtersVisible ? ViewStates.Visible : ViewStates.Gone;
        page.AddView(_filtersPanel);

        var scroll = new ScrollView(this);
        _listLayout = new LinearLayout(this) { Orientation = Orientation.Vertical };
        _listLayout.SetBackgroundColor(LauncherTheme.RowBase);
        scroll.AddView(_listLayout);
        page.AddView(scroll, new LinearLayout.LayoutParams(ViewGroup.LayoutParams.MatchParent, 0, 1f));

        var toolbarSep = new View(this);
        toolbarSep.SetBackgroundColor(LauncherTheme.Separator);
        page.AddView(toolbarSep, new LinearLayout.LayoutParams(ViewGroup.LayoutParams.MatchParent, 1));

        var toolbar = new LinearLayout(this) { Orientation = Orientation.Horizontal };
        toolbar.SetGravity(GravityFlags.CenterVertical);
        toolbar.SetPadding(Dp(4), 0, Dp(4), Dp(4));

        _searchInput = new EditText(this) { Hint = "Search For Servers..." };
        _searchInput.SetSingleLine(true);
        _searchInput.TextSize = 11f;
        _searchInput.SetTextColor(LauncherTheme.Foreground);
        _searchInput.SetHintTextColor(LauncherTheme.SubText);
        _searchInput.Background = LauncherTheme.CreateBox(LauncherTheme.PopupBackground, 0, LauncherTheme.StripeEdge, 1);
        _searchInput.SetPadding(Dp(8), 0, Dp(8), 0);
        _searchInput.TextChanged += (_, e) =>
        {
            _searchFilter = e?.Text?.ToString() ?? string.Empty;
            RenderServerList();
        };
        toolbar.AddView(_searchInput, new LinearLayout.LayoutParams(0, Dp(30), 1f) { RightMargin = Dp(8) });

        _filterBtn = new Button(this) { TextSize = 12f };
        _filterBtn.SetTextColor(LauncherTheme.Foreground);
        _filterBtn.SetPadding(Dp(12), 0, Dp(12), 0);
        _filterBtn.Background = CreateAngleButtonBackground(Dp(10), AngleSide.Both);
        _filterBtn.Click += (_, _) =>
        {
            _filtersVisible = !_filtersVisible;
            if (_filtersPanel != null)
                _filtersPanel.Visibility = _filtersVisible ? ViewStates.Visible : ViewStates.Gone;
        };
        toolbar.AddView(_filterBtn, new LinearLayout.LayoutParams(ViewGroup.LayoutParams.WrapContent, Dp(30)));

        var refresh = new Button(this) { Text = "↻", TextSize = 20f };
        refresh.SetTextColor(LauncherTheme.Foreground);
        refresh.SetPadding(0, 0, 0, 0);
        refresh.Background = CreateAngleButtonBackground(Dp(10), AngleSide.Both);
        refresh.Click += async (_, _) => await RefreshHubServersAsync();
        toolbar.AddView(refresh, new LinearLayout.LayoutParams(Dp(48), Dp(30)) { LeftMargin = Dp(2) });

        page.AddView(toolbar);

        UpdateFilterButtonState();
        RenderServerList();
        return page;
    }

    private View BuildOptionsPage()
    {
        var page = new LinearLayout(this) { Orientation = Orientation.Vertical };

        var title = new TextView(this) { Text = "Options", TextSize = 20f };
        title.SetTypeface(Typeface.Default, TypefaceStyle.Bold);
        title.SetTextColor(LauncherTheme.NanoGold);
        title.SetPadding(Dp(4), Dp(2), Dp(4), Dp(2));
        page.AddView(title);

        var scroll = new ScrollView(this);
        var list = new LinearLayout(this) { Orientation = Orientation.Vertical };
        list.SetPadding(Dp(4), 0, Dp(4), Dp(4));
        scroll.AddView(list);
        page.AddView(scroll, new LinearLayout.LayoutParams(ViewGroup.LayoutParams.MatchParent, 0, 1f));

        var prefs = GetSharedPreferences("ss14_mobile_config", FileCreationMode.Private)!;

        AddCycleRow(list, "Controls opacity", prefs, "mobile_opacity", 0.6f, new[] { 0.4f, 0.5f, 0.6f, 0.75f, 0.9f }, "%");
        AddChoiceRow(list, "Display resolution", prefs, "display_resolution", "native", new[] { "native", "1080p", "900p", "720p" });
        AddChoiceRow(list, "Graphics", prefs, "graphics_quality", "Medium", new[] { "Low", "Medium", "High" });
        // Android mobile lighting: "shadows" is the old per-light shadow maps path,
        // keep it as a fallback in case the lightmap looks wrong on a device.
        AddChoiceRow(list, "Lighting", prefs, "lighting_mode", "lightmap", new[] { "lightmap", "shadows" });
        AddChoiceRow(list, "Screen frame", prefs, "frame_mode", MobileSafeArea.DefaultMode, new[]
        {
            MobileSafeArea.FrameAuto, MobileSafeArea.FrameLeft, MobileSafeArea.FrameRight,
            MobileSafeArea.FrameTop, MobileSafeArea.FrameBottom, MobileSafeArea.FrameAllSides,
            MobileSafeArea.FrameOff,
        });

        AddActionRow(list, "Customize controls", () => ShowControlsEditor());

        AddActionRow(list, "Account settings", () => OnAccountButtonClicked());
        AddActionRow(list, "Clear content cache", () => ContentCacheManager.ClearAllCache(this, _storage));
        AddActionRow(list, "Logs", () => ShowCrashReporterDialog());
        AddActionRow(list, "Check for updates", () => _ = CheckForUpdatesAsync(true));
        AddActionRow(list, "Website", () => OpenUrl("https://spacestation14.com/"));
        AddActionRow(list, "GitHub", () => OpenUrl("https://github.com/ministation/space-station-14-client"));

        return page;
    }

    private void AddCycleRow(LinearLayout list, string label, ISharedPreferences prefs, string key, float fallback, float[] values, string suffix)
    {
        var row = new LinearLayout(this) { Orientation = Orientation.Horizontal };
        row.SetGravity(GravityFlags.CenterVertical);
        row.SetPadding(Dp(4), Dp(2), Dp(4), Dp(2));
        var text = new TextView(this) { Text = label, TextSize = 12f };
        text.SetTextColor(LauncherTheme.Foreground);
        row.AddView(text, new LinearLayout.LayoutParams(0, Dp(30), 1f));

        var current = prefs.GetFloat(key, fallback);
        var button = new Button(this) { TextSize = 11f };
        button.SetTextColor(LauncherTheme.Foreground);
        button.Background = CreateAngleButtonBackground(Dp(10), AngleSide.Both);
        button.SetPadding(Dp(10), 0, Dp(10), 0);
        void UpdateLabel()
        {
            button.Text = ((int)Math.Round(current * 100)) + suffix;
        }
        UpdateLabel();
        button.Click += (_, _) =>
        {
            var index = Array.FindIndex(values, v => Math.Abs(v - current) < 0.001f);
            index = (index + 1) % values.Length;
            current = values[index];
            prefs.Edit()?.PutFloat(key, current)?.Apply();
            UpdateLabel();
        };
        row.AddView(button, new LinearLayout.LayoutParams(Dp(76), Dp(30)));
        list.AddView(row);
    }

    private void AddChoiceRow(LinearLayout list, string label, ISharedPreferences prefs, string key, string fallback, string[] values)
    {
        var row = new LinearLayout(this) { Orientation = Orientation.Horizontal };
        row.SetGravity(GravityFlags.CenterVertical);
        row.SetPadding(Dp(4), Dp(2), Dp(4), Dp(2));
        var text = new TextView(this) { Text = label, TextSize = 12f };
        text.SetTextColor(LauncherTheme.Foreground);
        row.AddView(text, new LinearLayout.LayoutParams(0, Dp(30), 1f));

        var current = prefs.GetString(key, fallback) ?? fallback;
        if (Array.IndexOf(values, current) < 0)
            current = fallback;
        var button = new Button(this) { TextSize = 11f };
        button.SetTextColor(LauncherTheme.Foreground);
        button.Background = CreateAngleButtonBackground(Dp(10), AngleSide.Both);
        button.SetPadding(Dp(10), 0, Dp(10), 0);
        void UpdateLabel() => button.Text = current;
        UpdateLabel();
        button.Click += (_, _) =>
        {
            var index = Array.IndexOf(values, current);
            index = (index + 1) % values.Length;
            current = values[index];
            prefs.Edit()?.PutString(key, current)?.Apply();
            UpdateLabel();
        };
        row.AddView(button, new LinearLayout.LayoutParams(Dp(90), Dp(30)));
        list.AddView(row);
    }

    private static void ApplyGraphicsExtras(Intent intent, ISharedPreferences prefs)
    {
        var quality = prefs.GetString("graphics_quality", "Medium") ?? "Medium";
        string shadows, lights, occluders, lightRes, budget, bleed;
        switch (quality)
        {
            // Lights and shadow-casting lights are kept equal on purpose: a light
            // without a shadow map shines through walls.
            case "Low":
                shadows = "16"; lights = "16"; occluders = "64"; lightRes = "0.10";
                budget = "128"; bleed = "false";
                break;
            case "High":
                shadows = "48"; lights = "48"; occluders = "96"; lightRes = "0.15";
                budget = "512"; bleed = "false";
                break;
            default:
                shadows = "32"; lights = "32"; occluders = "80"; lightRes = "0.12";
                budget = "256"; bleed = "false";
                break;
        }

        // 120 fps cap: enough for 120 Hz panels, still a ceiling so an uncapped
        // renderer cannot burn the frame budget the phone needs to stay warm.
        intent.PutExtra("cvar_build_display.max_fps", "120");
        intent.PutExtra("cvar_build_display.vsync", "false");
        intent.PutExtra("cvar_build_light.max_shadowcasting_lights", shadows);
        intent.PutExtra("cvar_build_light.max_light_count", lights);
        intent.PutExtra("cvar_build_light.max_occluder_count", occluders);
        intent.PutExtra("cvar_build_light.resolution_scale", lightRes);
        intent.PutExtra("cvar_build_light.wall_bleed", bleed);

        // Lightmap is the mobile path: no per-light shadow maps, one occluded
        // lightmap drawn as a single quad.
        var lighting = prefs.GetString("lighting_mode", "lightmap") ?? "lightmap";
        intent.PutExtra("cvar_build_light.mobile_lightmap", lighting == "shadows" ? "false" : "true");
        intent.PutExtra("cvar_build_particles.quality", "0");
        intent.PutExtra("cvar_build_particles.global_budget", budget);
    }

    private void AddActionRow(LinearLayout list, string label, Action action)
    {
        // The row itself is the target: a repeated "Open" button on every line
        // made the list look like a wall of buttons.
        var row = new LinearLayout(this) { Orientation = Orientation.Horizontal };
        row.SetGravity(GravityFlags.CenterVertical);
        row.SetPadding(Dp(4), 0, Dp(4), 0);
        row.Clickable = true;
        row.Click += (_, _) => action();
        var text = new TextView(this) { Text = label, TextSize = 12f };
        text.SetTextColor(LauncherTheme.Foreground);
        row.AddView(text, new LinearLayout.LayoutParams(0, Dp(34), 1f));
        var chevron = new TextView(this) { Text = ">", TextSize = 12f };
        chevron.SetTextColor(LauncherTheme.SubText);
        chevron.Gravity = GravityFlags.CenterVertical;
        row.AddView(chevron, new LinearLayout.LayoutParams(Dp(18), Dp(34)));
        list.AddView(row);
    }

    private void ShowDirectConnectDialog()
    {
        var input = new EditText(this) { Hint = "ss14://host:port" };
        input.SetTextColor(LauncherTheme.Foreground);
        input.SetHintTextColor(LauncherTheme.SubText);
        input.Background = LauncherTheme.CreateBox(LauncherTheme.PopupBackground, 0, LauncherTheme.StripeEdge, 1);
        input.SetPadding(Dp(8), Dp(4), Dp(8), Dp(4));

        var dialog = new AlertDialog.Builder(this);
        dialog.SetTitle("Direct Connect");
        dialog.SetView(input);
        dialog.SetPositiveButton("Connect", (_, _) =>
        {
            var address = input.Text?.Trim();
            if (!string.IsNullOrEmpty(address))
                _ = StartConnectingAsync(address);
        });
        dialog.SetNegativeButton("Cancel", (_, _) => { });
        dialog.Show();
    }

    private TextView CreateTableCell(string text, float weight, GravityFlags gravity, float textSize, bool numeric, int width = 0)
    {
        var view = new TextView(this) { Text = text, TextSize = textSize };
        view.SetTextColor(numeric ? LauncherTheme.Foreground : LauncherTheme.SubText);
        view.Gravity = gravity | GravityFlags.CenterVertical;
        view.SetPadding(Dp(6), 0, Dp(6), 0);
        view.SetSingleLine(true);
        view.Ellipsize = global::Android.Text.TextUtils.TruncateAt.End;
        if (numeric)
            view.Typeface = Typeface.Monospace;
        var lp = width > 0
            ? new LinearLayout.LayoutParams(width, ViewGroup.LayoutParams.MatchParent)
            : new LinearLayout.LayoutParams(0, ViewGroup.LayoutParams.MatchParent, weight);
        view.LayoutParameters = lp;
        return view;
    }

    private async Task RefreshHubServersAsync()
    {
        try
        {
            var list = await _hubClient.FetchAllServersAsync(_storage.Data.HubUrls);
            RunOnUiThread(() =>
            {
                _servers = list;
                RenderServerList();
            });
        }
        catch (Exception ex)
        {
            RunOnUiThread(() => Toast.MakeText(this, "Failed to load servers: " + ex.Message, ToastLength.Short)?.Show());
        }
    }
    private void ShowKeyboard(View view)
    {
        try
        {
            var imm = (global::Android.Views.InputMethods.InputMethodManager?)GetSystemService(global::Android.Content.Context.InputMethodService);
            imm?.ShowSoftInput(view, global::Android.Views.InputMethods.ShowFlags.Implicit);
        }
        catch
        {
        }
    }


    private static IEnumerable<string> HubUrlsWithFallback()
    {
        yield return "https://hub.spacestation14.com/";
        yield return "https://hub.fallback.spacestation14.com/";
    }

    private void RenderServerList()
    {
        if (_listLayout == null)
            return;

        _listLayout.RemoveAllViews();
        var density = Density;

        IEnumerable<ServerListEntry> items = _servers;
        if (_activeTab == Tab.Home)
        {
            items = items.Where(s => _storage.IsFavorite(s.Address));
        }

        if (!string.IsNullOrWhiteSpace(_searchFilter))
        {
            var q = _searchFilter.Trim();
            items = items.Where(s =>
                (s.StatusData?.Name?.Contains(q, StringComparison.OrdinalIgnoreCase) ?? false) ||
                s.Address.Contains(q, StringComparison.OrdinalIgnoreCase) ||
                (s.StatusData?.Tags != null && s.StatusData.Tags.Any(t => t.Contains(q, StringComparison.OrdinalIgnoreCase))) ||
                (_serverInfoCache.TryGetValue(s.Address, out var inf) && (inf.Description?.Contains(q, StringComparison.OrdinalIgnoreCase) ?? false)));
        }

        if (_filterLanguage == "ru")
        {
            items = items.Where(s => s.StatusData?.Tags != null && s.StatusData.Tags.Any(t =>
                t.Equals("lang:ru", StringComparison.OrdinalIgnoreCase) ||
                t.StartsWith("lang:ru-", StringComparison.OrdinalIgnoreCase)));
        }
        else if (_filterLanguage == "en")
        {
            items = items.Where(s => s.StatusData?.Tags != null && s.StatusData.Tags.Any(t =>
                t.Equals("lang:en", StringComparison.OrdinalIgnoreCase) ||
                t.StartsWith("lang:en-", StringComparison.OrdinalIgnoreCase)));
        }

        if (_filterRegion == "eu")
        {
            items = items.Where(s => s.StatusData?.Tags != null && s.StatusData.Tags.Any(t =>
                t.StartsWith("region:eu", StringComparison.OrdinalIgnoreCase) ||
                t.Contains("ru_central", StringComparison.OrdinalIgnoreCase) ||
                t.Contains("luna", StringComparison.OrdinalIgnoreCase)));
        }
        else if (_filterRegion == "na")
        {
            items = items.Where(s => s.StatusData?.Tags != null && s.StatusData.Tags.Any(t =>
                t.StartsWith("region:na", StringComparison.OrdinalIgnoreCase)));
        }

        if (_filterHideEmpty)
            items = items.Where(s => (s.StatusData?.Players ?? 0) > 0);

        if (_filterHideFull)
        {
            items = items.Where(s =>
            {
                var p = s.StatusData?.Players ?? 0;
                var max = s.StatusData?.SoftMaxPlayers ?? 0;
                return max <= 0 || p < max;
            });
        }

        var sorted = items.OrderByDescending(s => s.StatusData?.Players ?? 0).ToList();
        if (_filterBtn != null && _activeTab == Tab.Servers)
            _filterBtn.Text = $"Filters ({sorted.Count} / {_servers.Count})";

        _renderedServerRows.Clear();
        _rowAlternates.Clear();

        if (sorted.Count == 0)
        {
            var empty = new TextView(this)
            {
                Text = _activeTab == Tab.Home
                    ? "No favorites yet. Add servers from the Servers tab."
                    : _servers.Count == 0 ? "Loading server list..." : "No servers match the filters.",
                TextSize = 12f
            };
            empty.SetTextColor(LauncherTheme.SubText);
            empty.SetPadding(Dp(12), Dp(12), Dp(12), Dp(12));
            _listLayout.AddView(empty);
            return;
        }

        var visible = Math.Min(_serverRenderLimit, sorted.Count);
        for (var i = 0; i < visible; i++)
            _listLayout.AddView(CreateServerTableRow(sorted[i], i % 2 == 1, density));

        if (sorted.Count > visible)
        {
            var more = new Button(this) { Text = "Show more (" + (sorted.Count - visible) + ")", TextSize = 10f };
            more.SetTextColor(LauncherTheme.Foreground);
            more.SetPadding(Dp(10), 0, Dp(10), 0);
            more.Background = CreateAngleButtonBackground(Dp(10), AngleSide.Both);
            more.Click += (_, _) =>
            {
                _serverRenderLimit += 50;
                RenderServerList();
            };
            _listLayout.AddView(more, new LinearLayout.LayoutParams(Dp(180), Dp(30))
            {
                Gravity = GravityFlags.Left,
                LeftMargin = Dp(16),
                TopMargin = Dp(6),
                BottomMargin = Dp(8)
            });
        }
    }

    private View CreateServerTableRow(ServerListEntry server, bool alternate, float density)
    {
        var rowColor = alternate ? LauncherTheme.RowAlternate : LauncherTheme.RowBase;
        var container = new LinearLayout(this) { Orientation = Orientation.Vertical };
        container.SetBackgroundColor(rowColor);
        container.LayoutParameters = new LinearLayout.LayoutParams(ViewGroup.LayoutParams.MatchParent, ViewGroup.LayoutParams.WrapContent);

        var isExpanded = string.Equals(_expandedServerAddress, server.Address, StringComparison.OrdinalIgnoreCase);

        var header = new LinearLayout(this) { Orientation = Orientation.Horizontal };
        header.SetGravity(GravityFlags.CenterVertical);
        header.SetPadding(0, 0, Dp(30), 0);
        header.LayoutParameters = new LinearLayout.LayoutParams(ViewGroup.LayoutParams.MatchParent, Dp(30));

        var chevron = new TextView(this) { Text = isExpanded ? "▾" : "▸", TextSize = 9f };
        chevron.SetTextColor(isExpanded ? LauncherTheme.Foreground : LauncherTheme.ForegroundMuted);
        chevron.Gravity = GravityFlags.Center;
        chevron.Clickable = true;
        chevron.Click += (_, _) => ToggleExpandServer(server.Address);
        header.AddView(chevron, new LinearLayout.LayoutParams(Dp(24), ViewGroup.LayoutParams.MatchParent));

        var name = new TextView(this) { Text = server.StatusData?.Name ?? server.Address, TextSize = 11f };
        name.SetTextColor(LauncherTheme.Foreground);
        name.SetSingleLine(true);
        name.Ellipsize = global::Android.Text.TextUtils.TruncateAt.End;
        name.Gravity = GravityFlags.CenterVertical;
        name.SetPadding(Dp(6), 0, Dp(6), 0);
        name.Clickable = true;
        name.Click += (_, _) => ToggleExpandServer(server.Address);
        header.AddView(name, new LinearLayout.LayoutParams(0, ViewGroup.LayoutParams.MatchParent, 1f));

        header.AddView(CreateListSeparator());

        var time = new TextView(this) { Text = FormatRoundTime(server.StatusData), TextSize = 11f };
        time.SetTextColor(LauncherTheme.Foreground);
        time.SetTypeface(Typeface.Default, TypefaceStyle.Bold);
        time.Gravity = GravityFlags.Right | GravityFlags.CenterVertical;
        time.SetPadding(0, 0, Dp(8), 0);
        time.SetSingleLine(true);
        header.AddView(time, new LinearLayout.LayoutParams(Dp(108), ViewGroup.LayoutParams.MatchParent));

        header.AddView(CreateListSeparator());

        var players = server.StatusData?.Players ?? 0;
        var maxPlayers = server.StatusData?.SoftMaxPlayers ?? 0;
        var playersText = new TextView(this) { Text = players + " / " + maxPlayers, TextSize = 10.5f };
        playersText.SetTextColor(LauncherTheme.Foreground);
        playersText.Typeface = Typeface.Monospace;
        playersText.Gravity = GravityFlags.Center;
        playersText.SetSingleLine(true);
        header.AddView(playersText, new LinearLayout.LayoutParams(Dp(128), ViewGroup.LayoutParams.MatchParent));

        header.AddView(CreateListSeparator());

        var connect = new Button(this) { Text = "Connect", TextSize = 10.5f };
        connect.SetTextColor(LauncherTheme.Foreground);
        connect.SetPadding(Dp(6), 0, Dp(6), 0);
        connect.Background = CreateAngleButtonBackground(Dp(8), AngleSide.Both);
        connect.Click += async (_, _) => await StartConnectingAsync(server.Address);
        header.AddView(connect, new LinearLayout.LayoutParams(Dp(84), Dp(26)));

        container.AddView(header);
        _renderedServerRows[server.Address] = (container, chevron, server);
        _rowAlternates[server.Address] = alternate;

        if (isExpanded)
            container.AddView(BuildServerDetailsView(server, density));

        return container;
    }

    private View CreateTableHeaderSeparator()
    {
        var sep = new View(this) { LayoutParameters = new LinearLayout.LayoutParams(Dp(2), ViewGroup.LayoutParams.MatchParent) };
        sep.SetBackgroundColor(Color.Argb(170, 0x57, 0x5B, 0x7F));
        return sep;
    }

    private View CreateListSeparator()
    {
        var sep = new View(this) { LayoutParameters = new LinearLayout.LayoutParams(Dp(2), ViewGroup.LayoutParams.MatchParent) };
        sep.SetBackgroundColor(Color.Argb(170, 0x57, 0x5B, 0x7F));
        return sep;
    }

    private View BuildServerDetailsView(ServerListEntry server, float density)
    {
        var alternate = _rowAlternates.TryGetValue(server.Address, out var alt) && alt;
        var detailsBox = new LinearLayout(this) { Orientation = Orientation.Vertical };
        detailsBox.SetBackgroundColor(alternate ? LauncherTheme.RowAlternate : LauncherTheme.RowBase);
        detailsBox.SetPadding(Dp(22), 0, Dp(8), Dp(6));

        if (!_serverInfoCache.TryGetValue(server.Address, out var info))
        {
            var loading = new TextView(this) { Text = "Loading server details...", TextSize = 11f };
            loading.SetTextColor(LauncherTheme.SubText);
            loading.SetPadding(0, Dp(2), 0, Dp(6));
            detailsBox.AddView(loading);
            return detailsBox;
        }

        var description = new TextView(this) { TextSize = 11f };
        description.SetTextColor(LauncherTheme.Foreground);
        description.SetTextIsSelectable(true);
        description.SetPadding(Dp(4), Dp(2), Dp(4), Dp(6));
        description.Text = string.IsNullOrWhiteSpace(info.Description)
            ? "No description provided."
            : info.Description.Trim();
        detailsBox.AddView(description);

        var meta = new System.Text.StringBuilder();
        if (!string.IsNullOrEmpty(server.StatusData?.Map)) meta.Append("Map: ").Append(server.StatusData.Map);
        if (!string.IsNullOrEmpty(server.StatusData?.Preset))
        {
            if (meta.Length > 0) meta.Append("   ");
            meta.Append("Mode: ").Append(server.StatusData.Preset);
        }

        if (meta.Length > 0)
        {
            var metaText = new TextView(this) { Text = meta.ToString(), TextSize = 10f };
            metaText.SetTextColor(LauncherTheme.SubText);
            metaText.SetPadding(Dp(4), 0, Dp(4), Dp(6));
            detailsBox.AddView(metaText);
        }

        var footer = new LinearLayout(this) { Orientation = Orientation.Horizontal };
        footer.SetGravity(GravityFlags.CenterVertical);

        var linksScroll = new HorizontalScrollView(this);
        linksScroll.HorizontalScrollBarEnabled = false;
        var linksRow = new LinearLayout(this) { Orientation = Orientation.Horizontal };
        if (info.Links != null)
        {
            foreach (var link in info.Links)
            {
                if (string.IsNullOrEmpty(link.Url))
                    continue;

                var linkButton = new Button(this) { Text = link.Name ?? link.Icon ?? "Link", TextSize = 10f };
                linkButton.SetTextColor(LauncherTheme.Foreground);
                linkButton.SetPadding(Dp(10), 0, Dp(10), 0);
                linkButton.Background = CreateAngleButtonBackground(Dp(10), AngleSide.Both);
                var url = link.Url;
                linkButton.Click += (_, _) => OpenUrl(url);
                linksRow.AddView(linkButton, new LinearLayout.LayoutParams(ViewGroup.LayoutParams.WrapContent, Dp(26)) { RightMargin = Dp(6) });
            }
        }
        linksScroll.AddView(linksRow);
        footer.AddView(linksScroll, new LinearLayout.LayoutParams(0, Dp(30), 1f));

        var isFav = _storage.IsFavorite(server.Address);
        var favorite = new Button(this) { Text = isFav ? "★ Remove favorite" : "☆ Add favorite", TextSize = 10f };
        favorite.SetTextColor(isFav ? LauncherTheme.NanoGold : LauncherTheme.Foreground);
        favorite.SetPadding(Dp(10), 0, Dp(10), 0);
        favorite.Background = CreateAngleButtonBackground(Dp(10), AngleSide.OpenRight);
        favorite.Click += (_, _) =>
        {
            _storage.ToggleFavorite(server.Address);
            var nowFav = _storage.IsFavorite(server.Address);
            favorite.Text = nowFav ? "★ Remove favorite" : "☆ Add favorite";
            favorite.SetTextColor(nowFav ? LauncherTheme.NanoGold : LauncherTheme.Foreground);
            if (_activeTab == Tab.Home)
                RenderServerList();
        };
        footer.AddView(favorite, new LinearLayout.LayoutParams(ViewGroup.LayoutParams.WrapContent, Dp(28)) { LeftMargin = Dp(6) });

        detailsBox.AddView(footer);
        return detailsBox;
    }

    private void ToggleExpandServer(string address)
    {
        var density = Resources?.DisplayMetrics?.Density ?? 1f;

        // If another server was previously expanded, collapse it in-place
        if (!string.IsNullOrEmpty(_expandedServerAddress) && 
            !string.Equals(_expandedServerAddress, address, StringComparison.OrdinalIgnoreCase))
        {
            if (_renderedServerRows.TryGetValue(_expandedServerAddress, out var prev))
            {
                prev.Chevron.Text = "▸";
                prev.Chevron.SetTextColor(LauncherTheme.ForegroundMuted);
                if (prev.Container.ChildCount > 1)
                {
                    prev.Container.RemoveViewAt(1);
                }
            }
        }

        if (string.Equals(_expandedServerAddress, address, StringComparison.OrdinalIgnoreCase))
        {
            _expandedServerAddress = null;
            if (_renderedServerRows.TryGetValue(address, out var cur))
            {
                cur.Chevron.Text = "▸";
                cur.Chevron.SetTextColor(LauncherTheme.ForegroundMuted);
                if (cur.Container.ChildCount > 1)
                {
                    cur.Container.RemoveViewAt(1);
                }
            }
            return;
        }

        _expandedServerAddress = address;
        if (_renderedServerRows.TryGetValue(address, out var entry))
        {
            entry.Chevron.Text = "▾";
            entry.Chevron.SetTextColor(LauncherTheme.NanoGold);
            if (entry.Container.ChildCount > 1)
            {
                entry.Container.RemoveViewAt(1);
            }
            var detailsBox = BuildServerDetailsView(entry.Server, density);
            entry.Container.AddView(detailsBox);
        }

        if (!_serverInfoCache.ContainsKey(address) && !_loadingServerInfo.Contains(address))
        {
            _loadingServerInfo.Add(address);
            Task.Run(async () =>
            {
                try
                {
                    var info = await _hubClient.FetchServerInfoAsync(address);
                    if (info != null)
                    {
                        _serverInfoCache[address] = info;
                    }
                }
                catch { }
                finally
                {
                    _loadingServerInfo.Remove(address);
                    RunOnUiThread(() =>
                    {
                        if (string.Equals(_expandedServerAddress, address, StringComparison.OrdinalIgnoreCase))
                        {
                            if (_renderedServerRows.TryGetValue(address, out var e))
                            {
                                if (e.Container.ChildCount > 1)
                                {
                                    e.Container.RemoveViewAt(1);
                                }
                                var updatedBox = BuildServerDetailsView(e.Server, density);
                                e.Container.AddView(updatedBox);
                            }
                        }
                    });
                }
            });
        }
    }

    private string FormatRoundTime(ServerStatus? status)
    {
        if (status == null) return "—";
        if (status.RunLevel == 0) return "Lobby";
        if (status.RunLevel == 2) return "Ending";

        if (!string.IsNullOrEmpty(status.RoundStartTime) &&
            DateTime.TryParse(status.RoundStartTime, null, System.Globalization.DateTimeStyles.RoundtripKind, out var startTime))
        {
            var elapsed = DateTime.UtcNow - startTime.ToUniversalTime();
            if (elapsed.TotalMinutes < 0) return "0M";
            if (elapsed.TotalHours < 1)
                return $"{Math.Max(0, (int)elapsed.TotalMinutes)}M";
            return $"{(int)elapsed.TotalHours}H {elapsed.Minutes:D2}M";
        }

        return status.RunLevel == 1 ? "In-Round" : "Online";
    }

    private LinearLayout BuildFiltersPanel(float density)
    {
        var panel = new LinearLayout(this)
        {
            Orientation = Orientation.Vertical
        };
        panel.SetBackgroundColor(LauncherTheme.PopupBackground);
        panel.SetPadding((int)(12 * density), (int)(8 * density), (int)(12 * density), (int)(8 * density));
        panel.LayoutParameters = new LinearLayout.LayoutParams(ViewGroup.LayoutParams.MatchParent, ViewGroup.LayoutParams.WrapContent);

        // Language row
        var langRow = new LinearLayout(this) { Orientation = Orientation.Horizontal };
        langRow.SetPadding(0, 0, 0, (int)(6 * density));
        var langLabel = new TextView(this)
        {
            Text = "Language:",
            TextSize = 11,
            Typeface = Typeface.DefaultBold
        };
        langLabel.SetTextColor(LauncherTheme.NanoGold);
        langLabel.LayoutParameters = new LinearLayout.LayoutParams((int)(75 * density), ViewGroup.LayoutParams.WrapContent);
        langRow.AddView(langLabel);

        langRow.AddView(CreateFilterChip("All", _filterLanguage == "all", density, () => { _filterLanguage = "all"; ApplyFilterChange(); }));
        langRow.AddView(CreateFilterChip("Russian (RU)", _filterLanguage == "ru", density, () => { _filterLanguage = "ru"; ApplyFilterChange(); }));
        langRow.AddView(CreateFilterChip("English (EN)", _filterLanguage == "en", density, () => { _filterLanguage = "en"; ApplyFilterChange(); }));
        panel.AddView(langRow);

        // Region row
        var regRow = new LinearLayout(this) { Orientation = Orientation.Horizontal };
        regRow.SetPadding(0, 0, 0, (int)(6 * density));
        var regLabel = new TextView(this)
        {
            Text = "Region:",
            TextSize = 11,
            Typeface = Typeface.DefaultBold
        };
        regLabel.SetTextColor(LauncherTheme.NanoGold);
        regLabel.LayoutParameters = new LinearLayout.LayoutParams((int)(75 * density), ViewGroup.LayoutParams.WrapContent);
        regRow.AddView(regLabel);

        regRow.AddView(CreateFilterChip("All", _filterRegion == "all", density, () => { _filterRegion = "all"; ApplyFilterChange(); }));
        regRow.AddView(CreateFilterChip("Europe / RU", _filterRegion == "eu", density, () => { _filterRegion = "eu"; ApplyFilterChange(); }));
        regRow.AddView(CreateFilterChip("North America", _filterRegion == "na", density, () => { _filterRegion = "na"; ApplyFilterChange(); }));
        panel.AddView(regRow);

        // Options row (Hide empty, Hide full, Reset)
        var optRow = new LinearLayout(this) { Orientation = Orientation.Horizontal };
        var optLabel = new TextView(this)
        {
            Text = "Players:",
            TextSize = 11,
            Typeface = Typeface.DefaultBold
        };
        optLabel.SetTextColor(LauncherTheme.NanoGold);
        optLabel.LayoutParameters = new LinearLayout.LayoutParams((int)(75 * density), ViewGroup.LayoutParams.WrapContent);
        optRow.AddView(optLabel);

        optRow.AddView(CreateFilterChip("Hide Empty", _filterHideEmpty, density, () => { _filterHideEmpty = !_filterHideEmpty; ApplyFilterChange(); }));
        optRow.AddView(CreateFilterChip("Hide Full", _filterHideFull, density, () => { _filterHideFull = !_filterHideFull; ApplyFilterChange(); }));
        optRow.AddView(CreateFilterChip("Reset All", false, density, () =>
        {
            _filterLanguage = "all";
            _filterRegion = "all";
            _filterHideEmpty = false;
            _filterHideFull = false;
            ApplyFilterChange();
        }));
        panel.AddView(optRow);

        return panel;
    }

    private Button CreateFilterChip(string text, bool isActive, float density, Action onClick)
    {
        var btn = new Button(this)
        {
            Text = text,
            TextSize = 10,
            Typeface = isActive ? Typeface.DefaultBold : Typeface.Default
        };
        btn.Background = isActive
            ? LauncherTheme.CreateBox(LauncherTheme.ControlHigh, 3)
            : LauncherTheme.CreateBox(LauncherTheme.ControlMid, 3);
        btn.SetTextColor(isActive ? Color.White : LauncherTheme.Foreground);
        var lp = new LinearLayout.LayoutParams(ViewGroup.LayoutParams.WrapContent, (int)(26 * density));
        lp.SetMargins(0, 0, (int)(6 * density), 0);
        btn.LayoutParameters = lp;
        btn.SetPadding((int)(8 * density), 0, (int)(8 * density), 0);
        btn.Click += (_, _) => onClick();
        return btn;
    }

    private void ApplyFilterChange()
    {
        var density = Resources?.DisplayMetrics?.Density ?? 1f;
        if (_filtersPanel != null && _filtersPanel.Parent is ViewGroup parent)
        {
            var idx = parent.IndexOfChild(_filtersPanel);
            parent.RemoveViewAt(idx);
            _filtersPanel = BuildFiltersPanel(density);
            _filtersPanel.Visibility = _filtersVisible ? ViewStates.Visible : ViewStates.Gone;
            parent.AddView(_filtersPanel, idx);
        }
        UpdateFilterButtonState();
        RenderServerList();
    }

    private void UpdateFilterButtonState()
    {
        if (_filterBtn == null) return;
        var hasActiveFilters = _filterLanguage != "all" || _filterRegion != "all" || _filterHideEmpty || _filterHideFull;
        _filterBtn.SetTextColor(hasActiveFilters ? LauncherTheme.NanoGold : LauncherTheme.Foreground);
        _filterBtn.Background = hasActiveFilters
            ? LauncherTheme.CreateBox(LauncherTheme.ControlHigh, 3)
            : LauncherTheme.CreateBox(LauncherTheme.ControlMid, 3);
    }

    private string FormatRunLevel(int? runLevel)
    {
        return runLevel switch
        {
            0 => "Lobby",
            1 => "In-Round",
            2 => "Post-Round",
            _ => "Online"
        };
    }

    // [SolomonNumb1] hides on screen IME keyboard when closing login or search dialogs to prevent UI overlap
    private void HideKeyboard(View? view)
    {
        try
        {
            var imm = (global::Android.Views.InputMethods.InputMethodManager?)GetSystemService(global::Android.Content.Context.InputMethodService);
            var token = view?.WindowToken ?? Window?.DecorView?.WindowToken;
            if (imm != null && token != null)
            {
                imm.HideSoftInputFromWindow(token, 0);
            }
        }
        catch {}
    }

    private sealed class SafeInsetsListener : Java.Lang.Object, View.IOnApplyWindowInsetsListener
    {
        private readonly Action<View, WindowInsets> _apply;

        public SafeInsetsListener(Action<View, WindowInsets> apply) => _apply = apply;

        public WindowInsets OnApplyWindowInsets(View v, WindowInsets insets)
        {
            _apply(v, insets);
            return insets;
        }
    }

    private sealed class MobileButtonConfig
    {
        public string Id { get; set; } = "";
        public string Label { get; set; } = "";
        public string Key { get; set; } = "";
        public float Size { get; set; } = 50f;
        public bool IsDeleted { get; set; }
        public bool IsCustom { get; set; }
        public float X { get; set; }
        public float Y { get; set; }
    }

    private static List<MobileButtonConfig> GetDefaultButtonConfigs() => new()
    {
        new() { Id = "use", Label = "USE", Key = "Z", Size = 54f, X = 820, Y = 360 },
        new() { Id = "inter", Label = "INTER", Key = "E", Size = 48f, X = 760, Y = 385 },
        new() { Id = "sec", Label = "SEC", Key = "Secondary", Size = 46f, X = 705, Y = 405 },
        new() { Id = "hand", Label = "HAND", Key = "X", Size = 44f, X = 820, Y = 295 },
        new() { Id = "drop", Label = "DROP", Key = "Q", Size = 42f, X = 760, Y = 325 },
        new() { Id = "alt", Label = "ALT", Key = "Alt", Size = 42f, X = 705, Y = 345 },
        new() { Id = "pull", Label = "PULL", Key = "Control", Size = 44f, X = 60, Y = 240 },
        new() { Id = "exam", Label = "EXAM", Key = "Shift", Size = 44f, X = 116, Y = 240 },
        new() { Id = "esc", Label = "ESC", Key = "Escape", Size = 46f, X = 24, Y = 14 },
        new() { Id = "keyb", Label = "KEYB", Key = "Keyboard", Size = 46f, X = 900, Y = 14 },
    };

    private List<MobileButtonConfig> LoadAllButtonsFromPrefs()
    {
        var list = GetDefaultButtonConfigs();
        var prefs = GetSharedPreferences("ss14_mobile_config", FileCreationMode.Private);
        var layout = prefs?.GetString("mobile_layout", "") ?? "";
        if (string.IsNullOrWhiteSpace(layout))
            return list;

        var entries = layout.Split(';', StringSplitOptions.RemoveEmptyEntries);
        foreach (var entry in entries)
        {
            var parts = entry.Split(':');
            if (parts.Length < 2) continue;

            if (parts[0] == "del" && parts.Length >= 2)
            {
                var delId = parts[1];
                if (delId == "edit" || delId == "hide" || delId == "esc" || delId == "keyb") continue;
                var existing = list.Find(b => b.Id == delId);
                if (existing != null) existing.IsDeleted = true;
                continue;
            }

            if (parts[0] == "custom" && parts.Length >= 5)
            {
                var id = parts[1];
                var label = parts[2];
                var key = parts[3];
                var coords = parts[4].Split(',');
                float cx = 400, cy = 300, cSize = 50f;
                if (coords.Length == 2 && float.TryParse(coords[0], NumberStyles.Float, CultureInfo.InvariantCulture, out var px)) cx = px;
                if (coords.Length == 2 && float.TryParse(coords[1], NumberStyles.Float, CultureInfo.InvariantCulture, out var py)) cy = py;
                if (parts.Length >= 6 && float.TryParse(parts[5], NumberStyles.Float, CultureInfo.InvariantCulture, out var ps)) cSize = ps;

                var existing = list.Find(b => b.Id == id);
                if (existing == null)
                {
                    list.Add(new MobileButtonConfig
                    {
                        Id = id,
                        Label = label,
                        Key = key,
                        Size = cSize,
                        X = cx,
                        Y = cy,
                        IsCustom = true
                    });
                }
                else
                {
                    existing.Label = label;
                    existing.Key = key;
                    existing.Size = cSize;
                    existing.X = cx;
                    existing.Y = cy;
                }
                continue;
            }

            if (parts[0] == "btn" && parts.Length >= 5)
            {
                var id = parts[1];
                var label = parts[2];
                var key = parts[3];
                var coords = parts[4].Split(',');
                var existing = list.Find(b => b.Id == id);
                if (existing != null)
                {
                    existing.Label = label;
                    existing.Key = key;
                    if (coords.Length == 2 && float.TryParse(coords[0], NumberStyles.Float, CultureInfo.InvariantCulture, out var px)) existing.X = px;
                    if (coords.Length == 2 && float.TryParse(coords[1], NumberStyles.Float, CultureInfo.InvariantCulture, out var py)) existing.Y = py;
                    if (parts.Length >= 6 && float.TryParse(parts[5], NumberStyles.Float, CultureInfo.InvariantCulture, out var ps)) existing.Size = ps;
                }
                continue;
            }

            var btnId = parts[0];
            var bCoords = parts[1].Split(',');
            var bExisting = list.Find(b => b.Id == btnId);
            if (bExisting != null)
            {
                if (bCoords.Length == 2 && float.TryParse(bCoords[0], NumberStyles.Float, CultureInfo.InvariantCulture, out var px)) bExisting.X = px;
                if (bCoords.Length == 2 && float.TryParse(bCoords[1], NumberStyles.Float, CultureInfo.InvariantCulture, out var py)) bExisting.Y = py;
                if (parts.Length >= 3 && float.TryParse(parts[2], NumberStyles.Float, CultureInfo.InvariantCulture, out var ps)) bExisting.Size = ps;
            }
        }

        return list;
    }

    private void SaveAllButtonsToPrefs(List<MobileButtonConfig> buttons)
    {
        var parts = new List<string>();
        foreach (var b in buttons)
        {
            if (b.Id == "edit" || b.Id == "hide") continue;

            if (b.IsDeleted)
            {
                if (b.Id == "esc" || b.Id == "keyb") continue;
                parts.Add($"del:{b.Id}");
                continue;
            }

            if (b.IsCustom)
            {
                parts.Add($"custom:{b.Id}:{b.Label}:{b.Key}:{b.X.ToString("F1", CultureInfo.InvariantCulture)},{b.Y.ToString("F1", CultureInfo.InvariantCulture)}:{b.Size.ToString("F0", CultureInfo.InvariantCulture)}");
            }
            else
            {
                parts.Add($"btn:{b.Id}:{b.Label}:{b.Key}:{b.X.ToString("F1", CultureInfo.InvariantCulture)},{b.Y.ToString("F1", CultureInfo.InvariantCulture)}:{b.Size.ToString("F0", CultureInfo.InvariantCulture)}");
            }
        }

        var serialized = string.Join(";", parts);
        var prefs = GetSharedPreferences("ss14_mobile_config", FileCreationMode.Private);
        prefs?.Edit()?.PutString("mobile_layout", serialized)?.Apply();
        MainActivity.LaunchBuildCVars["mobile.controls_layout"] = serialized;
    }

    // Launcher-side pad editor: the game no longer has an edit mode, so the
    // player drags the sticks and buttons here and the fractions are passed to
    // the engine as cvar_build_mobile.controls_positions on the next launch.
    private void ShowControlsEditor(Action? onClosed = null)
    {
        var prefs = GetSharedPreferences("ss14_mobile_config", FileCreationMode.Private);
        var editor = new PadEditorView(this, prefs, prefs?.GetString("mobile_positions", "") ?? "", Density);

        var dialog = new Dialog(this);
        dialog.RequestWindowFeature((int)WindowFeatures.NoTitle);
        dialog.SetCanceledOnTouchOutside(false);

        var rootView = new FrameLayout(this);
        rootView.SetBackgroundColor(Color.ParseColor("#101014"));
        rootView.AddView(editor, new FrameLayout.LayoutParams(
            ViewGroup.LayoutParams.MatchParent, ViewGroup.LayoutParams.MatchParent));

        var topPanel = new LinearLayout(this) { Orientation = Orientation.Vertical };
        topPanel.SetBackgroundColor(Color.ParseColor("#E6101014"));

        var title = new TextView(this) { Text = "Drag to move, tap to select, slider to resize, then Save", TextSize = 12 };
        title.SetTextColor(LauncherTheme.NanoGold);
        title.SetPadding(Dp(12), Dp(8), Dp(12), Dp(6));
        topPanel.AddView(title);

        var sizeRow = new LinearLayout(this) { Orientation = Orientation.Horizontal };
        sizeRow.SetGravity(GravityFlags.CenterVertical);
        sizeRow.SetPadding(Dp(12), 0, Dp(12), 0);
        var sizeLabel = new TextView(this) { Text = "Tap an element to resize", TextSize = 11 };
        sizeLabel.SetTextColor(LauncherTheme.Foreground);
        sizeRow.AddView(sizeLabel, new LinearLayout.LayoutParams(0, Dp(34), 1f));
        var sizeBar = new SeekBar(this) { Max = 240 };
        var updatingSize = false;
        void SyncSize()
        {
            updatingSize = true;
            sizeBar.Progress = editor.HasSelection ? Math.Clamp(editor.SelectedScalePercent - 40, 0, 240) : 0;
            sizeLabel.Text = editor.HasSelection
                ? $"{editor.SelectedLabel} size {editor.SelectedScalePercent}%"
                : "Tap an element to resize";
            updatingSize = false;
        }
        editor.SelectionChanged += SyncSize;
        sizeBar.ProgressChanged += (_, e) =>
        {
            if (updatingSize || !e.FromUser || !editor.HasSelection)
                return;
            editor.SetSelectedScale((40 + e.Progress) / 100f);
            sizeLabel.Text = $"{editor.SelectedLabel} size {editor.SelectedScalePercent}%";
        };
        sizeRow.AddView(sizeBar, new LinearLayout.LayoutParams(Dp(180), Dp(34)));
        topPanel.AddView(sizeRow);

        var bar = new LinearLayout(this) { Orientation = Orientation.Horizontal };
        bar.SetPadding(Dp(8), Dp(2), Dp(8), Dp(6));
        var resetBtn = new Button(this) { Text = "Reset", TextSize = 11 };
        resetBtn.Background = LauncherTheme.CreateBox(LauncherTheme.ControlMid, 3);
        resetBtn.SetTextColor(LauncherTheme.Foreground);
        resetBtn.Click += (_, _) => editor.Reset();
        bar.AddView(resetBtn, new LinearLayout.LayoutParams(0, Dp(36), 1f));

        var saveBtn = new Button(this) { Text = "Save", TextSize = 11 };
        saveBtn.Background = LauncherTheme.CreateBox(LauncherTheme.ControlHigh, 3);
        saveBtn.SetTextColor(LauncherTheme.Foreground);
        saveBtn.Click += (_, _) =>
        {
            editor.Save();
            dialog.Dismiss();
        };
        bar.AddView(saveBtn, new LinearLayout.LayoutParams(0, Dp(36), 1f));
        topPanel.AddView(bar, new LinearLayout.LayoutParams(ViewGroup.LayoutParams.MatchParent, ViewGroup.LayoutParams.WrapContent));

        rootView.AddView(topPanel, new FrameLayout.LayoutParams(
            ViewGroup.LayoutParams.MatchParent, ViewGroup.LayoutParams.WrapContent) { Gravity = GravityFlags.Top });

        dialog.SetContentView(rootView);
        dialog.DismissEvent += (_, _) => onClosed?.Invoke();
        dialog.Show();
        var window = dialog.Window;
        if (window != null)
        {
            window.SetLayout(ViewGroup.LayoutParams.MatchParent, ViewGroup.LayoutParams.MatchParent);
            window.AddFlags(WindowManagerFlags.Fullscreen);
            window.SetBackgroundDrawable(new ColorDrawable(Color.ParseColor("#101014")));
            // The preview must use the same full screen frame as the game,
            // otherwise the status bar inset shifts every control down.
            if (Build.VERSION.SdkInt >= BuildVersionCodes.P)
            {
                var attributes = window.Attributes;
                if (attributes != null)
                {
                    // Always (Android 11+) so the preview shows the camera hole in
                    // landscape; older releases only have the short edges mode.
                    attributes.LayoutInDisplayCutoutMode = Build.VERSION.SdkInt >= BuildVersionCodes.R
                        ? LayoutInDisplayCutoutMode.Always
                        : LayoutInDisplayCutoutMode.ShortEdges;
                    window.Attributes = attributes;
                }
            }
            if (Build.VERSION.SdkInt >= BuildVersionCodes.R)
            {
                window.SetDecorFitsSystemWindows(false);
                var controller = window.InsetsController;
                if (controller != null)
                {
                    controller.Hide(WindowInsets.Type.StatusBars() | WindowInsets.Type.NavigationBars());
                    controller.SystemBarsBehavior = (int)WindowInsetsControllerBehavior.ShowTransientBarsBySwipe;
                }
            }

            if (window.DecorView is View decor)
                decor.SystemUiVisibility = (StatusBarVisibility)(SystemUiFlags.LayoutStable |
                    SystemUiFlags.LayoutFullscreen | SystemUiFlags.HideNavigation |
                    SystemUiFlags.Fullscreen | SystemUiFlags.ImmersiveSticky);
        }

        // Ask for fresh insets once the window attributes are in place.
        editor.RequestApplyInsets();
    }

    private sealed class PadEditorView : View
    {
        private sealed class PadItem
        {
            public string Id = "";
            public string Label = "";
            public float Fx;
            public float Fy;
            public float Radius;
            public bool Moved;
            public bool Resized;
        }

        // Shared with BuildPadLayout so the launcher and the game use one table.
        public static readonly (string id, string label, float fx, float fy, float radius)[] Defaults =
        {
            // Radii are fractions of screen height, matching the engine's
            // ArrangeOverride table in MobileControlsOverlay.
            ("stick", "MOVE", 0.21f, 0.775f, 0.20f),
            ("aim", "AIM", 0.79f, 0.775f, 0.20f),
            ("drop", "DROP", 0.3278f, 0.5661f, 0.095f),
            ("pull", "PULL", 0.21f, 0.4795f, 0.095f),
            ("esc", "ESC", 0.0922f, 0.5661f, 0.095f),
            ("run", "RUN", 0.5544f, 0.5661f, 0.09f),
            ("z", "Z", 0.9078f, 0.5661f, 0.095f),
            ("exam", "EXAM", 0.79f, 0.4795f, 0.095f),
            ("bag", "BAG", 0.6722f, 0.5661f, 0.095f),
        };

        // Version of the saved layout. MigratePadLayout drops anything older, so
        // Save and Reset have to write this exact number.
        public const int LayoutVersion = 7;

        // One range for a saved radius and for the resize slider. The engine takes
        // the radius as is, so a value outside this range would show one size in the
        // editor and another one in game.
        private const float MinRadiusFraction = 0.03f;
        private const float MaxRadiusFraction = 0.26f;

        private readonly List<PadItem> _items = new();
        private readonly ISharedPreferences? _prefs;
        private readonly float _density;
        private readonly Paint _paint = new(PaintFlags.AntiAlias);
        private int _screenW;
        private int _screenH;
        private int _originX;
        private int _originY;
        private int _safeLeft;
        private int _safeTop;
        private int _safeRight;
        private int _safeBottom;
        private bool _insetsReady;
        private PadItem? _dragging;
        private PadItem? _selected;
        private float _downX;
        private float _downY;
        private bool _moved;

        public event Action? SelectionChanged;
        public string SelectedLabel => _selected?.Label ?? "none";
        public bool HasSelection => _selected != null;

        private static float DefaultRadius(string id)
        {
            foreach (var item in Defaults)
            {
                if (item.id == id)
                    return item.radius;
            }
            return 0.078f;
        }

        /// <summary>Selected element size as a percentage of its default size.</summary>
        public int SelectedScalePercent => _selected == null
            ? 100
            : (int)Math.Round(_selected.Radius / DefaultRadius(_selected.Id) * 100f);

        public void SetSelectedScale(float scale)
        {
            if (_selected == null)
                return;
            var radius = DefaultRadius(_selected.Id) * Math.Clamp(scale, 0.4f, 2.8f);
            _selected.Radius = Math.Clamp(radius, MinRadiusFraction, MaxRadiusFraction);
            _selected.Resized = true;
            Save();
            Invalidate();
        }

        public PadEditorView(Context context, ISharedPreferences? prefs, string saved, float density) : base(context)
        {
            _prefs = prefs;
            _density = density;
            UpdateScreenFrame();
            ResetItems();
            foreach (var entry in saved.Split(';', StringSplitOptions.RemoveEmptyEntries))
            {
                var at = entry.IndexOf('@');
                if (at <= 0)
                    continue;
                var coords = entry[(at + 1)..].Split(',');
                if (coords.Length < 2)
                    continue;
                if (!float.TryParse(coords[0], NumberStyles.Float, CultureInfo.InvariantCulture, out var fx) ||
                    !float.TryParse(coords[1], NumberStyles.Float, CultureInfo.InvariantCulture, out var fy))
                    continue;
                var item = _items.Find(i => i.Id == entry[..at]);
                if (item == null)
                    continue;
                item.Fx = fx;
                item.Fy = fy;
                item.Moved = true;
                if (coords.Length >= 3 &&
                    float.TryParse(coords[2], NumberStyles.Float, CultureInfo.InvariantCulture, out var r))
                {
                    item.Radius = Math.Clamp(r, MinRadiusFraction, MaxRadiusFraction);
                    item.Resized = true;
                }
            }
            Focusable = true;
            FocusableInTouchMode = true;
            // The dialog window is laid out edge to edge with the cutout reported,
            // so it receives the same physical frame the game window gets.
            SetOnApplyWindowInsetsListener(new SafeInsetsListener((view, insets) =>
            {
                UpdateScreenFrame();
                var (frameMode, frameScale) = MobileSafeArea.ReadPrefs(Context);
                var (left, top, right, bottom) =
                    MobileSafeArea.Measure(insets, _screenW, _screenH, frameMode, frameScale);
                _safeLeft = left;
                _safeTop = top;
                _safeRight = right;
                _safeBottom = bottom;
                _insetsReady = true;
                Invalidate();
            }));
        }

        protected override void OnAttachedToWindow()
        {
            base.OnAttachedToWindow();
            RequestApplyInsets();
        }

        /// <summary>
        /// The editor preview draws in screen fractions, the same frame the game
        /// overlay uses. The dialog view itself is inset by the status bar, so
        /// every draw and touch converts through the view's screen origin.
        /// </summary>
        private void UpdateScreenFrame()
        {
            var (width, height) = MobileSafeArea.ScreenSize(Context);
            if (width > 0 && height > 0)
            {
                _screenW = width;
                _screenH = height;
            }
            if (_screenW <= 0)
            {
                _screenW = Width;
                _screenH = Height;
            }
            var location = new int[2];
            GetLocationOnScreen(location);
            _originX = location[0];
            _originY = location[1];
        }

        private void ResetItems()
        {
            _items.Clear();
            foreach (var (id, label, fx, fy, radius) in Defaults)
                _items.Add(new PadItem { Id = id, Label = label, Fx = fx, Fy = fy, Radius = radius });
        }

        public void Reset()
        {
            ResetItems();
            _selected = null;
            _prefs?.Edit()?.Remove("mobile_positions")?.PutInt("mobile_positions_layout", LayoutVersion)?.Apply();
            SelectionChanged?.Invoke();
            Invalidate();
        }

        public void Save()
        {
            var entries = new List<string>();
            foreach (var item in _items)
            {
                if (!item.Moved && !item.Resized)
                    continue;
                // Save the centre the preview actually draws (after the safe area
                // clamp), not the raw finger position: the game then reproduces
                // exactly what the player saw in the editor.
                UpdateScreenFrame();
                var radiusPx = item.Radius * _screenH;
                var center = ClampToSafe(new Vector2(item.Fx * _screenW, item.Fy * _screenH), radiusPx);
                var fx = center.X / _screenW;
                var fy = center.Y / _screenH;
                // Third field is the radius as a fraction of screen height; the
                // engine applies it together with the position.
                entries.Add($"{item.Id}@{fx.ToString("F4", CultureInfo.InvariantCulture)}," +
                            $"{fy.ToString("F4", CultureInfo.InvariantCulture)}," +
                            $"{item.Radius.ToString("F4", CultureInfo.InvariantCulture)}");
            }
            var serialized = string.Join(";", entries);
            _prefs?.Edit()?
                .PutString("mobile_positions", serialized)?
                .PutInt("mobile_positions_layout", LayoutVersion)?
                .Apply();
            CrashReporter.AppendLauncherLog("pad", $"editor saved: {serialized}");
        }

        /// <summary>
        /// Full pad layout the engine receives: every element, defaults included,
        /// clamped to the same physical frame the editor draws. The game applies
        /// the fractions to the whole screen and never clamps the launcher layout
        /// again, so the clamp has to happen here.
        /// </summary>
        public static string BuildLayout(ISharedPreferences? prefs, int screenW, int screenH,
            (int Left, int Top, int Right, int Bottom) insets)
        {
            var saved = prefs?.GetString("mobile_positions", "") ?? "";
            var byId = new Dictionary<string, (float Fx, float Fy, float R)>();
            foreach (var entry in saved.Split(';', StringSplitOptions.RemoveEmptyEntries))
            {
                var at = entry.IndexOf('@');
                if (at <= 0)
                    continue;
                var coords = entry[(at + 1)..].Split(',');
                if (coords.Length < 2)
                    continue;
                if (!float.TryParse(coords[0], NumberStyles.Float, CultureInfo.InvariantCulture, out var fx) ||
                    !float.TryParse(coords[1], NumberStyles.Float, CultureInfo.InvariantCulture, out var fy))
                    continue;
                if (coords.Length < 3 ||
                    !float.TryParse(coords[2], NumberStyles.Float, CultureInfo.InvariantCulture, out var r))
                    continue;
                byId[entry[..at]] = (fx, fy, r);
            }

            var hasScreen = screenW > 0 && screenH > 0;
            var frame = hasScreen
                ? ClampFrame(screenW, screenH, insets)
                : (Left: 0f, Top: 0f, Right: 0f, Bottom: 0f);
            var entries = new List<string>();
            foreach (var (id, _, fx, fy, radius) in Defaults)
            {
                var item = byId.TryGetValue(id, out var savedItem) ? savedItem : (Fx: fx, Fy: fy, R: radius);
                var outFx = item.Fx;
                var outFy = item.Fy;
                if (hasScreen)
                {
                    var clamped = ClampCenter(new Vector2(item.Fx * screenW, item.Fy * screenH),
                        item.R * screenH, screenW, screenH, frame);
                    outFx = clamped.X / screenW;
                    outFy = clamped.Y / screenH;
                }

                entries.Add($"{id}@{outFx.ToString("F4", CultureInfo.InvariantCulture)}," +
                            $"{outFy.ToString("F4", CultureInfo.InvariantCulture)}," +
                            $"{item.R.ToString("F4", CultureInfo.InvariantCulture)}");
            }

            return string.Join(";", entries);
        }

        // Physical safe frame plus the floors the engine uses for its own clamp,
        // so a control never lands on a rounded corner.
        private static (float Left, float Top, float Right, float Bottom) ClampFrame(
            int screenW, int screenH, (int Left, int Top, int Right, int Bottom) raw) => (
            MathF.Max(raw.Left, screenW * 0.02f),
            MathF.Max(raw.Top, screenH * 0.006f),
            MathF.Max(raw.Right, screenW * 0.02f),
            MathF.Max(raw.Bottom, screenH * 0.008f));

        private static Vector2 ClampCenter(Vector2 center, float radius, int screenW, int screenH,
            (float Left, float Top, float Right, float Bottom) frame)
        {
            // The visible disc is drawn at the radius, so the pad is the radius.
            center.X = Math.Clamp(center.X, frame.Left + radius,
                MathF.Max(frame.Left + radius, screenW - frame.Right - radius));
            center.Y = Math.Clamp(center.Y, frame.Top + radius,
                MathF.Max(frame.Top + radius, screenH - frame.Bottom - radius));
            return center;
        }

        // The cutout frame in physical screen pixels, the same values the game
        // passes to mobile.safe_area_* without any surface scaling.
        private (int Left, int Top, int Right, int Bottom) RawSafeInsets()
        {
            UpdateScreenFrame();
            if (_insetsReady)
                return (_safeLeft, _safeTop, _safeRight, _safeBottom);

            var (frameMode, frameScale) = MobileSafeArea.ReadPrefs(Context);
            var (left, top, right, bottom) =
                MobileSafeArea.Measure(RootWindowInsets, _screenW, _screenH, frameMode, frameScale);
            return (left, top, right, bottom);
        }

        private Vector2 ClampToSafe(Vector2 center, float radius)
        {
            var frame = ClampFrame(_screenW, _screenH, RawSafeInsets());
            return ClampCenter(center, radius, _screenW, _screenH, frame);
        }

        protected override void OnDraw(Canvas canvas)
        {
            base.OnDraw(canvas);
            canvas.DrawColor(Color.Argb(255, 26, 29, 38));
            UpdateScreenFrame();

            foreach (var item in _items)
            {
                var radiusPx = item.Radius * _screenH;
                var isStick = item.Id == "stick" || item.Id == "aim";
                var clamped = ClampToSafe(new Vector2(item.Fx * _screenW, item.Fy * _screenH), radiusPx);
                // Screen point to canvas point: the view sits below the status bar.
                var cx = clamped.X - _originX;
                var cy = clamped.Y - _originY;
                if (isStick)
                    DrawStick(canvas, _paint, cx, cy, radiusPx, item.Id == "aim");
                else
                    DrawButton(canvas, cx, cy, radiusPx, item.Label, AccentOf(item.Id));
            }

            if (_selected != null)
            {
                var selectedRadius = _selected.Radius * _screenH;
                var selectedCenter = ClampToSafe(
                    new Vector2(_selected.Fx * _screenW, _selected.Fy * _screenH), selectedRadius);
                _paint.SetStyle(Paint.Style.Stroke);
                _paint.StrokeWidth = MathF.Max(3f, _density * 2f);
                _paint.Color = Color.Argb(255, 255, 215, 0);
                canvas.DrawCircle(selectedCenter.X - _originX, selectedCenter.Y - _originY,
                    selectedRadius + _density * 4f, _paint);
            }

            DrawSafeBands(canvas);
        }

        /// <summary>
        /// Cutout and gesture bars in the same physical pixels that go to
        /// mobile.safe_area_*: the engine scales them to the render surface and
        /// Android stretches the surface back, so on screen the bar is unchanged.
        /// </summary>
        private void DrawSafeBands(Canvas canvas)
        {
            var (left, top, right, bottom) = RawSafeInsets();
            if (left <= 0 && top <= 0 && right <= 0 && bottom <= 0)
                return;

            _paint.SetStyle(Paint.Style.Fill);
            _paint.Color = Color.Argb(255, 0, 0, 0);

            var leftEdge = left - _originX;
            var topEdge = top - _originY;
            var rightEdge = MathF.Max(0f, (float)(_screenW - _originX - right));
            var bottomEdge = MathF.Max(0f, (float)(_screenH - _originY - bottom));
            if (leftEdge > 0)
                canvas.DrawRect(0, 0, leftEdge, Height, _paint);
            if (topEdge > 0)
                canvas.DrawRect(0, 0, Width, topEdge, _paint);
            if (right > 0)
                canvas.DrawRect(rightEdge, 0, Width, Height, _paint);
            if (bottom > 0)
                canvas.DrawRect(0, bottomEdge, Width, Height, _paint);
        }

        public static Color AccentOf(string id) => id switch
        {
            "pull" => Color.Argb(255, 155, 89, 182),
            "exam" => Color.Argb(255, 52, 152, 219),
            "run" => Color.Argb(255, 230, 180, 60),
            "z" => Color.Argb(255, 0, 180, 216),
            "drop" => Color.Argb(255, 231, 76, 60),
            "bag" => Color.Argb(255, 26, 188, 156),
            _ => Color.Argb(255, 210, 210, 210),
        };

        // Same idle face colour as PojavActionButton.Draw.
        private static Color FaceOf(Color accent) => Mix(Color.Argb(255, 74, 78, 86), accent, 0.28f);

        private static Color Mix(Color a, Color b, float t) => Color.Argb(
            (int)(a.A + (b.A - a.A) * t),
            (int)(a.R + (b.R - a.R) * t),
            (int)(a.G + (b.G - a.G) * t),
            (int)(a.B + (b.B - a.B) * t));

        public static void DrawButton(Canvas canvas, float cx, float cy, float radius, string label, Color accent)
        {
            var unit = MathF.Max(3f, MathF.Round(radius / 6f));
            var face = FaceOf(accent);
            var light = Mix(face, Color.White, 0.55f);
            var dark = Mix(face, Color.Black, 0.55f);
            var border = Color.Argb(235, 8, 10, 14);
            DrawDisc(canvas, cx, cy, radius, face, light, dark, border, unit, false);

            var labelUnit = MathF.Max(2f, MathF.Floor(unit * 0.75f));
            var textW = PixelTextWidth(label, labelUnit);
            var textH = 5f * labelUnit;
            while (labelUnit > 1.5f && (textW > radius * 1.55f || textH > radius * 1.1f))
            {
                labelUnit -= 1f;
                textW = PixelTextWidth(label, labelUnit);
                textH = 5f * labelUnit;
            }
            DrawPixelText(canvas, label, cx - textW / 2f, cy - textH / 2f, labelUnit,
                Color.Argb(245, 248, 250, 255));
        }

        public static void DrawStick(Canvas canvas, Paint paint, float cx, float cy, float radius, bool aim)
        {
            var unit = MathF.Max(2f, MathF.Round(radius / 7f));
            DrawDisc(canvas, cx, cy, radius,
                Color.Argb(110, 9, 11, 14), Color.Argb(14, 190, 195, 202),
                Color.Argb(90, 0, 0, 0), Color.Argb(150, 132, 139, 150), unit, false);
            DrawDisc(canvas, cx, cy, radius * 0.6f,
                Color.Argb(30, 0, 0, 0), Color.Argb(5, 200, 205, 212),
                Color.Argb(30, 0, 0, 0), Color.Argb(40, 0, 0, 0),
                MathF.Max(2f, unit * 0.8f), false);
            DrawDisc(canvas, cx, cy, radius * 0.3f,
                Color.Argb(40, 0, 0, 0), Color.Argb(8, 200, 205, 212),
                Color.Argb(30, 0, 0, 0), Color.Argb(70, 14, 16, 20),
                MathF.Max(2f, MathF.Round(radius * 0.3f / 5f)), false);

            var block = MathF.Max(2f, MathF.Round(radius * 0.11f));
            var cross = Color.Argb(110, 186, 192, 202);
            paint.SetStyle(Paint.Style.Fill);
            paint.Color = cross;
            void Pix(float ox, float oy)
            {
                var half = block / 2f;
                canvas.DrawRect(cx + ox - half, cy + oy - half, cx + ox + half, cy + oy + half, paint);
            }

            if (aim)
            {
                var arm = radius * 0.42f;
                Pix(-arm, 0);
                Pix(arm, 0);
                Pix(0, -arm);
                Pix(0, arm);
                Pix(0, 0);
                return;
            }

            var reach = radius * 0.62f;
            var s = block * 1.3f;
            // Four pixel chevrons, like the walk stick in game.
            Pix(-s, -reach); Pix(0, -reach - s); Pix(s, -reach);
            Pix(-s, reach); Pix(0, reach + s); Pix(s, reach);
            Pix(-reach, -s); Pix(-reach - s, 0); Pix(-reach, s);
            Pix(reach, -s); Pix(reach + s, 0); Pix(reach, s);
        }

        private static void DrawDisc(Canvas canvas, float cx, float cy, float radius,
            Color face, Color light, Color dark, Color border, float unit, bool pressed)
        {
            var paint = new Paint(PaintFlags.AntiAlias);
            var rows = Math.Max(2, (int)MathF.Ceiling(radius / unit) * 2);
            var rowHeight = radius * 2f / rows;
            var topColor = pressed ? dark : light;
            var bottomColor = pressed ? light : dark;

            for (var i = 0; i < rows; i++)
            {
                var y0 = -radius + i * rowHeight;
                var yMid = y0 + rowHeight * 0.5f;
                var half = MathF.Sqrt(MathF.Max(0f, radius * radius - yMid * yMid));
                if (half <= 0.75f)
                    continue;

                var left = cx - half;
                var top = cy + y0;
                paint.Color = border;
                canvas.DrawRect(left, top, left + half * 2f, top + rowHeight, paint);

                var inset = MathF.Min(unit, half * 0.4f);
                var innerLeft = left + inset;
                var innerRight = left + half * 2f - inset;
                if (innerRight - innerLeft <= 0.5f)
                    continue;

                paint.Color = face;
                canvas.DrawRect(innerLeft, top, innerRight, top + rowHeight, paint);

                var strip = MathF.Min(unit, (innerRight - innerLeft) * 0.5f);
                paint.Color = yMid < 0f ? topColor : bottomColor;
                var stripLeft = yMid < 0f ? innerLeft : innerRight - strip;
                canvas.DrawRect(stripLeft, top, stripLeft + strip, top + rowHeight, paint);
            }
        }

        private static readonly Dictionary<char, int[]> Glyphs = new()
        {
            ['A'] = new[] { 2, 5, 7, 5, 5 },
            ['B'] = new[] { 6, 5, 6, 5, 6 },
            ['C'] = new[] { 3, 4, 4, 4, 3 },
            ['D'] = new[] { 6, 5, 5, 5, 6 },
            ['E'] = new[] { 7, 4, 6, 4, 7 },
            ['F'] = new[] { 7, 4, 6, 4, 4 },
            ['G'] = new[] { 3, 4, 5, 5, 3 },
            ['H'] = new[] { 5, 5, 7, 5, 5 },
            ['I'] = new[] { 7, 2, 2, 2, 7 },
            ['J'] = new[] { 1, 1, 1, 5, 2 },
            ['K'] = new[] { 5, 5, 6, 5, 5 },
            ['L'] = new[] { 4, 4, 4, 4, 7 },
            ['M'] = new[] { 5, 7, 7, 5, 5 },
            ['N'] = new[] { 5, 7, 7, 7, 5 },
            ['O'] = new[] { 2, 5, 5, 5, 2 },
            ['P'] = new[] { 6, 5, 6, 4, 4 },
            ['Q'] = new[] { 2, 5, 5, 6, 3 },
            ['R'] = new[] { 6, 5, 6, 5, 5 },
            ['S'] = new[] { 3, 4, 2, 1, 6 },
            ['T'] = new[] { 7, 2, 2, 2, 2 },
            ['U'] = new[] { 5, 5, 5, 5, 7 },
            ['V'] = new[] { 5, 5, 5, 5, 2 },
            ['W'] = new[] { 5, 5, 7, 7, 5 },
            ['X'] = new[] { 5, 5, 2, 5, 5 },
            ['Y'] = new[] { 5, 5, 2, 2, 2 },
            ['Z'] = new[] { 7, 1, 2, 4, 7 },
            ['0'] = new[] { 7, 5, 5, 5, 7 },
            ['1'] = new[] { 2, 6, 2, 2, 7 },
            ['2'] = new[] { 6, 1, 2, 4, 7 },
            ['3'] = new[] { 6, 1, 2, 1, 6 },
            ['4'] = new[] { 5, 5, 7, 1, 1 },
            ['5'] = new[] { 7, 4, 6, 1, 6 },
            ['6'] = new[] { 3, 4, 7, 5, 7 },
            ['7'] = new[] { 7, 1, 2, 2, 2 },
            ['8'] = new[] { 7, 5, 7, 5, 7 },
            ['9'] = new[] { 7, 5, 7, 1, 6 },
            ['-'] = new[] { 0, 0, 7, 0, 0 },
            ['.'] = new[] { 0, 0, 0, 0, 2 },
        };

        private static float PixelTextWidth(string text, float unit)
        {
            var columns = 0f;
            foreach (var ch in text)
                columns += ch == ' ' ? 2f : 4f;
            columns -= 1f;
            return MathF.Max(0f, columns) * unit;
        }

        private static void DrawPixelText(Canvas canvas, string text, float left, float top, float unit, Color color)
        {
            var paint = new Paint(PaintFlags.AntiAlias);
            paint.Color = color;
            var x = left;
            foreach (var raw in text)
            {
                var c = char.ToUpperInvariant(raw);
                if (c == ' ')
                {
                    x += 2f * unit;
                    continue;
                }
                if (!Glyphs.TryGetValue(c, out var glyph))
                {
                    x += 4f * unit;
                    continue;
                }
                for (var row = 0; row < 5; row++)
                {
                    for (var col = 0; col < 3; col++)
                    {
                        if ((glyph[row] & (1 << (2 - col))) == 0)
                            continue;
                        var px = x + col * unit;
                        var py = top + row * unit;
                        canvas.DrawRect(px, py, px + unit, py + unit, paint);
                    }
                }
                x += 4f * unit;
            }
        }

        public override bool OnTouchEvent(MotionEvent? e)
        {
            if (e == null)
                return false;
            switch (e.ActionMasked)
            {
                case MotionEventActions.Down:
                {
                    UpdateScreenFrame();
                    var touchX = e.GetX() + _originX;
                    var touchY = e.GetY() + _originY;
                    var best = 44f * _density;
                    PadItem? found = null;
                    foreach (var item in _items)
                    {
                        var itemRadius = item.Radius * _screenH;
                        var itemCenter = ClampToSafe(
                            new Vector2(item.Fx * _screenW, item.Fy * _screenH), itemRadius);
                        var dx = touchX - itemCenter.X;
                        var dy = touchY - itemCenter.Y;
                        var dist = MathF.Sqrt(dx * dx + dy * dy);
                        if (dist <= MathF.Max(best, itemRadius))
                        {
                            best = dist;
                            found = item;
                        }
                    }
                    _dragging = found;
                    _downX = e.GetX();
                    _downY = e.GetY();
                    _moved = false;
                    Invalidate();
                    return found != null;
                }
                case MotionEventActions.Move:
                {
                    if (_dragging == null)
                        return false;
                    if (!_moved &&
                        MathF.Abs(e.GetX() - _downX) + MathF.Abs(e.GetY() - _downY) > 8f * _density)
                        _moved = true;
                    if (!_moved)
                        return true;
                    _dragging.Fx = Math.Clamp((e.GetX() + _originX) / _screenW, 0.01f, 0.99f);
                    _dragging.Fy = Math.Clamp((e.GetY() + _originY) / _screenH, 0.01f, 0.99f);
                    Invalidate();
                    return true;
                }
                case MotionEventActions.Up:
                case MotionEventActions.Cancel:
                {
                    if (_dragging == null)
                        return false;
                    if (_moved)
                    {
                        _dragging.Moved = true;
                        Save();
                    }
                    else
                    {
                        _selected = _dragging;
                        SelectionChanged?.Invoke();
                    }
                    _dragging = null;
                    _moved = false;
                    Invalidate();
                    return true;
                }
            }
            return true;
        }
    }
    // [SolomonNumb1] settings dialog: opacity, resolution, FPS limits, and cache management
    private void ShowSettingsDialog()
    {
        var prefs = GetSharedPreferences("ss14_mobile_config", FileCreationMode.Private);
        var density = Resources!.DisplayMetrics!.Density;

        var builder = new AlertDialog.Builder(this);
        builder.SetTitle("Settings");

        var scrollView = new ScrollView(this);
        var view = new LinearLayout(this)
        {
            Orientation = Orientation.Vertical
        };
        view.SetPadding((int)(16 * density), (int)(12 * density), (int)(16 * density), (int)(12 * density));
        scrollView.AddView(view);

        var toolsRow = new LinearLayout(this) { Orientation = Orientation.Horizontal };
        var logsBtn = new Button(this) { Text = "Logs", TextSize = 11 };
        logsBtn.Background = LauncherTheme.CreateBox(LauncherTheme.ControlMid, 0);
        logsBtn.SetTextColor(LauncherTheme.Foreground);
        logsBtn.LayoutParameters = new LinearLayout.LayoutParams(0, (int)(34 * density), 1f)
        {
            BottomMargin = (int)(8 * density)
        };
        logsBtn.Click += (_, _) => ShowCrashReporterDialog();
        toolsRow.AddView(logsBtn);
        view.AddView(toolsRow);

        var opacityLabel = new TextView(this)
        {
            Text = "Controls Opacity:",
            TextSize = 13,
            Typeface = global::Android.Graphics.Typeface.DefaultBold
        };
        opacityLabel.SetTextColor(LauncherTheme.Foreground);
        view.AddView(opacityLabel);

        var currentOpacity = prefs?.GetFloat("mobile_opacity", 0.6f) ?? 0.6f;
        var opacityBtnsLayout = new LinearLayout(this) { Orientation = Orientation.Horizontal };
        var opacities = new (string text, float val)[]
        {
            ("50%", 0.5f),
            ("70%", 0.7f),
            ("85%", 0.85f),
            ("100%", 1.0f)
        };

        var opBtnList = new List<Button>();
        foreach (var (text, val) in opacities)
        {
            var btn = new Button(this)
            {
                Text = text,
                TextSize = 11
            };
            btn.Background = Math.Abs(currentOpacity - val) < 0.05f 
                ? LauncherTheme.CreateBox(LauncherTheme.ControlHigh, 3) 
                : LauncherTheme.CreateBox(LauncherTheme.ControlMid, 3);
            btn.SetTextColor(LauncherTheme.Foreground);
            var lp = new LinearLayout.LayoutParams(0, (int)(32 * density), 1f);
            lp.SetMargins((int)(2 * density), (int)(4 * density), (int)(2 * density), (int)(10 * density));
            btn.LayoutParameters = lp;
            btn.Click += (_, _) =>
            {
                prefs?.Edit()?.PutFloat("mobile_opacity", val)?.Apply();
                MainActivity.LaunchBuildCVars["mobile.controls_opacity"] = val.ToString("F2", CultureInfo.InvariantCulture);
                foreach (var b in opBtnList)
                    b.Background = (b == btn) ? LauncherTheme.CreateBox(LauncherTheme.ControlHigh, 3) : LauncherTheme.CreateBox(LauncherTheme.ControlMid, 3);
            };
            opBtnList.Add(btn);
            opacityBtnsLayout.AddView(btn);
        }
        view.AddView(opacityBtnsLayout);

        var resLabel = new TextView(this)
        {
            Text = "Display Resolution:",
            TextSize = 13,
            Typeface = global::Android.Graphics.Typeface.DefaultBold
        };
        resLabel.SetTextColor(LauncherTheme.Foreground);
        var resLp = new LinearLayout.LayoutParams(ViewGroup.LayoutParams.MatchParent, ViewGroup.LayoutParams.WrapContent);
        resLp.SetMargins(0, (int)(8 * density), 0, 0);
        resLabel.LayoutParameters = resLp;
        view.AddView(resLabel);

        var currentRes = prefs?.GetString("display_resolution", "native") ?? "native";
        var resBtnsLayout = new LinearLayout(this) { Orientation = Orientation.Horizontal };
        var resolutions = new (string text, string val)[]
        {
            ("Native", "native"),
            ("900p", "900p"),
            ("720p", "720p")
        };

        var resBtnList = new List<Button>();
        foreach (var (text, val) in resolutions)
        {
            var btn = new Button(this)
            {
                Text = text,
                TextSize = 11
            };
            btn.Background = currentRes == val 
                ? LauncherTheme.CreateBox(LauncherTheme.ControlHigh, 3) 
                : LauncherTheme.CreateBox(LauncherTheme.ControlMid, 3);
            btn.SetTextColor(LauncherTheme.Foreground);
            var lp = new LinearLayout.LayoutParams(0, (int)(32 * density), 1f);
            lp.SetMargins((int)(2 * density), (int)(4 * density), (int)(2 * density), (int)(10 * density));
            btn.LayoutParameters = lp;
            btn.Click += (_, _) =>
            {
                prefs?.Edit()?.PutString("display_resolution", val)?.Apply();
                foreach (var b in resBtnList)
                    b.Background = (b == btn) ? LauncherTheme.CreateBox(LauncherTheme.ControlHigh, 3) : LauncherTheme.CreateBox(LauncherTheme.ControlMid, 3);
            };
            resBtnList.Add(btn);
            resBtnsLayout.AddView(btn);
        }
        view.AddView(resBtnsLayout);

        var listHeader = new TextView(this)
        {
            Text = "Button Customization:",
            TextSize = 13,
            Typeface = global::Android.Graphics.Typeface.DefaultBold
        };
        listHeader.SetTextColor(LauncherTheme.Foreground);
        var lhp = new LinearLayout.LayoutParams(ViewGroup.LayoutParams.MatchParent, ViewGroup.LayoutParams.WrapContent);
        lhp.SetMargins(0, (int)(10 * density), 0, (int)(6 * density));
        listHeader.LayoutParameters = lhp;
        view.AddView(listHeader);

        var buttons = LoadAllButtonsFromPrefs();
        var buttonsContainer = new LinearLayout(this) { Orientation = Orientation.Vertical };
        view.AddView(buttonsContainer);

        void PopulateButtonsList()
        {
            buttonsContainer.RemoveAllViews();
            foreach (var b in buttons)
            {
                var card = new LinearLayout(this)
                {
                    Orientation = Orientation.Horizontal,
                    BaselineAligned = false
                };
                card.Background = LauncherTheme.CreateBox(LauncherTheme.ControlMid, 3);
                card.SetPadding((int)(10 * density), (int)(6 * density), (int)(10 * density), (int)(6 * density));
                var clp = new LinearLayout.LayoutParams(ViewGroup.LayoutParams.MatchParent, ViewGroup.LayoutParams.WrapContent);
                clp.SetMargins(0, (int)(2 * density), 0, (int)(2 * density));
                card.LayoutParameters = clp;

                var textCol = new LinearLayout(this) { Orientation = Orientation.Vertical };
                var tclp = new LinearLayout.LayoutParams(0, ViewGroup.LayoutParams.WrapContent, 1f);
                textCol.LayoutParameters = tclp;

                var title = new TextView(this)
                {
                    Text = b.Label + (b.IsDeleted ? " (Hidden)" : ""),
                    TextSize = 12,
                    Typeface = global::Android.Graphics.Typeface.DefaultBold
                };
                title.SetTextColor(b.IsDeleted ? LauncherTheme.SubText : LauncherTheme.Foreground);
                textCol.AddView(title);

                var subtitle = new TextView(this)
                {
                    Text = $"Key: {b.Key} | Size: {b.Size:F0}px" + (b.IsCustom ? " | Custom" : ""),
                    TextSize = 10
                };
                subtitle.SetTextColor(LauncherTheme.SubText);
                textCol.AddView(subtitle);
                card.AddView(textCol);

                var curBtn = b;

                var editBtn = new Button(this)
                {
                    Text = "Edit",
                    TextSize = 10
                };
                editBtn.Background = LauncherTheme.CreateBox(LauncherTheme.ControlHigh, 3);
                editBtn.SetTextColor(LauncherTheme.Foreground);
                var elp = new LinearLayout.LayoutParams((int)(56 * density), (int)(32 * density));
                elp.SetMargins((int)(3 * density), 0, (int)(3 * density), 0);
                editBtn.LayoutParameters = elp;
                editBtn.Click += (_, _) =>
                {
                    ShowEditButtonDialog(curBtn, () =>
                    {
                        SaveAllButtonsToPrefs(buttons);
                        PopulateButtonsList();
                    });
                };
                card.AddView(editBtn);

                var toggleBtn = new Button(this)
                {
                    Text = curBtn.IsDeleted ? "Show" : (curBtn.IsCustom ? "Del" : "Hide"),
                    TextSize = 10
                };
                toggleBtn.Background = LauncherTheme.CreateBox(LauncherTheme.ControlMid, 3);
                toggleBtn.SetTextColor(curBtn.IsDeleted ? LauncherTheme.ControlHigh : LauncherTheme.SubText);
                var tglp = new LinearLayout.LayoutParams((int)(56 * density), (int)(32 * density));
                toggleBtn.LayoutParameters = tglp;
                toggleBtn.Click += (_, _) =>
                {
                    if (curBtn.IsCustom && !curBtn.IsDeleted)
                    {
                        buttons.Remove(curBtn);
                    }
                    else
                    {
                        curBtn.IsDeleted = !curBtn.IsDeleted;
                    }
                    SaveAllButtonsToPrefs(buttons);
                    PopulateButtonsList();
                };
                card.AddView(toggleBtn);

                buttonsContainer.AddView(card);
            }
        }

        PopulateButtonsList();

        var addBtn = new Button(this)
        {
            Text = "+ Add Custom Button",
            TextSize = 12
        };
        addBtn.Background = LauncherTheme.CreateBox(LauncherTheme.ControlHigh, 3);
        addBtn.SetTextColor(LauncherTheme.Foreground);
        var alp = new LinearLayout.LayoutParams(ViewGroup.LayoutParams.MatchParent, (int)(36 * density));
        alp.SetMargins(0, (int)(8 * density), 0, (int)(4 * density));
        addBtn.LayoutParameters = alp;
        addBtn.Click += (_, _) =>
        {
            ShowAddButtonDialog((newBtn) =>
            {
                buttons.Add(newBtn);
                SaveAllButtonsToPrefs(buttons);
                PopulateButtonsList();
            });
        };
        view.AddView(addBtn);

        var resetBtn = new Button(this)
        {
            Text = "Reset All Controls to Default",
            TextSize = 11
        };
        resetBtn.Background = LauncherTheme.CreateBox(LauncherTheme.ControlMid, 3);
        resetBtn.SetTextColor(LauncherTheme.SubText);
        var rlp = new LinearLayout.LayoutParams(ViewGroup.LayoutParams.MatchParent, (int)(34 * density));
        rlp.SetMargins(0, (int)(4 * density), 0, 0);
        resetBtn.LayoutParameters = rlp;
        resetBtn.Click += (_, _) =>
        {
            prefs?.Edit()?.Remove("mobile_layout")?.Apply();
            MainActivity.LaunchBuildCVars["mobile.controls_layout"] = "";
            buttons = GetDefaultButtonConfigs();
            PopulateButtonsList();
            Toast.MakeText(this, "Controls layout reset to default", ToastLength.Short)?.Show();
        };
        view.AddView(resetBtn);

        var cacheHeader = new TextView(this)
        {
            Text = "Server Assets Cache:",
            TextSize = 13,
            Typeface = global::Android.Graphics.Typeface.DefaultBold
        };
        cacheHeader.SetTextColor(LauncherTheme.Foreground);
        var clp = new LinearLayout.LayoutParams(ViewGroup.LayoutParams.MatchParent, ViewGroup.LayoutParams.WrapContent);
        clp.SetMargins(0, (int)(12 * density), 0, (int)(4 * density));
        cacheHeader.LayoutParameters = clp;
        view.AddView(cacheHeader);

        var cacheSizeBytes = ContentCacheManager.GetTotalCacheSizeBytes(this);
        var clearCacheBtn = new Button(this)
        {
            Text = $"Clear Server Cache ({ContentCacheManager.FormatBytes(cacheSizeBytes)})",
            TextSize = 11
        };
        clearCacheBtn.Background = LauncherTheme.CreateBox(LauncherTheme.ControlMid, 3);
        clearCacheBtn.SetTextColor(Color.ParseColor("#ef4444"));
        var cblp = new LinearLayout.LayoutParams(ViewGroup.LayoutParams.MatchParent, (int)(34 * density));
        cblp.SetMargins(0, (int)(4 * density), 0, 0);
        clearCacheBtn.LayoutParameters = cblp;
        clearCacheBtn.Click += (_, _) =>
        {
            var freed = ContentCacheManager.ClearAllCache(this, _storage);
            clearCacheBtn.Text = "Clear Server Cache (0 MB)";
            Toast.MakeText(this, $"Cleared {ContentCacheManager.FormatBytes(freed)} of server assets", ToastLength.Short)?.Show();
        };
        view.AddView(clearCacheBtn);

        var infoText = new TextView(this)
        {
            Text = "In-game: tap EDIT at the top to reposition buttons by dragging.",
            TextSize = 10
        };
        infoText.SetTextColor(LauncherTheme.SubText);
        var ilp = new LinearLayout.LayoutParams(ViewGroup.LayoutParams.MatchParent, ViewGroup.LayoutParams.WrapContent);
        ilp.SetMargins(0, (int)(8 * density), 0, (int)(4 * density));
        infoText.LayoutParameters = ilp;
        view.AddView(infoText);

        builder.SetView(scrollView);
        builder.SetPositiveButton("Close", (IDialogInterfaceOnClickListener?)null);
        builder.Show();
    }

    private void ShowEditButtonDialog(MobileButtonConfig btn, Action onSaved)
    {
        var density = Resources?.DisplayMetrics?.Density ?? 1.0f;
        var builder = new AlertDialog.Builder(this);
        builder.SetTitle($"Configure: {btn.Label}");

        var view = new LinearLayout(this)
        {
            Orientation = Orientation.Vertical
        };
        view.SetPadding((int)(16 * density), (int)(12 * density), (int)(16 * density), (int)(12 * density));

        var nameLabel = new TextView(this) { Text = "Button Label:", TextSize = 11 };
        nameLabel.SetTextColor(LauncherTheme.SubText);
        view.AddView(nameLabel);

        var nameInput = new EditText(this) { Text = btn.Label, TextSize = 13 };
        nameInput.SetTextColor(LauncherTheme.Foreground);
        nameInput.Background = LauncherTheme.CreateBox(LauncherTheme.ControlMid, 3);
        view.AddView(nameInput);

        var keyLabel = new TextView(this) { Text = "Key Code (e.g. E, Space, Return, Shift):", TextSize = 11 };
        keyLabel.SetTextColor(LauncherTheme.SubText);
        var klp = new LinearLayout.LayoutParams(ViewGroup.LayoutParams.MatchParent, ViewGroup.LayoutParams.WrapContent);
        klp.SetMargins(0, (int)(8 * density), 0, 0);
        keyLabel.LayoutParameters = klp;
        view.AddView(keyLabel);

        var keyInput = new EditText(this) { Text = btn.Key, TextSize = 13 };
        keyInput.SetTextColor(LauncherTheme.Foreground);
        keyInput.Background = LauncherTheme.CreateBox(LauncherTheme.ControlMid, 3);
        view.AddView(keyInput);

        var quickRow1 = new LinearLayout(this) { Orientation = Orientation.Horizontal };
        var qlp1 = new LinearLayout.LayoutParams(ViewGroup.LayoutParams.MatchParent, ViewGroup.LayoutParams.WrapContent);
        qlp1.SetMargins(0, (int)(4 * density), 0, 0);
        quickRow1.LayoutParameters = qlp1;
        string[] quickKeys1 = ["E", "F", "R", "C", "T", "Tab", "Space"];
        foreach (var k in quickKeys1)
        {
            var qBtn = new Button(this) { Text = k, TextSize = 9 };
            qBtn.Background = LauncherTheme.CreateBox(LauncherTheme.ControlMid, 2);
            qBtn.SetTextColor(LauncherTheme.Foreground);
            var qp = new LinearLayout.LayoutParams(0, (int)(28 * density), 1f);
            qp.SetMargins((int)(1 * density), 0, (int)(1 * density), 0);
            qBtn.LayoutParameters = qp;
            var keyVal = k;
            qBtn.Click += (_, _) =>
            {
                keyInput.Text = keyVal;
                if (string.IsNullOrWhiteSpace(nameInput.Text) || nameInput.Text == btn.Key)
                    nameInput.Text = keyVal;
            };
            quickRow1.AddView(qBtn);
        }
        view.AddView(quickRow1);

        var quickRow2 = new LinearLayout(this) { Orientation = Orientation.Horizontal };
        var qlp2 = new LinearLayout.LayoutParams(ViewGroup.LayoutParams.MatchParent, ViewGroup.LayoutParams.WrapContent);
        qlp2.SetMargins(0, (int)(4 * density), 0, 0);
        quickRow2.LayoutParameters = qlp2;
        string[] quickKeys2 = ["Shift", "Control", "Alt", "1", "2", "3", "4", "Return"];
        foreach (var k in quickKeys2)
        {
            var qBtn = new Button(this) { Text = k, TextSize = 9 };
            qBtn.Background = LauncherTheme.CreateBox(LauncherTheme.ControlMid, 2);
            qBtn.SetTextColor(LauncherTheme.Foreground);
            var qp = new LinearLayout.LayoutParams(0, (int)(28 * density), 1f);
            qp.SetMargins((int)(1 * density), 0, (int)(1 * density), 0);
            qBtn.LayoutParameters = qp;
            var keyVal = k;
            qBtn.Click += (_, _) =>
            {
                keyInput.Text = keyVal;
                if (string.IsNullOrWhiteSpace(nameInput.Text) || nameInput.Text == btn.Key)
                    nameInput.Text = keyVal;
            };
            quickRow2.AddView(qBtn);
        }
        view.AddView(quickRow2);

        var sizeLabel = new TextView(this) { Text = "Button Size:", TextSize = 11 };
        sizeLabel.SetTextColor(LauncherTheme.SubText);
        var slp = new LinearLayout.LayoutParams(ViewGroup.LayoutParams.MatchParent, ViewGroup.LayoutParams.WrapContent);
        slp.SetMargins(0, (int)(8 * density), 0, 0);
        sizeLabel.LayoutParameters = slp;
        view.AddView(sizeLabel);

        var sizeRow = new LinearLayout(this) { Orientation = Orientation.Horizontal };
        var sizes = new (string text, float val)[]
        {
            ("40px", 40f),
            ("50px", 50f),
            ("60px", 60f),
            ("72px", 72f)
        };
        var currentSize = btn.Size;
        var sizeBtnList = new List<Button>();
        foreach (var (st, sv) in sizes)
        {
            var sBtn = new Button(this) { Text = st, TextSize = 9 };
            sBtn.Background = Math.Abs(currentSize - sv) < 5f 
                ? LauncherTheme.CreateBox(LauncherTheme.ControlHigh, 2) 
                : LauncherTheme.CreateBox(LauncherTheme.ControlMid, 2);
            sBtn.SetTextColor(LauncherTheme.Foreground);
            var sp = new LinearLayout.LayoutParams(0, (int)(28 * density), 1f);
            sp.SetMargins((int)(1 * density), 0, (int)(1 * density), 0);
            sBtn.LayoutParameters = sp;
            var valCapture = sv;
            sBtn.Click += (_, _) =>
            {
                currentSize = valCapture;
                foreach (var sb in sizeBtnList)
                    sb.Background = (sb == sBtn) ? LauncherTheme.CreateBox(LauncherTheme.ControlHigh, 2) : LauncherTheme.CreateBox(LauncherTheme.ControlMid, 2);
            };
            sizeBtnList.Add(sBtn);
            sizeRow.AddView(sBtn);
        }
        view.AddView(sizeRow);

        builder.SetView(view);
        builder.SetPositiveButton("Save", (_, _) =>
        {
            btn.Label = string.IsNullOrWhiteSpace(nameInput.Text) ? "BTN" : nameInput.Text.Trim();
            btn.Key = string.IsNullOrWhiteSpace(keyInput.Text) ? btn.Label : keyInput.Text.Trim();
            btn.Size = currentSize;
            onSaved();
            Toast.MakeText(this, $"Saved button \"{btn.Label}\"", ToastLength.Short)?.Show();
        });
        builder.SetNegativeButton("Cancel", (IDialogInterfaceOnClickListener?)null);
        builder.Show();
    }

    private void ShowAddButtonDialog(Action<MobileButtonConfig> onAdded)
    {
        var density = Resources?.DisplayMetrics?.Density ?? 1.0f;
        var builder = new AlertDialog.Builder(this);
        builder.SetTitle("New Screen Button");

        var view = new LinearLayout(this)
        {
            Orientation = Orientation.Vertical
        };
        view.SetPadding((int)(16 * density), (int)(12 * density), (int)(16 * density), (int)(12 * density));

        var nameLabel = new TextView(this) { Text = "Button Label (e.g. E, F, TAB):", TextSize = 11 };
        nameLabel.SetTextColor(LauncherTheme.SubText);
        view.AddView(nameLabel);

        var nameInput = new EditText(this) { Text = "E", TextSize = 13 };
        nameInput.SetTextColor(LauncherTheme.Foreground);
        nameInput.Background = LauncherTheme.CreateBox(LauncherTheme.ControlMid, 3);
        view.AddView(nameInput);

        var keyLabel = new TextView(this) { Text = "Key Code (e.g. E, Space, Return, Shift):", TextSize = 11 };
        keyLabel.SetTextColor(LauncherTheme.SubText);
        var klp = new LinearLayout.LayoutParams(ViewGroup.LayoutParams.MatchParent, ViewGroup.LayoutParams.WrapContent);
        klp.SetMargins(0, (int)(8 * density), 0, 0);
        keyLabel.LayoutParameters = klp;
        view.AddView(keyLabel);

        var keyInput = new EditText(this) { Text = "E", TextSize = 13 };
        keyInput.SetTextColor(LauncherTheme.Foreground);
        keyInput.Background = LauncherTheme.CreateBox(LauncherTheme.ControlMid, 3);
        view.AddView(keyInput);

        builder.SetView(view);
        builder.SetPositiveButton("Add", (_, _) =>
        {
            var btnText = string.IsNullOrWhiteSpace(nameInput.Text) ? "BTN" : nameInput.Text.Trim();
            var btnKey = string.IsNullOrWhiteSpace(keyInput.Text) ? btnText : keyInput.Text.Trim();
            var id = $"custom_{Guid.NewGuid().ToString()[..6]}";

            var newBtn = new MobileButtonConfig
            {
                Id = id,
                Label = btnText,
                Key = btnKey,
                Size = 52f,
                X = 400f,
                Y = 300f,
                IsCustom = true
            };

            onAdded(newBtn);
            Toast.MakeText(this, $"Button \"{btnText}\" added!", ToastLength.Short)?.Show();
        });
        builder.SetNegativeButton("Cancel", (IDialogInterfaceOnClickListener?)null);
        builder.Show();
    }

    private async Task StartConnectingAsync(string address)
    {
        var activeAccount = _storage.GetActiveAccount();
        if (activeAccount == null || string.IsNullOrEmpty(activeAccount.Token))
        {
            Toast.MakeText(this, "Войдите в аккаунт Space Station 14, чтобы играть", ToastLength.Long)?.Show();
            UpdateAccountButton();
            return;
        }

        _storage.SetLastServer(address);
        _overlayBox.Visibility = ViewStates.Visible;
        SetOverlayProgress(-1);
        _overlayText.Text = "Resolving server build info...";
        Window?.AddFlags(WindowManagerFlags.KeepScreenOn);

        try
        {
            if (!UriHelper.TryParseSs14Uri(address, out var parsedAddress))
            {
                throw new FormatException($"Invalid server address: {address}");
            }

            ServerInfo? serverInfo = null;
            for (var attempt = 1; attempt <= 2; attempt++)
            {
                if (attempt > 1)
                {
                    _overlayText.Text = "Server preparing content, retrying...";
                    await Task.Delay(2000);
                }
                serverInfo = await _hubClient.FetchServerInfoAsync(address);
                if (serverInfo?.Build != null)
                    break;
            }

            var build = serverInfo?.Build;

            global::Android.Util.Log.Info("SS14Launcher", $"Server info: connectAddr={serverInfo?.ConnectAddress}, hasBuild={build != null}, acz={build?.Acz}, engineVer={build?.EngineVersion}, manifestHash={build?.ManifestHash}");

            if (build == null)
            {
                Window?.ClearFlags(WindowManagerFlags.KeepScreenOn);
                _overlayBox.Visibility = ViewStates.Gone;
                var errMsg = serverInfo == null
                    ? "Failed to reach server. Please ensure the server is online and try again."
                    : "Server did not provide build or content information.";
                global::Android.Util.Log.Warn("SS14Launcher", errMsg);
                Toast.MakeText(this, errMsg, ToastLength.Long)?.Show();
                return;
            }

            var cacheDir = AppStorage.ContentCacheDir;
            Directory.CreateDirectory(cacheDir);
            ContentStore.AdoptLegacyDir(AppStorage.LegacyContentCacheDir, cacheDir);

            string? targetContent = null;

            var contentDir = ContentStore.GetContentDir(cacheDir, parsedAddress.ToString());
            if (ContentStore.IsUpToDate(contentDir, build))
            {
                global::Android.Util.Log.Info("SS14Launcher", $"Content is up to date: {contentDir}");
                targetContent = contentDir;
            }
            else
            {
                // Старый кеш в виде zip всё ещё валиден и не требует перекачивания.
                var expectedHash = build.ManifestHash?.ToUpperInvariant() ?? build.Hash?.ToUpperInvariant();
                if (!string.IsNullOrEmpty(expectedHash))
                {
                    var cachedZip = Path.Combine(cacheDir, $"{expectedHash}.zip");
                    if (File.Exists(cachedZip) && new FileInfo(cachedZip).Length > 0)
                    {
                        global::Android.Util.Log.Info("SS14Launcher", $"Found cached content zip: {cachedZip}");
                        targetContent = cachedZip;
                    }
                }
            }

            if (targetContent != null)
            {
                Window?.ClearFlags(WindowManagerFlags.KeepScreenOn);
                _overlayText.Text = "Launching game...";
                LaunchGameWithContent(targetContent, serverInfo, parsedAddress.ToString());
                return;
            }

            _pendingServerInfo = serverInfo;
            _pendingServerAddress = parsedAddress.ToString();

            SetOverlayProgress(-1);
            _overlayText.Text = "Downloading server content (check notification)...";

            ContentDownloadService.Start(this, build, address, cacheDir);
        }
        catch (Exception ex)
        {
            Window?.ClearFlags(WindowManagerFlags.KeepScreenOn);
            _overlayBox.Visibility = ViewStates.Gone;
            global::Android.Util.Log.Error("SS14Launcher", $"StartConnectingAsync error: {ex}");
            RunOnUiThread(() =>
            {
                Toast.MakeText(this, $"Connection error: {ex.Message}", ToastLength.Long)?.Show();
            });
        }
    }

    private void OnServiceProgressChanged(int cur, int tot, string status)
    {
        RunOnUiThread(() =>
        {
            if (_overlayBox.Visibility != ViewStates.Visible)
            {
                _overlayBox.Visibility = ViewStates.Visible;
                Window?.AddFlags(WindowManagerFlags.KeepScreenOn);
            }

            SetOverlayProgress(tot > 0 ? (int)(cur * 100L / tot) : -1);
            _overlayText.Text = status;
        });
    }

    private void OnServiceDownloadCompleted(string targetContent)
    {
        RunOnUiThread(() =>
        {
            Window?.ClearFlags(WindowManagerFlags.KeepScreenOn);
            _overlayText.Text = "Launching game...";
            LaunchGameWithContent(targetContent, _pendingServerInfo, _pendingServerAddress ?? "");
        });
    }

    private void OnServiceDownloadFailed(string error)
    {
        RunOnUiThread(() =>
        {
            Window?.ClearFlags(WindowManagerFlags.KeepScreenOn);
            _overlayBox.Visibility = ViewStates.Gone;
            Toast.MakeText(this, $"Download failed: {error}", ToastLength.Long)?.Show();
        });
    }

    private void LaunchGameWithContent(string? targetContent, ServerInfo? serverInfo, string ss14Address)
    {
        _overlayBox.Visibility = ViewStates.Gone;

        var intent = new Intent(this, typeof(MainActivity));
        intent.AddFlags(ActivityFlags.ClearTop | ActivityFlags.SingleTop);
        if (targetContent != null && Directory.Exists(targetContent))
        {
            intent.PutExtra("mount_dir", targetContent);
        }
        else if (targetContent != null && File.Exists(targetContent))
        {
            intent.PutExtra("mount_zip", targetContent);
        }

        var parsedAddr = UriHelper.TryParseSs14Uri(ss14Address, out var pAddr) ? pAddr : new Uri("ss14://127.0.0.1:1212");
        var realConnectAddr = UriHelper.GetConnectAddress(serverInfo, parsedAddr);

        global::Android.Util.Log.Info("SS14Launcher", $"Launching MainActivity: connect_addr={realConnectAddr}, ss14_addr={ss14Address}, content={targetContent}");

        intent.PutExtra("connect_addr", realConnectAddr);
        intent.PutExtra("ss14_addr", ss14Address);

        var prefs = GetSharedPreferences("ss14_mobile_config", FileCreationMode.Private);
        if (prefs != null)
        {
            var o = prefs.GetFloat("mobile_opacity", 0.85f);
            intent.PutExtra("cvar_build_mobile.controls_opacity", o.ToString("F2", System.Globalization.CultureInfo.InvariantCulture));
            // display.uiScale is derived from display_resolution in MainActivity.
            var dispRes = prefs.GetString("display_resolution", "native") ?? "native";
            intent.PutExtra("display_resolution", dispRes);
            intent.PutExtra("cvar_build_mobile.controls_positions", BuildPadLayout(prefs));
            intent.PutExtra("frame_mode", MobileSafeArea.ReadMode(prefs));
            intent.PutExtra("frame_scale", "x" + MobileSafeArea.ReadScale(prefs));
            ApplyGraphicsExtras(intent, prefs);
        }

        if (!string.IsNullOrEmpty(serverInfo?.Auth?.PublicKey))
        {
            intent.PutExtra("pubkey", serverInfo.Auth.PublicKey);
            Environment.SetEnvironmentVariable("ROBUST_AUTH_PUBKEY", serverInfo.Auth.PublicKey);
        }

        var build = serverInfo?.Build;
        if (build != null)
        {
            if (!string.IsNullOrEmpty(build.EngineVersion))
            {
                intent.PutExtra("engine_version", build.EngineVersion);
                intent.PutExtra("cvar_build_engine_version", build.EngineVersion);
            }
            if (!string.IsNullOrEmpty(build.ManifestHash)) intent.PutExtra("cvar_build_manifest_hash", build.ManifestHash);
            if (!string.IsNullOrEmpty(build.ForkId)) intent.PutExtra("cvar_build_fork_id", build.ForkId);
            if (!string.IsNullOrEmpty(build.Version)) intent.PutExtra("cvar_build_version", build.Version);
            if (!string.IsNullOrEmpty(build.Hash)) intent.PutExtra("cvar_build_hash", build.Hash);
            if (!string.IsNullOrEmpty(build.DownloadUrl)) intent.PutExtra("cvar_build_download_url", build.DownloadUrl);
            if (!string.IsNullOrEmpty(build.ManifestUrl)) intent.PutExtra("cvar_build_manifest_url", build.ManifestUrl);
            if (!string.IsNullOrEmpty(build.ManifestDownloadUrl)) intent.PutExtra("cvar_build_manifest_download_url", build.ManifestDownloadUrl);
        }

        var acc = _storage.GetActiveAccount();
        if (acc != null && !string.IsNullOrEmpty(acc.Token))
        {
            intent.PutExtra("username", acc.Username);
            intent.PutExtra("token", acc.Token);
            intent.PutExtra("userid", acc.UserId);
            Environment.SetEnvironmentVariable("ROBUST_AUTH_TOKEN", acc.Token);
            Environment.SetEnvironmentVariable("ROBUST_AUTH_USERID", acc.UserId);
            Environment.SetEnvironmentVariable("ROBUST_AUTH_SERVER", "https://auth.spacestation14.com/");
        }
        else
        {
            string? fallbackUser = null;
            try
            {
                var authPath = Path.Combine(FilesDir?.AbsolutePath ?? "", "auth.json");
                if (File.Exists(authPath))
                {
                    var json = File.ReadAllText(authPath);
                    using var doc = System.Text.Json.JsonDocument.Parse(json);
                    var root = doc.RootElement;
                    var user = root.TryGetProperty("username", out var u) ? u.GetString() : null;
                    var token = root.TryGetProperty("token", out var t) ? t.GetString() : null;
                    var userid = root.TryGetProperty("userId", out var id) ? id.GetString() : (root.TryGetProperty("userid", out var id2) ? id2.GetString() : null);
                    if (!string.IsNullOrEmpty(user) && !string.IsNullOrEmpty(token))
                    {
                        fallbackUser = user;
                        intent.PutExtra("username", user);
                        intent.PutExtra("token", token);
                        if (!string.IsNullOrEmpty(userid)) intent.PutExtra("userid", userid);
                        Environment.SetEnvironmentVariable("ROBUST_AUTH_TOKEN", token);
                        if (!string.IsNullOrEmpty(userid)) Environment.SetEnvironmentVariable("ROBUST_AUTH_USERID", userid);
                        Environment.SetEnvironmentVariable("ROBUST_AUTH_SERVER", "https://auth.spacestation14.com/");
                    }
                }
            }
            catch {}

        }

        StartActivity(intent);
    }

    #region Crash Reporter
    // [SolomonNumb1] prompts user with crash diagnostics if previous game session terminated unexpectedly
    private void CheckForCrashAndPrompt()
    {
        try
        {
            if (!CrashReporter.HasNewCrash())
                return;
            var builder = new AlertDialog.Builder(this);
            builder.SetTitle("Last session crashed");
            builder.SetMessage("The game crashed last time. You can view the crash report " +
                "and share it with developers.\n\n" + CrashReporter.GetStatusLine());
            builder.SetPositiveButton("View Logs", (_, _) => ShowCrashReporterDialog("crash.txt"));
            builder.SetNegativeButton("Dismiss", (_, _) => CrashReporter.MarkSeen());
            builder.SetCancelable(true);
            RunOnUiThread(() =>
            {
                try { builder.Show(); } catch { }
            });
        }
        catch { }
    }

    // Full-screen log viewer. The phone has no adb, so every diagnostic the game
    // or this activity writes has to be readable and shareable in the app itself.
    private void ShowCrashReporterDialog(string? selectTitle = null)
    {
        var density = Resources?.DisplayMetrics?.Density ?? 1f;
        var sources = CrashReporter.GetLogFiles();

        var dialog = new Dialog(this);
        dialog.RequestWindowFeature((int)WindowFeatures.NoTitle);
        dialog.SetCanceledOnTouchOutside(false);

        var root = new LinearLayout(this) { Orientation = Orientation.Vertical };
        root.SetBackgroundColor(Color.ParseColor("#101014"));
        root.SetPadding((int)(12 * density), (int)(10 * density), (int)(12 * density), (int)(10 * density));

        var status = new TextView(this) { Text = CrashReporter.GetStatusLine(), TextSize = 11 };
        status.SetTextColor(LauncherTheme.SubText);
        root.AddView(status);

        var tabs = new HorizontalScrollView(this);
        var tabRow = new LinearLayout(this) { Orientation = Orientation.Horizontal };
        tabs.AddView(tabRow);
        root.AddView(tabs, new LinearLayout.LayoutParams(
            ViewGroup.LayoutParams.MatchParent, ViewGroup.LayoutParams.WrapContent));

        var scroll = new ScrollView(this);
        var text = new TextView(this) { TextSize = 9, Typeface = Typeface.Monospace };
        text.SetTextColor(LauncherTheme.Foreground);
        text.SetTextIsSelectable(true);
        text.SetPadding(0, (int)(6 * density), 0, 0);
        scroll.AddView(text);
        root.AddView(scroll, new LinearLayout.LayoutParams(
            ViewGroup.LayoutParams.MatchParent, 0, 1f));

        var info = new TextView(this) { TextSize = 10 };
        info.SetTextColor(LauncherTheme.SubText);
        info.SetPadding(0, (int)(4 * density), 0, (int)(4 * density));
        root.AddView(info);

        var bar = new LinearLayout(this) { Orientation = Orientation.Horizontal };

        string? currentTitle = null;
        string? currentPath = null;

        void Load(string? title)
        {
            CrashReporter.LogFile? picked = null;
            foreach (var source in sources)
            {
                if (source.Title == title)
                {
                    picked = source;
                    break;
                }
            }

            if (picked == null && sources.Count > 0)
                picked = sources[0];

            if (picked == null)
            {
                currentTitle = null;
                currentPath = null;
                text.Text = "No logs yet.\n\n" +
                    "The game writes game.log and crash.txt on its next run.\n" +
                    "mobile-controls-debug.txt appears once the in-game pad is laid out.";
                info.Text = CrashReporter.GetStatusLine();
                RenderTabs();
                return;
            }

            var log = picked.Value;
            currentTitle = log.Title;
            currentPath = log.Path;
            var body = CrashReporter.ReadTailText(log.Path, 400_000);
            text.Text = body;
            info.Text = $"{log.Title}: {log.Size / 1024} KB, {body.Length} chars shown";
            scroll.Post(() => scroll.FullScroll(FocusSearchDirection.Down));
            RenderTabs();
        }

        void RenderTabs()
        {
            tabRow.RemoveAllViews();
            foreach (var source in sources)
            {
                var name = source.Title;
                var tab = new Button(this) { Text = name, TextSize = 10 };
                tab.SetTextColor(LauncherTheme.Foreground);
                tab.Background = LauncherTheme.CreateBox(
                    currentTitle == name ? LauncherTheme.ControlHigh : LauncherTheme.ControlMid, 3);
                tab.SetPadding((int)(10 * density), 0, (int)(10 * density), 0);
                tab.LayoutParameters = new LinearLayout.LayoutParams(
                    ViewGroup.LayoutParams.WrapContent, (int)(32 * density))
                {
                    RightMargin = (int)(4 * density)
                };
                tab.Click += (_, _) => Load(name);
                tabRow.AddView(tab);
            }
        }

        void CopyCurrentLog()
        {
            try
            {
                var body = currentPath == null
                    ? CrashReporter.GetStatusLine()
                    : CrashReporter.GetShareText(currentTitle ?? "log", currentPath);
                var clipboard = GetSystemService(ClipboardService) as ClipboardManager;
                if (clipboard != null)
                    clipboard.PrimaryClip = ClipData.NewPlainText("SS14 log", body);
                Toast.MakeText(this, $"Copied {currentTitle ?? "status"}", ToastLength.Short)?.Show();
            }
            catch (Exception ex)
            {
                Toast.MakeText(this, $"Copy failed: {ex.Message}", ToastLength.Short)?.Show();
            }
        }

        void ShareCurrentLog()
        {
            try
            {
                if (currentPath == null)
                {
                    Toast.MakeText(this, "No log to share yet", ToastLength.Short)?.Show();
                    return;
                }

                var intent = new Intent(Intent.ActionSend);
                intent.SetType("text/plain");
                intent.PutExtra(Intent.ExtraSubject,
                    $"SS14 Android log {GetCurrentAppVersion()} ({currentTitle})");
                intent.PutExtra(Intent.ExtraText, CrashReporter.GetShareText(currentTitle ?? "log", currentPath));
                StartActivity(Intent.CreateChooser(intent, "Share log"));
            }
            catch (Exception ex)
            {
                Toast.MakeText(this, $"Share failed: {ex.Message}", ToastLength.Short)?.Show();
            }
        }

        Button MakeBarButton(string label)
        {
            var button = new Button(this) { Text = label, TextSize = 11 };
            button.Background = LauncherTheme.CreateBox(LauncherTheme.ControlMid, 3);
            button.SetTextColor(LauncherTheme.Foreground);
            button.LayoutParameters = new LinearLayout.LayoutParams(0, (int)(34 * density), 1f)
            {
                RightMargin = (int)(4 * density)
            };
            bar.AddView(button);
            return button;
        }

        MakeBarButton("Refresh").Click += (_, _) =>
        {
            sources = CrashReporter.GetLogFiles();
            status.Text = CrashReporter.GetStatusLine();
            Load(currentTitle);
        };
        MakeBarButton("Copy").Click += (_, _) => CopyCurrentLog();
        MakeBarButton("Share").Click += (_, _) => ShareCurrentLog();
        MakeBarButton("Close").Click += (_, _) => dialog.Dismiss();

        root.AddView(bar, new LinearLayout.LayoutParams(
            ViewGroup.LayoutParams.MatchParent, ViewGroup.LayoutParams.WrapContent));
        dialog.SetContentView(root);

        var window = dialog.Window;
        if (window != null)
        {
            window.SetLayout(ViewGroup.LayoutParams.MatchParent, ViewGroup.LayoutParams.MatchParent);
            window.SetBackgroundDrawable(new ColorDrawable(Color.ParseColor("#101014")));
        }

        dialog.Show();
        Load(selectTitle);
        CrashReporter.MarkSeen();
    }

    private void ClearCrashReports()
    {
        try
        {
            CrashReporter.Clear();
            Toast.MakeText(this, "Crash reports cleared", ToastLength.Short)?.Show();
        }
        catch { }
    }

    #endregion

    private void OpenUrl(string url)
    {
        try
        {
            var intent = new Intent(Intent.ActionView, global::Android.Net.Uri.Parse(url));
            StartActivity(intent);
        }
        catch {}
    }

    public const string CurrentAppVersion = "v1.0.4";


    public string GetCurrentAppVersion()
    {
        try
        {
            var pInfo = PackageManager?.GetPackageInfo(PackageName ?? "", 0);
            if (!string.IsNullOrEmpty(pInfo?.VersionName))
            {
                var ver = pInfo.VersionName.Trim();
                return ver.StartsWith("v", StringComparison.OrdinalIgnoreCase) ? ver : "v" + ver;
            }
        }
        catch {}
        return CurrentAppVersion;
    }

    private void ShowDisclaimerDialog()
    {
        var builder = new AlertDialog.Builder(this);
        var density = Resources?.DisplayMetrics?.Density ?? 1f;

        var scroll = new ScrollView(this);
        var view = new LinearLayout(this)
        {
            Orientation = Orientation.Vertical
        };
        view.SetBackgroundColor(LauncherTheme.PopupBackground);
        view.SetPadding((int)(20 * density), (int)(16 * density), (int)(20 * density), (int)(16 * density));

        var title = new TextView(this)
        {
            Text = "Unofficial Mobile Client",
            TextSize = 16,
            Typeface = Typeface.DefaultBold
        };
        title.SetTextColor(LauncherTheme.NanoGold);
        title.SetPadding(0, 0, 0, (int)(10 * density));
        view.AddView(title);

        var msgText = new TextView(this)
        {
            Text = "This Space Station 14 Android port is an UNOFFICIAL, community-driven client modification.\n\n" +
                   "• It is NOT developed, provided, or supported by Wizard's Den or Space Wizards Federation.\n\n" +
                   "• Play at your own risk: Stability, controls, and performance vary depending on your device.\n\n" +
                   "• DO NOT report mobile-specific issues (crashes, low FPS, touch UI bugs) via in-game 'ahelp' or to official Wizard's Den staff/mentors.\n\n" +
                   "• If you encounter bugs or performance problems, report them directly to the GitHub Issues section.",
            TextSize = 12
        };
        msgText.SetTextColor(LauncherTheme.Foreground);
        msgText.SetPadding(0, 0, 0, (int)(14 * density));
        view.AddView(msgText);

        var btnRow = new LinearLayout(this)
        {
            Orientation = Orientation.Horizontal
        };
        btnRow.LayoutParameters = new LinearLayout.LayoutParams(ViewGroup.LayoutParams.MatchParent, ViewGroup.LayoutParams.WrapContent);

        var issuesBtn = new Button(this)
        {
            Text = "GitHub Issues",
            TextSize = 11
        };
        issuesBtn.Background = LauncherTheme.CreateBox(LauncherTheme.ControlMid, 3);
        issuesBtn.SetTextColor(LauncherTheme.Foreground);
        var issuesLp = new LinearLayout.LayoutParams(0, (int)(36 * density), 1f)
        {
            RightMargin = (int)(6 * density)
        };
        issuesBtn.LayoutParameters = issuesLp;
        issuesBtn.Click += (_, _) => OpenUrl("https://github.com/ministation/space-station-14-client/issues");
        btnRow.AddView(issuesBtn);

        AlertDialog? dialog = null;

        var agreeBtn = new Button(this)
        {
            Text = "I Understand & Agree",
            TextSize = 11,
            Typeface = Typeface.DefaultBold
        };
        agreeBtn.Background = LauncherTheme.CreateBox(Color.ParseColor("#2e7d32"), 3);
        agreeBtn.SetTextColor(LauncherTheme.Foreground);
        var agreeLp = new LinearLayout.LayoutParams(0, (int)(36 * density), 1.2f);
        agreeBtn.LayoutParameters = agreeLp;
        agreeBtn.Click += (_, _) =>
        {
            _storage.Data.DisclaimerAccepted = true;
            _storage.Save();
            dialog?.Dismiss();
            _ = CheckForUpdatesAsync();
        };
        btnRow.AddView(agreeBtn);

        view.AddView(btnRow);
        scroll.AddView(view);
        builder.SetView(scroll);
        builder.SetCancelable(false);

        dialog = builder.Create();
        dialog.Show();
    }

    private async Task CheckForUpdatesAsync(bool userInitiated = false)
    {
        try
        {
            using var http = new HttpClient();
            http.Timeout = TimeSpan.FromSeconds(10);
            http.DefaultRequestHeaders.UserAgent.ParseAdd("SS14-Android-Launcher");

            var json = await http.GetStringAsync("https://api.github.com/repos/ministation/space-station-14-client/releases?per_page=20");
            var releases = JsonSerializer.Deserialize<List<GitHubReleaseInfo>>(json) ?? new List<GitHubReleaseInfo>();
            GitHubReleaseInfo? release = null;
            foreach (var candidate in releases)
            {
                if (string.IsNullOrWhiteSpace(candidate.TagName) ||
                    !candidate.TagName.StartsWith("android-v", StringComparison.OrdinalIgnoreCase))
                    continue;
                if (release == null || IsNewerVersion(candidate.TagName, release.TagName))
                    release = candidate;
            }

            if (release == null || string.IsNullOrWhiteSpace(release.TagName))
            {
                if (userInitiated)
                {
                    RunOnUiThread(() => Toast.MakeText(this, "Could not check for updates.", ToastLength.Short)?.Show());
                }
                return;
            }

            var currentVer = GetCurrentAppVersion();
            if (IsNewerVersion(release.TagName, currentVer))
            {
                RunOnUiThread(() => ShowUpdateDialog(release, currentVer));
            }
            else if (userInitiated)
            {
                RunOnUiThread(() => Toast.MakeText(this, $"You are on the latest version ({currentVer})", ToastLength.Short)?.Show());
            }
        }
        catch (Exception ex)
        {
            if (userInitiated)
            {
                RunOnUiThread(() => Toast.MakeText(this, $"Update check failed: {ex.Message}", ToastLength.Short)?.Show());
            }
        }
    }

    private static bool IsNewerVersion(string latestTag, string currentTag)
    {
        var lStr = latestTag.TrimStart('v', 'V').Trim();
        var cStr = currentTag.TrimStart('v', 'V').Trim();

        if (Version.TryParse(lStr, out var lVer) && Version.TryParse(cStr, out var cVer))
        {
            return lVer > cVer;
        }

        var lParts = lStr.Split('.');
        var cParts = cStr.Split('.');
        var len = Math.Max(lParts.Length, cParts.Length);
        for (var i = 0; i < len; i++)
        {
            var lNum = i < lParts.Length && int.TryParse(lParts[i], out var p1) ? p1 : 0;
            var cNum = i < cParts.Length && int.TryParse(cParts[i], out var p2) ? p2 : 0;
            if (lNum > cNum) return true;
            if (lNum < cNum) return false;
        }

        return string.Compare(latestTag, currentTag, StringComparison.OrdinalIgnoreCase) > 0;
    }

    private void ShowUpdateDialog(GitHubReleaseInfo release, string currentVer)
    {
        var builder = new AlertDialog.Builder(this);
        var density = Resources?.DisplayMetrics?.Density ?? 1f;

        var scroll = new ScrollView(this);
        var view = new LinearLayout(this)
        {
            Orientation = Orientation.Vertical
        };
        view.SetBackgroundColor(LauncherTheme.PopupBackground);
        view.SetPadding((int)(20 * density), (int)(16 * density), (int)(20 * density), (int)(16 * density));

        var title = new TextView(this)
        {
            Text = $"New Update Available: {release.TagName}",
            TextSize = 16,
            Typeface = Typeface.DefaultBold
        };
        title.SetTextColor(LauncherTheme.NanoGold);
        title.SetPadding(0, 0, 0, (int)(6 * density));
        view.AddView(title);

        var verText = new TextView(this)
        {
            Text = $"Current: {currentVer}  →  New: {release.TagName}",
            TextSize = 12
        };
        verText.SetTextColor(LauncherTheme.SubText);
        verText.SetPadding(0, 0, 0, (int)(10 * density));
        view.AddView(verText);

        if (!string.IsNullOrWhiteSpace(release.Body))
        {
            var notesTitle = new TextView(this)
            {
                Text = "Changelog:",
                TextSize = 12,
                Typeface = Typeface.DefaultBold
            };
            notesTitle.SetTextColor(LauncherTheme.Foreground);
            notesTitle.SetPadding(0, 0, 0, (int)(4 * density));
            view.AddView(notesTitle);

            var notesText = new TextView(this)
            {
                Text = release.Body.Trim(),
                TextSize = 11
            };
            notesText.SetTextColor(LauncherTheme.ForegroundMuted);
            notesText.SetPadding(0, 0, 0, (int)(12 * density));
            view.AddView(notesText);
        }

        var btnRow = new LinearLayout(this)
        {
            Orientation = Orientation.Horizontal
        };
        btnRow.LayoutParameters = new LinearLayout.LayoutParams(ViewGroup.LayoutParams.MatchParent, ViewGroup.LayoutParams.WrapContent);

        scroll.AddView(view);
        builder.SetView(scroll);

        AlertDialog? dialog = null;

        var laterBtn = new Button(this)
        {
            Text = "Later",
            TextSize = 11
        };
        laterBtn.Background = LauncherTheme.CreateBox(LauncherTheme.ControlMid, 3);
        laterBtn.SetTextColor(LauncherTheme.Foreground);
        var laterLp = new LinearLayout.LayoutParams(0, (int)(36 * density), 1f)
        {
            RightMargin = (int)(6 * density)
        };
        laterBtn.LayoutParameters = laterLp;
        laterBtn.Click += (_, _) => dialog?.Dismiss();
        btnRow.AddView(laterBtn);

        var downloadBtn = new Button(this)
        {
            Text = "Download APK",
            TextSize = 11,
            Typeface = Typeface.DefaultBold
        };
        downloadBtn.Background = LauncherTheme.CreateBox(LauncherTheme.NanoGold, 3);
        downloadBtn.SetTextColor(Color.ParseColor("#111827"));
        var downloadLp = new LinearLayout.LayoutParams(0, (int)(36 * density), 1.2f);
        downloadBtn.LayoutParameters = downloadLp;

        var apkAsset = release.Assets?.FirstOrDefault(a => a.Name.EndsWith(".apk", StringComparison.OrdinalIgnoreCase));
        // Updates download from the CDN mirror, not from GitHub: the mirror keeps every released version
        // under /android/<version>/ and is reachable in Russia without the GitHub release page.
        var version = release.TagName?.Trim() ?? "";
        if (version.StartsWith("android-v", StringComparison.OrdinalIgnoreCase))
            version = version["android-v".Length..];
        var downloadUrl = !string.IsNullOrWhiteSpace(version)
            ? $"https://cdn.ministation.ru/android/{version}/space-station.apk"
            : apkAsset?.BrowserDownloadUrl ?? release.HtmlUrl ?? "https://github.com/ministation/space-station-14-client/releases/latest";

        downloadBtn.Click += (_, _) =>
        {
            dialog?.Dismiss();
            OpenUrl(downloadUrl);
        };
        btnRow.AddView(downloadBtn);

        view.AddView(btnRow);

        dialog = builder.Create();
        dialog.Show();
    }
}
