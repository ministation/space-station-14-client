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
    private TextView _status = default!;
    private TextView _detail = default!;
    private ProgressBar _bar = default!;
    private LinearLayout _root = default!;
    private CancellationTokenSource? _cancel;
    private bool _running;

    protected override void OnCreate(Bundle? savedInstanceState)
    {
        base.OnCreate(savedInstanceState);
        Window?.AddFlags(WindowManagerFlags.KeepScreenOn);
        BuildUi();
        StartProvisioning();
    }

    protected override void OnDestroy()
    {
        _cancel?.Cancel();
        base.OnDestroy();
    }

    private void BuildUi()
    {
        _root = new LinearLayout(this)
        {
            Orientation = Orientation.Vertical,
        };
        _root.SetGravity(GravityFlags.Center);
        _root.SetBackgroundColor(global::Android.Graphics.Color.ParseColor("#15151A"));
        var pad = (int)(24 * Resources!.DisplayMetrics!.Density);
        _root.SetPadding(pad, pad, pad, pad);

        var title = new TextView(this)
        {
            Text = "Mini Station",
            TextSize = 32,
            TextAlignment = TextAlignment.Center,
        };
        title.SetTextColor(global::Android.Graphics.Color.ParseColor("#E8E8F0"));

        _status = new TextView(this) { Text = "Подготовка...", TextSize = 18, Gravity = GravityFlags.Center };
        _status.SetTextColor(global::Android.Graphics.Color.ParseColor("#B8B8C8"));

        _detail = new TextView(this) { Text = string.Empty, TextSize = 14, Gravity = GravityFlags.Center };
        _detail.SetTextColor(global::Android.Graphics.Color.ParseColor("#8888A0"));

        _bar = new ProgressBar(this, null, global::Android.Resource.Attribute.ProgressBarStyleHorizontal)
        {
            Indeterminate = true,
            Max = 1000,
        };

        _root.AddView(title);
        _root.AddView(_bar, new LinearLayout.LayoutParams(ViewGroup.LayoutParams.MatchParent, (int)(8 * Resources!.DisplayMetrics!.Density)) { TopMargin = pad, BottomMargin = pad / 2 });
        _root.AddView(_status);
        _root.AddView(_detail);
        SetContentView(_root);
    }

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
                _root.Clickable = true;
                _root.Click += (_, _) =>
                {
                    _root.Clickable = false;
                    _detail.Text = string.Empty;
                    _bar.Indeterminate = true;
                    StartProvisioning();
                };
            });
        }
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
        StartActivity(intent);
        Finish();
    }
}