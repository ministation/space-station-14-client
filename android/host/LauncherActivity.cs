using System;
using System.Threading;
using System.Threading.Tasks;
using Android.App;
using Android.Content;
using Android.Content.PM;
using Android.OS;
using Android.Util;
using Android.Views;
using Android.Widget;

namespace Content.Android;

/// <summary>
/// Startup screen: downloads the prebuilt client content, then launches the real SS14 client.
/// </summary>
[Activity(
    Label = "@string/app_name",
    MainLauncher = true,
    Exported = true,
    Icon = "@mipmap/ic_launcher",
    RoundIcon = "@mipmap/ic_launcher_round",
    Theme = "@android:style/Theme.NoTitleBar.Fullscreen",
    ScreenOrientation = ScreenOrientation.SensorLandscape,
    ConfigurationChanges =
        ConfigChanges.Orientation | ConfigChanges.ScreenSize | ConfigChanges.Keyboard |
        ConfigChanges.KeyboardHidden | ConfigChanges.UiMode | ConfigChanges.Navigation |
        ConfigChanges.ScreenLayout | ConfigChanges.SmallestScreenSize | ConfigChanges.Density)]
public class LauncherActivity : Activity
{
    private static readonly string[] LaunchExtras =
    {
        "connect_addr", "ss14_addr", "username", "token", "userid", "pubkey",
    };

    private TextView _status = default!;
    private TextView _detail = default!;
    private TextView _message = default!;
    private ProgressBar _bar = default!;
    private LinearLayout _root = default!;
    private CancellationTokenSource? _cancel;
    private bool _running;

    protected override void OnCreate(Bundle? savedInstanceState)
    {
        base.OnCreate(savedInstanceState);
        Window?.AddFlags(WindowManagerFlags.KeepScreenOn);
        ConfigureServerInfoUrl();
        BuildUi();
        StartProvisioning();
    }

    /// <summary>
    /// Points the provisioner at the server the user actually connects to:
    /// udp://host:port → http://host:port/info (overridable via info_url extra).
    /// </summary>
    private void ConfigureServerInfoUrl()
    {
        var infoUrl = Intent?.GetStringExtra("info_url");
        if (string.IsNullOrWhiteSpace(infoUrl))
        {
            var connectAddr = Intent?.GetStringExtra("connect_addr");
            if (!string.IsNullOrWhiteSpace(connectAddr)
                && Uri.TryCreate(connectAddr.Replace("udp://", "http://", StringComparison.OrdinalIgnoreCase)
                    .Replace("ss14s://", "https://", StringComparison.OrdinalIgnoreCase)
                    .Replace("ss14://", "http://", StringComparison.OrdinalIgnoreCase),
                    UriKind.Absolute, out var uri))
            {
                infoUrl = $"{uri.Scheme}://{uri.Authority}/info";
            }
        }

        if (!string.IsNullOrWhiteSpace(infoUrl))
            ContentProvisioner.ServerInfoUrl = infoUrl;
        Log.Info("MiniStation", $"Provisioner info URL: {ContentProvisioner.ServerInfoUrl}");
    }

    protected override void OnDestroy()
    {
        _destroyed = true;
        _cancel?.Cancel();
        base.OnDestroy();
    }

    // ConnectingOverlay look (official SS14.Launcher, MIT): dark theme background,
    // logo + NanoGold heading, status, progress, random message line.
    private void BuildUi()
    {
        var density = Resources?.DisplayMetrics?.Density ?? 1f;
        _root = new LinearLayout(this)
        {
            Orientation = Orientation.Vertical,
        };
        _root.SetGravity(GravityFlags.Center);
        _root.SetBackgroundColor(LauncherTheme.Bg);
        var pad = (int)(24 * density);
        _root.SetPadding(pad, pad, pad, pad);

        var logo = LauncherTheme.LoadEmbeddedLogo();
        if (logo != null)
        {
            var logoView = new ImageView(this) { ScaleType = ImageView.ScaleType.FitCenter };
            logoView.SetImageBitmap(logo);
            _root.AddView(logoView, new LinearLayout.LayoutParams(
                ViewGroup.LayoutParams.MatchParent, (int)(64 * density))
            { BottomMargin = (int)(12 * density) });
        }

        var title = new TextView(this)
        {
            Text = "Mini Station",
            TextSize = 24,
            TextAlignment = TextAlignment.Center,
        };
        title.SetTextColor(LauncherTheme.NanoGold);
        title.SetTypeface(null, TypefaceStyle.Bold);
        _root.AddView(title, new ViewGroup.LayoutParams(
            ViewGroup.LayoutParams.MatchParent, ViewGroup.LayoutParams.WrapContent));
        _root.AddView(LauncherTheme.GoldLine(this));

        _status = new TextView(this)
        {
            Text = "Подготовка...",
            TextSize = 16,
            Gravity = GravityFlags.Center,
            TextAlignment = TextAlignment.Center,
        };
        _status.SetTextColor(LauncherTheme.Fg);

        _detail = new TextView(this)
        {
            Text = string.Empty,
            TextSize = 13,
            Gravity = GravityFlags.Center,
            TextAlignment = TextAlignment.Center,
        };
        _detail.SetTextColor(LauncherTheme.SubText);

        _bar = new ProgressBar(this, null, global::Android.Resource.Attribute.ProgressBarStyleHorizontal)
        {
            Indeterminate = true,
            Max = 1000,
        };
        _bar.ProgressTintList = global::Android.Content.Res.ColorStateList.ValueOf(LauncherTheme.ControlMid);

        _message = new TextView(this)
        {
            Text = string.Empty,
            TextSize = 12,
            Gravity = GravityFlags.Center,
            TextAlignment = TextAlignment.Center,
        };
        _message.SetTextColor(LauncherTheme.SubText);

        var barLp = new LinearLayout.LayoutParams(ViewGroup.LayoutParams.MatchParent, ViewGroup.LayoutParams.WrapContent)
        {
            TopMargin = pad,
            BottomMargin = (int)(8 * density),
        };
        _root.AddView(_bar, barLp);
        _root.AddView(_status, new LinearLayout.LayoutParams(
            ViewGroup.LayoutParams.MatchParent, ViewGroup.LayoutParams.WrapContent));
        _root.AddView(_detail, new LinearLayout.LayoutParams(
            ViewGroup.LayoutParams.MatchParent, ViewGroup.LayoutParams.WrapContent)
        { TopMargin = (int)(4 * density) });
        _root.AddView(_message, new LinearLayout.LayoutParams(
            ViewGroup.LayoutParams.MatchParent, ViewGroup.LayoutParams.WrapContent)
        { TopMargin = (int)(10 * density) });
        SetContentView(_root);
        _ = RotateMessageAsync();
    }

    private async Task RotateMessageAsync()
    {
        while (!_destroyed)
        {
            var msg = await LauncherTheme.GetRandomMessageAsync();
            if (_destroyed)
                break;
            RunOnUiThread(() =>
            {
                if (!_destroyed)
                    _message.Text = msg;
            });
            await Task.Delay(8000);
        }
    }

    private bool _destroyed;

    private void StartProvisioning()
    {
        if (_running)
            return;
        _running = true;
        _cancel = new CancellationTokenSource();
        _ = Task.Run(() => ProvisionAsync(_cancel.Token));
    }

    private async Task ProvisionAsync(CancellationToken cancel)
    {
        try
        {
            var progress = new Progress<ProvisionProgress>(p => RunOnUiThread(() => ShowProgress(p)));
            var zip = await ContentProvisioner.ProvisionAsync(progress, cancel);
            Log.Info("MiniStation", $"Provisioned: {zip}");
            RunOnUiThread(() => ShowStatus("Запуск клиента..."));
            StartGame(zip);
        }
        catch (System.OperationCanceledException)
        {
            // activity is going away
        }
        catch (Exception ex)
        {
            CrashReporter.WriteCrash("ProvisionAsync", ex);
            Log.Error("MiniStation", $"Provisioning failed: {ex}");
            RunOnUiThread(() =>
            {
                ShowStatus("Ошибка загрузки контента");
                _detail.Text = ex.Message + "\nНажмите, чтобы повторить";
                _bar.Indeterminate = false;
                _bar.Progress = 0;
                _running = false;
                WireRetry();
            });
        }
    }

    private bool _retryWired;

    private void WireRetry()
    {
        if (_retryWired)
            return;
        _retryWired = true;
        _root.Click += (_, _) =>
        {
            if (_running)
                return;
            _detail.Text = string.Empty;
            _bar.Indeterminate = true;
            StartProvisioning();
        };
    }

    private void ShowProgress(ProvisionProgress p)
    {
        var total = p.Total;
        if (total > 0)
        {
            var percent = (int)(p.Done * 100 / total);
            _bar.Indeterminate = false;
            _bar.Progress = percent * 10;
            ShowStatus(p.Status);
            _detail.Text = $"{percent}%  ({p.Done / (1024 * 1024)} / {total / (1024 * 1024)} МБ)";
        }
        else
        {
            _bar.Indeterminate = true;
            ShowStatus(p.Status);
        }
    }

    private void ShowStatus(string text)
    {
        _status.Text = text;
    }

    private void StartGame(string zip)
    {
        var intent = new Intent(this, typeof(MainActivity));
        intent.PutExtra("mount_zip", zip);
        // Пробрасываем параметры запуска (подключение, аккаунт), чтобы можно было стартовать
        // сразу в игру: adb shell am start ... --es connect_addr host:port.
        foreach (var key in LaunchExtras)
        {
            var value = Intent?.GetStringExtra(key);
            if (!string.IsNullOrEmpty(value))
                intent.PutExtra(key, value);
        }
        StartActivity(intent);
        Finish();
    }
}