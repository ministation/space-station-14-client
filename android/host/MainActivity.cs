extern alias ppysdl;
using System;
using System.IO;
using System.IO.Compression;
using System.Reflection;
using Android.App;
using Android.OS;
using Android.Views;
using SDLActivity = ppysdl::Org.Libsdl.App.SDLActivity;

[assembly: UsesPermission(Android.Manifest.Permission.Internet)]
[assembly: UsesPermission(Android.Manifest.Permission.AccessNetworkState)]

namespace Content.Android
{
    [Activity(
        Label = "Space Station 14",
        MainLauncher = false,
        Exported = true,
        Icon = "@mipmap/ic_launcher",
        RoundIcon = "@mipmap/ic_launcher_round",
        Theme = "@android:style/Theme.NoTitleBar.Fullscreen",
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
    public class MainActivity : SDLActivity, global::Android.Hardware.Input.InputManager.IInputDeviceListener
    {
        public static MainActivity? Instance { get; private set; }

        public MainActivity()
        {
            Instance = this;
        }

        public MainActivity(IntPtr handle, global::Android.Runtime.JniHandleOwnership transfer)
            : base(handle, transfer)
        {
            Instance = this;
        }

        public static bool IsEngineReady => (bool?)AppDomain.CurrentDomain.GetData("SS14_ENGINE_READY") ?? false;
        public static bool IsTextInputActive => (bool?)AppDomain.CurrentDomain.GetData("SS14_TEXT_INPUT_ACTIVE") ?? false;

        public static bool IsHardwareKeyboardConnected()
        {
            try
            {
                var cfg = Application.Context?.Resources?.Configuration;
                if (cfg != null && cfg.Keyboard == global::Android.Content.Res.KeyboardType.Qwerty)
                {
                    if (cfg.HardKeyboardHidden != global::Android.Content.Res.HardKeyboardHidden.Yes)
                    {
                        return true;
                    }
                }

                var deviceIds = InputDevice.GetDeviceIds();
                if (deviceIds != null)
                {
                    foreach (var id in deviceIds)
                    {
                        if (id <= 0) continue;
                        var dev = InputDevice.GetDevice(id);
                        if (dev == null) continue;

                        var isKeyboard = (dev.Sources & InputSourceType.Keyboard) == InputSourceType.Keyboard;
                        if (isKeyboard)
                        {
                            if (dev.IsExternal)
                                return true;

                            if (!dev.IsVirtual && (int)dev.KeyboardType == 2)
                                return true;
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                global::Android.Util.Log.Warn("SS14Keyboard", $"Error checking hardware keyboard: {ex.Message}");
            }
            return false;
        }

        public static void DismissSoftKeyboard()
        {
            try
            {
                var imm = (global::Android.Views.InputMethods.InputMethodManager?)Application.Context?.GetSystemService(global::Android.Content.Context.InputMethodService);
                var view = Instance?.Window?.DecorView ?? MSurface;
                if (imm != null && view != null)
                {
                    imm.HideSoftInputFromWindow(view.WindowToken, 0);
                }
            }
            catch {}
        }

        public static void UpdateHardwareKeyboardState()
        {
            var connected = IsHardwareKeyboardConnected();
            AppDomain.CurrentDomain.SetData("SS14_HARDWARE_KEYBOARD_CONNECTED", connected);
            global::Android.Util.Log.Info("SS14Keyboard", $"Hardware keyboard connected: {connected}");
            if (connected)
            {
                Instance?.RunOnUiThread(DismissSoftKeyboard);
            }
        }

        public void OnInputDeviceAdded(int deviceId)
        {
            UpdateHardwareKeyboardState();
        }

        public void OnInputDeviceRemoved(int deviceId)
        {
            UpdateHardwareKeyboardState();
        }

        public void OnInputDeviceChanged(int deviceId)
        {
            UpdateHardwareKeyboardState();
        }

        public override void OnConfigurationChanged(global::Android.Content.Res.Configuration newConfig)
        {
            base.OnConfigurationChanged(newConfig);
            UpdateHardwareKeyboardState();
        }

        public override bool DispatchTouchEvent(MotionEvent? ev)
        {
            if (!IsEngineReady)
                return true;
            return base.DispatchTouchEvent(ev);
        }

        public override bool DispatchKeyEvent(KeyEvent? e)
        {
            if (!IsEngineReady)
                return true;

            if (e != null)
            {
                if (e.KeyCode is Keycode.VolumeUp or Keycode.VolumeDown or Keycode.VolumeMute)
                {
                    return base.DispatchKeyEvent(e);
                }

                if (e.KeyCode == Keycode.Back)
                {
                    if (e.Action == KeyEventActions.Down)
                    {
                        OnBackPressed();
                    }
                    return true;
                }

                if (IsTextInputActive)
                {
                    return base.DispatchKeyEvent(e);
                }

                try
                {
                    var effectiveKey = NormalizeKeycode(e);
                    if (effectiveKey != Keycode.Unknown)
                    {
                        if (HandleKeyEvent(MSurface, (int)effectiveKey, e, null))
                            return true;
                    }
                }
                catch (Exception ex)
                {
                    LogCrash("DispatchKeyEvent", ex);
                }
            }
            return base.DispatchKeyEvent(e);
        }

        private static readonly System.Collections.Concurrent.ConcurrentDictionary<int, Keycode> _downKeyMap = new();

        private static Keycode NormalizeKeycode(KeyEvent e)
        {
            if (e.Action == KeyEventActions.Up)
            {
                if (e.ScanCode > 0 && _downKeyMap.TryRemove(e.ScanCode, out var recordedKey))
                    return recordedKey;
                if ((int)e.KeyCode != 0 && _downKeyMap.TryRemove((int)e.KeyCode, out var recordedByCode))
                    return recordedByCode;
            }

            if (e.ScanCode > 0)
            {
                var mapped = e.ScanCode switch
                {
                    17 => Keycode.W,
                    30 => Keycode.A,
                    31 => Keycode.S,
                    32 => Keycode.D,
                    16 => Keycode.Q,
                    18 => Keycode.E,
                    19 => Keycode.R,
                    20 => Keycode.T,
                    21 => Keycode.Y,
                    22 => Keycode.U,
                    23 => Keycode.I,
                    24 => Keycode.O,
                    25 => Keycode.P,
                    26 => Keycode.LeftBracket,
                    27 => Keycode.RightBracket,
                    33 => Keycode.F,
                    34 => Keycode.G,
                    35 => Keycode.H,
                    36 => Keycode.J,
                    37 => Keycode.K,
                    38 => Keycode.L,
                    39 => Keycode.Semicolon,
                    40 => Keycode.Apostrophe,
                    41 => Keycode.Grave,
                    43 => Keycode.Backslash,
                    44 => Keycode.Z,
                    45 => Keycode.X,
                    46 => Keycode.C,
                    47 => Keycode.V,
                    48 => Keycode.B,
                    49 => Keycode.N,
                    50 => Keycode.M,
                    51 => Keycode.Comma,
                    52 => Keycode.Period,
                    53 => Keycode.Slash,
                    2 => Keycode.Num1,
                    3 => Keycode.Num2,
                    4 => Keycode.Num3,
                    5 => Keycode.Num4,
                    6 => Keycode.Num5,
                    7 => Keycode.Num6,
                    8 => Keycode.Num7,
                    9 => Keycode.Num8,
                    10 => Keycode.Num9,
                    11 => Keycode.Num0,
                    12 => Keycode.Minus,
                    13 => Keycode.Equals,
                    1 => Keycode.Escape,
                    28 => Keycode.Enter,
                    57 => Keycode.Space,
                    14 => Keycode.Del,
                    15 => Keycode.Tab,
                    _ => Keycode.Unknown
                };

                if (mapped != Keycode.Unknown)
                {
                    if (e.Action == KeyEventActions.Down)
                        _downKeyMap[e.ScanCode] = mapped;
                    return mapped;
                }
            }

            var unicode = e.UnicodeChar;
            if (unicode > 0)
            {
                var ch = char.ToLowerInvariant((char)unicode);
                var charMapped = ch switch
                {
                    'w' or 'ц' => Keycode.W,
                    'a' or 'ф' => Keycode.A,
                    's' or 'ы' => Keycode.S,
                    'd' or 'в' => Keycode.D,
                    'q' or 'й' => Keycode.Q,
                    'e' or 'у' => Keycode.E,
                    'r' or 'к' => Keycode.R,
                    't' or 'е' => Keycode.T,
                    'y' or 'н' => Keycode.Y,
                    'u' or 'г' => Keycode.U,
                    'i' or 'ш' => Keycode.I,
                    'o' or 'щ' => Keycode.O,
                    'p' or 'з' => Keycode.P,
                    '[' or 'х' => Keycode.LeftBracket,
                    ']' or 'ъ' => Keycode.RightBracket,
                    'f' or 'а' => Keycode.F,
                    'g' or 'п' => Keycode.G,
                    'h' or 'р' => Keycode.H,
                    'j' or 'о' => Keycode.J,
                    'k' or 'л' => Keycode.K,
                    'l' or 'д' => Keycode.L,
                    ';' or 'ж' => Keycode.Semicolon,
                    '\'' or 'э' => Keycode.Apostrophe,
                    'z' or 'я' => Keycode.Z,
                    'x' or 'ч' => Keycode.X,
                    'c' or 'с' => Keycode.C,
                    'v' or 'м' => Keycode.V,
                    'b' or 'и' => Keycode.B,
                    'n' or 'т' => Keycode.N,
                    'm' or 'ь' => Keycode.M,
                    ',' or 'б' => Keycode.Comma,
                    '.' or 'ю' => Keycode.Period,
                    '`' or 'ё' => Keycode.Grave,
                    _ => Keycode.Unknown
                };

                if (charMapped != Keycode.Unknown)
                {
                    if (e.Action == KeyEventActions.Down)
                    {
                        if (e.ScanCode > 0)
                            _downKeyMap[e.ScanCode] = charMapped;
                        else if ((int)e.KeyCode != 0)
                            _downKeyMap[(int)e.KeyCode] = charMapped;
                    }
                    return charMapped;
                }
            }

            return e.KeyCode;
        }

        public override bool OnGenericMotionEvent(MotionEvent? e)
        {
            if (e != null && e.Action == MotionEventActions.Scroll)
            {
                try
                {
                    var vscroll = e.GetAxisValue(Axis.Vscroll);
                    var hscroll = e.GetAxisValue(Axis.Hscroll);
                    SDLActivity.OnNativeMouse(0, (int)MotionEventActions.Scroll, hscroll, vscroll, false);
                    return true;
                }
                catch (Exception ex)
                {
                    LogCrash("OnGenericMotionEvent", ex);
                }
            }
            return base.OnGenericMotionEvent(e);
        }

        public override void OnBackPressed()
        {
            try
            {
                var now = SystemClock.UptimeMillis();
                var escDown = new KeyEvent(now, now, KeyEventActions.Down, Keycode.Escape, 0);
                var escUp = new KeyEvent(now, now, KeyEventActions.Up, Keycode.Escape, 0);
                HandleKeyEvent(MSurface, (int)Keycode.Escape, escDown, null);
                HandleKeyEvent(MSurface, (int)Keycode.Escape, escUp, null);
            }
            catch (Exception ex)
            {
                LogCrash("OnBackPressed", ex);
            }
        }

        public override bool OnKeyDown(Keycode keyCode, KeyEvent? e)
        {
            if (keyCode == Keycode.Back)
            {
                OnBackPressed();
                return true;
            }
            return base.OnKeyDown(keyCode, e);
        }

        public override bool OnKeyUp(Keycode keyCode, KeyEvent? e)
        {
            if (keyCode == Keycode.Back)
            {
                return true;
            }
            return base.OnKeyUp(keyCode, e);
        }

        private static void LogCrash(string stage, Exception ex)
        {
            try
            {
                CrashReporter.WriteCrash(stage, ex);
            }
            catch {}
        }

        /// <summary>
        /// Catches Java-side (non-.NET) crashes and routes them to crash.txt.
        /// </summary>
        private sealed class JavaCrashHandler : Java.Lang.Object, Java.Lang.Thread.IUncaughtExceptionHandler
        {
            private readonly Java.Lang.Thread.IUncaughtExceptionHandler? _default;

            public JavaCrashHandler(Java.Lang.Thread.IUncaughtExceptionHandler? def)
            {
                _default = def;
            }

            public void UncaughtException(Java.Lang.Thread t, Java.Lang.Throwable e)
            {
                try
                {
                    CrashReporter.WriteJavaCrash($"JavaThread:{t?.Name ?? "?"}",
                        e?.ToString() ?? "<no throwable>");
                }
                catch { }
                try
                {
                    // Give the file write a moment before the process dies.
                    System.Threading.Thread.Sleep(300);
                }
                catch { }
                if (_default != null)
                {
                    try { _default.UncaughtException(t, e); } catch { }
                }
            }
        }

        private static bool _javaHandlerInstalled;

        // [SolomonNumb1] hook JVM uncaught exception handler to capture OS level android crashes before app termination
        private static void InstallJavaCrashHandler()
        {
            if (_javaHandlerInstalled)
                return;
            _javaHandlerInstalled = true;
            try
            {
                var def = Java.Lang.Thread.DefaultUncaughtExceptionHandler;
                if (def is JavaCrashHandler)
                    return;
                Java.Lang.Thread.DefaultUncaughtExceptionHandler = new JavaCrashHandler(def);
            }
            catch (Exception ex)
            {
                try { CrashReporter.WriteCrash("InstallJavaCrashHandler", ex); } catch { }
            }
        }

        static MainActivity()
        {
            try
            {
                AppDomain.CurrentDomain.UnhandledException += (sender, args) =>
                {
                    LogCrash("UnhandledException", args.ExceptionObject as Exception ?? new Exception("Unknown unhandled exception"));
                };
                System.Threading.Tasks.TaskScheduler.UnobservedTaskException += (sender, args) =>
                {
                    LogCrash("UnobservedTaskException", args.Exception);
                };
                try
                {
                    global::Android.Runtime.AndroidEnvironment.UnhandledExceptionRaiser += (sender, args) =>
                    {
                        LogCrash("AndroidEnvironment.Unhandled", args.Exception);
                    };
                }
                catch { }

                AppDomain.CurrentDomain.AssemblyResolve += (sender, args) =>
                {
                    var name = new AssemblyName(args.Name).Name;
                    foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies())
                    {
                        if (assembly.GetName().Name == name)
                        {
                            return assembly;
                        }
                    }
                    return null;
                };

                try
                {
                    System.Environment.SetEnvironmentVariable("MONO_GC_PARAMS", "major=marksweep-conc,nursery-size=8m");
                }
                catch { }

            }
            catch (Exception ex)
            {
                LogCrash("StaticConstructor", ex);
            }
        }

        public static string? LaunchMountZip;
        public static string? LaunchMountDir;
        public static string? LaunchConnectAddress;
        public static string? LaunchSs14Address;
        public static string? LaunchUsername;
        public static string? LaunchToken;
        public static string? LaunchUserId;
        public static string? LaunchPubKey;
        public static readonly System.Collections.Generic.Dictionary<string, string> LaunchBuildCVars = new();

        private static void EnsureResourcesExtracted(string? filesDir)
        {
            if (string.IsNullOrEmpty(filesDir))
                filesDir = AppStorage.FilesDir;
            var zipFile = Path.Combine(filesDir, "resources.zip");
            var verFile = Path.Combine(filesDir, "resources.version");
            var currentVer = typeof(MainActivity).Assembly.GetName().Version?.ToString() ?? "1.3.0";
            try
            {
                if (!File.Exists(zipFile) || new FileInfo(zipFile).Length == 0 || !File.Exists(verFile) || File.ReadAllText(verFile).Trim() != currentVer)
                {
                    var assembly = typeof(MainActivity).Assembly;
                    var resourceName = "Content.Android.resources.zip";
                    var names = assembly.GetManifestResourceNames();
                    foreach (var name in names)
                    {
                        if (name.EndsWith("resources.zip"))
                        {
                            resourceName = name;
                            break;
                        }
                    }
                    using var inStream = assembly.GetManifestResourceStream(resourceName);
                    if (inStream != null)
                    {
                        Directory.CreateDirectory(filesDir);
                        var tempFile = zipFile + ".tmp";
                        using (var outStream = File.Create(tempFile))
                        {
                            inStream.CopyTo(outStream);
                        }
                        File.Move(tempFile, zipFile, true);
                        File.WriteAllText(verFile, currentVer);
                        global::Android.Util.Log.Info("SS14_LOG", $"Extracted fresh resources.zip ({new FileInfo(zipFile).Length} bytes) for version {currentVer}");
                    }
                }
            }
            catch (Exception ex)
            {
                global::Android.Util.Log.Error("SS14_LOG", $"Failed to ensure resources extracted: {ex}");
            }
        }

        protected override void OnCreate(Bundle? savedInstanceState)
        {
            try
            {
                System.Environment.SetEnvironmentVariable("MONO_GC_PARAMS", "major=marksweep-conc,nursery-size=8m");
            }
            catch { }

            InstallJavaCrashHandler();
            // The SDL runtime can reset orientation at startup; enforce
            // landscape in code (manifest attribute alone doesn't stick).
            try
            {
                RequestedOrientation = global::Android.Content.PM.ScreenOrientation.SensorLandscape;
                VolumeControlStream = global::Android.Media.Stream.Music;
            }
            catch { }

            try
            {
                LaunchMountZip = Intent?.GetStringExtra("mount_zip") ?? LaunchMountZip;
                LaunchMountDir = Intent?.GetStringExtra("mount_dir") ?? LaunchMountDir;
                LaunchConnectAddress = Intent?.GetStringExtra("connect_addr") ?? LaunchConnectAddress;
                LaunchSs14Address = Intent?.GetStringExtra("ss14_addr") ?? LaunchSs14Address;
                LaunchUsername = Intent?.GetStringExtra("username") ?? LaunchUsername;
                LaunchToken = Intent?.GetStringExtra("token") ?? LaunchToken;
                LaunchUserId = Intent?.GetStringExtra("userid") ?? LaunchUserId;
                LaunchPubKey = Intent?.GetStringExtra("pubkey") ?? LaunchPubKey;

                LaunchBuildCVars.Clear();

                // Mobile first defaults: low graphics, particles and parallax off.
                // display.max_fps 0 means no cap: the phone's refresh rate is the limit.
                // Extras from the launcher below can override any of these.
                LaunchBuildCVars["display.uiScale"] = UIScaleForResolution(Intent?.GetStringExtra("display_resolution"));
                LaunchBuildCVars["display.vsync"] = "false";
                LaunchBuildCVars["display.max_fps"] = "60";
                LaunchBuildCVars["particles.quality"] = "0";
                LaunchBuildCVars["particles.global_budget"] = "256";
                LaunchBuildCVars["parallax.enabled"] = "false";
                LaunchBuildCVars["parallax.low_quality"] = "true";
                LaunchBuildCVars["light.resolution_scale"] = "0.12";
                LaunchBuildCVars["light.max_light_count"] = "32";
                LaunchBuildCVars["light.max_occluder_count"] = "80";
                // Every rendered light casts shadows, otherwise it shines through
                // walls. The launcher graphics presets override both together.
                LaunchBuildCVars["light.max_shadowcasting_lights"] = "32";
                LaunchBuildCVars["light.wall_bleed"] = "false";
                LaunchBuildCVars["light.ambient_occlusion"] = "false";

                if (Intent?.Extras != null)
                {
                    var extras = Intent.Extras;
                    var keySet = extras.KeySet();
                    if (keySet != null)
                    {
                        foreach (var key in keySet)
                        {
                            if (key != null && key.StartsWith("cvar_build_"))
                            {
                                var cvarName = key.Substring("cvar_build_".Length);
                                var val = extras.GetString(key);
                                if (!string.IsNullOrEmpty(val))
                                    LaunchBuildCVars[cvarName] = val;
                            }
                        }
                    }
                }

                Assembly.Load("Robust.Shared");
                Assembly.Load("Robust.Client");
            }
            catch (Exception ex)
            {
                LogCrash("OnCreateLoadAssemblies", ex);
            }

            try
            {
                EnsureResourcesExtracted(FilesDir?.AbsolutePath);
            }
            catch (Exception ex)
            {
                LogCrash("OnCreateExtractResources", ex);
            }
            base.OnCreate(savedInstanceState);
            Instance = this;
            try
            {
                var im = (global::Android.Hardware.Input.InputManager?)GetSystemService(InputService);
                im?.RegisterInputDeviceListener(this, null);
            }
            catch {}
            AppDomain.CurrentDomain.SetData("SS14_IS_HARDWARE_KEYBOARD_CONNECTED", (Func<bool>)IsHardwareKeyboardConnected);
            AppDomain.CurrentDomain.SetData("SS14_DISMISS_SOFT_KEYBOARD_IF_HARDWARE", (Action)(() =>
            {
                if (IsHardwareKeyboardConnected())
                {
                    Instance?.RunOnUiThread(DismissSoftKeyboard);
                }
            }));
            UpdateHardwareKeyboardState();
            ApplyImmersiveMode();
        }

        protected override void OnDestroy()
        {
            try
            {
                var im = (global::Android.Hardware.Input.InputManager?)GetSystemService(InputService);
                im?.UnregisterInputDeviceListener(this);
            }
            catch {}
            if (Instance == this)
                Instance = null;
            base.OnDestroy();
        }


        /// <summary>
        /// Assign through the attributes property: mutating the object in place does
        /// not reach the window manager once the window is attached, and the cutout
        /// mode has to be in place before the first insets dispatch.
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
                    // Always: the system reports the camera hole in every orientation,
                    // including the long edge when the phone lies in landscape.
                    SetCutoutMode(LayoutInDisplayCutoutMode.Always);
                }
                else if (Build.VERSION.SdkInt >= BuildVersionCodes.P)
                {
                    // Always appeared in Android 11; older releases only have short edges.
                    SetCutoutMode(LayoutInDisplayCutoutMode.ShortEdges);
                }

                Window.AddFlags(WindowManagerFlags.KeepScreenOn);

                if (Build.VERSION.SdkInt >= BuildVersionCodes.R)
                {
                    Window.SetDecorFitsSystemWindows(false);
                    var controller = Window.InsetsController;
                    if (controller != null)
                    {
                        controller.Hide(WindowInsets.Type.StatusBars() | WindowInsets.Type.NavigationBars() | WindowInsets.Type.CaptionBar());
                        controller.SystemBarsBehavior = (int)WindowInsetsControllerBehavior.ShowTransientBarsBySwipe;
                    }
                }
                else
                {
#pragma warning disable CS0618
                    Window.AddFlags(WindowManagerFlags.Fullscreen);
                    Window.DecorView.SystemUiVisibility = (StatusBarVisibility)(
                        SystemUiFlags.Fullscreen |
                        SystemUiFlags.HideNavigation |
                        SystemUiFlags.ImmersiveSticky |
                        SystemUiFlags.LayoutStable |
                        SystemUiFlags.LayoutHideNavigation |
                        SystemUiFlags.LayoutFullscreen);
#pragma warning restore CS0618
                }

                Window.SetSoftInputMode(SoftInput.StateAlwaysHidden);
                InstallSafeAreaListener();
            }
            catch (Exception ex)
            {
                LogCrash("ApplyImmersiveMode", ex);
            }
        }

        private void EnsureSurfaceFocus()
        {
            try
            {
                if (MSurface != null)
                {
                    MSurface.Focusable = true;
                    MSurface.FocusableInTouchMode = true;
                    MSurface.RequestFocus();
                }
            }
            catch {}
        }

        // [SolomonNumb1] downscales render surface to 720p/900p/1080p, drastically improving framerates on L GPUs(telling this with my mali g57 mc2,lmao)
        private void ApplyDisplayResolution()
        {
            try
            {
                if (MSurface?.Holder == null) return;
                var targetH = RenderHeightForResolution(Intent?.GetStringExtra("display_resolution"));
                if (targetH <= 0)
                {
                    MSurface.Holder.SetSizeFromLayout();
                    return;
                }

                var metrics = Resources?.DisplayMetrics;
                if (metrics == null) return;

                var screenW = Math.Max(metrics.WidthPixels, metrics.HeightPixels);
                var screenH = Math.Min(metrics.WidthPixels, metrics.HeightPixels);
                if (screenH <= 0) return;
                var aspect = (float)screenW / screenH;

                if (targetH < screenH)
                {
                    int targetW = (int)Math.Round(targetH * aspect);
                    global::Android.Util.Log.Info("SS14Launch", $"[Resolution] Setting Surface size to {targetW}x{targetH} (native is {screenW}x{screenH})");
                    MSurface.Holder.SetFixedSize(targetW, targetH);
                    // SDL sizes the surface view to the render resolution, which
                    // leaves the game in the top left corner of the screen. The
                    // view has to stay full screen so Android scales the buffer.
                    ForceSurfaceFullscreen();
                }
                else
                {
                    MSurface.Holder.SetSizeFromLayout();
                }
            }
            catch (Exception ex)
            {
                global::Android.Util.Log.Warn("SS14Launch", $"ApplyDisplayResolution failed: {ex.Message}");
            }
        }

        private void ForceSurfaceFullscreen(int retries = 2)
        {
            try
            {
                if (MSurface?.LayoutParameters == null)
                    return;

                // SDL puts the surface in a RelativeLayout, so the existing
                // params must be reused instead of replaced by ViewGroup ones.
                var lp = MSurface.LayoutParameters;
                if (lp.Width == ViewGroup.LayoutParams.MatchParent &&
                    lp.Height == ViewGroup.LayoutParams.MatchParent)
                {
                    return;
                }

                lp.Width = ViewGroup.LayoutParams.MatchParent;
                lp.Height = ViewGroup.LayoutParams.MatchParent;
                MSurface.LayoutParameters = lp;
                MSurface.RequestLayout();
                if (retries > 0)
                    MSurface.PostDelayed(() => ForceSurfaceFullscreen(retries - 1), 500);
            }
            catch (Exception ex)
            {
                global::Android.Util.Log.Warn("SS14Launch", $"ForceSurfaceFullscreen failed: {ex.Message}");
            }
        }

        // [SolomonNumb1] enforces SensorLandscape orientation, preventing SDL runtime from resetting display mode
        public override global::Android.Content.PM.ScreenOrientation RequestedOrientation
        {
            get => base.RequestedOrientation;
            set
            {
                // SDL runtime requests FULL_USER or PORTRAIT during window creation;
                // unconditionally enforce SensorLandscape so orientation never flips.
                base.RequestedOrientation = global::Android.Content.PM.ScreenOrientation.SensorLandscape;
            }
        }

        protected override void OnResume()
        {
            base.OnResume();
            try
            {
                if (RequestedOrientation != global::Android.Content.PM.ScreenOrientation.SensorLandscape)
                    RequestedOrientation = global::Android.Content.PM.ScreenOrientation.SensorLandscape;
            }
            catch { }
            UpdateHardwareKeyboardState();
            ApplyImmersiveMode();
            EnsureSurfaceFocus();
            ApplyDisplayResolution();
        }

        public override void OnWindowFocusChanged(bool hasFocus)
        {
            base.OnWindowFocusChanged(hasFocus);
            if (hasFocus)
            {
                try
                {
                    // SDL sets its own orientation at runtime, stomping the
                    // manifest; re-assert after focus lands (and again shortly
                    // after, to win any late SDL reset).
                    RequestedOrientation = global::Android.Content.PM.ScreenOrientation.SensorLandscape;
                    var view = Window?.DecorView;
                    view?.PostDelayed(() =>
                    {
                        try
                        {
                            if (RequestedOrientation != global::Android.Content.PM.ScreenOrientation.SensorLandscape)
                                RequestedOrientation = global::Android.Content.PM.ScreenOrientation.SensorLandscape;
                        }
                        catch { }
                    }, 500);
                }
                catch { }
                ApplyImmersiveMode();
                EnsureSurfaceFocus();
                ApplyDisplayResolution();
            }
        }

        protected override string[] GetLibraries()
        {
            return new[]
            {
                "SDL3",
                "sodium",
                "zstd",
                "freetype6",
                "openal"
            };
        }

        private sealed class AndroidLogcatWriter : TextWriter
        {
            private readonly object _lock = new();
            private readonly System.Text.StringBuilder _sb = new();
            private readonly string _tag;
            private readonly string? _filePath;
            private StreamWriter? _writer;
            private long _fileLength;

            public AndroidLogcatWriter(string tag, string? filePath = null)
            {
                _tag = tag;
                _filePath = filePath;
                InitWriter();
            }

            private void InitWriter()
            {
                if (string.IsNullOrEmpty(_filePath))
                    return;

                try
                {
                    var fi = new FileInfo(_filePath);
                    if (fi.Exists && fi.Length > 2 * 1024 * 1024)
                    {
                        fi.Delete();
                    }
                    _writer = new StreamWriter(File.Open(_filePath, FileMode.Create, FileAccess.Write, FileShare.ReadWrite)) { AutoFlush = true };
                    _fileLength = 0;
                }
                catch { }
            }

            public override System.Text.Encoding Encoding => System.Text.Encoding.UTF8;

            public override void Write(char value)
            {
                lock (_lock)
                {
                    if (value == '\n')
                    {
                        var line = _sb.ToString();
                        _sb.Clear();
                        global::Android.Util.Log.Info(_tag, line);
                        if (_writer != null)
                        {
                            try
                            {
                                _writer.WriteLine(line);
                                _fileLength += line.Length + 1;
                                if (_fileLength > 2 * 1024 * 1024)
                                {
                                    _writer.Dispose();
                                    InitWriter();
                                }
                            }
                            catch { }
                        }
                    }
                    else if (value != '\r')
                    {
                        _sb.Append(value);
                    }
                }
            }

            public override void Write(string? value)
            {
                if (value == null)
                    return;

                lock (_lock)
                {
                    for (int i = 0; i < value.Length; i++)
                    {
                        var c = value[i];
                        if (c == '\n')
                        {
                            var line = _sb.ToString();
                            _sb.Clear();
                            global::Android.Util.Log.Info(_tag, line);
                            if (_writer != null)
                            {
                                try
                                {
                                    _writer.WriteLine(line);
                                    _fileLength += line.Length + 1;
                                    if (_fileLength > 2 * 1024 * 1024)
                                    {
                                        _writer.Dispose();
                                        InitWriter();
                                    }
                                }
                                catch { }
                            }
                        }
                        else if (c != '\r')
                        {
                            _sb.Append(c);
                        }
                    }
                }
            }
        }


        private static void CleanLegacyContentFiles(string filesDir)
        {
            try
            {
                foreach (var file in Directory.EnumerateFiles(filesDir, "*", SearchOption.TopDirectoryOnly))
                {
                    var ext = Path.GetExtension(file);
                    if (ext.Equals(".dll", StringComparison.OrdinalIgnoreCase) ||
                        ext.Equals(".pdb", StringComparison.OrdinalIgnoreCase))
                    {
                        File.Delete(file);
                    }
                }
            }
            catch (Exception ex)
            {
                global::Android.Util.Log.Warn("SS14_LOG", $"Legacy content cleanup failed: {ex.Message}");
            }
        }

        protected override void Main()
        {
            try
            {
                var filesDir = AppStorage.FilesDir;
                // Старая схема клала Content.*.dll прямо в files: движок видел их как второй модуль.
                CleanLegacyContentFiles(filesDir);
                try
                {
                    var logWriter = new AndroidLogcatWriter("SS14_LOG", Path.Combine(filesDir, "game.log"));
                    Console.SetOut(logWriter);
                    Console.SetError(logWriter);
                    global::Android.Util.Log.Info("SS14_LOG", "=== Console redirected to AndroidLogcatWriter ===");
                }
                catch (Exception ex)
                {
                    global::Android.Util.Log.Error("SS14_LOG", $"Failed to redirect console: {ex}");
                }

                var robustShared = Assembly.Load("Robust.Shared");
                if (robustShared == null) throw new Exception("robustShared assembly is null");
                var robustClient = Assembly.Load("Robust.Client");
                if (robustClient == null) throw new Exception("robustClient assembly is null");

                var programSharedType = robustShared.GetType("Robust.Shared.ProgramShared");
                if (programSharedType == null)
                {
                    foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
                    {
                        var t = asm.GetType("Robust.Shared.ProgramShared");
                        if (t != null)
                        {
                            programSharedType = t;
                            break;
                        }
                    }
                }
                if (programSharedType == null) throw new Exception("Robust.Shared.ProgramShared type is null");
                var pathOffsetField = programSharedType.GetField("PathOffset", BindingFlags.Public | BindingFlags.Static);
                if (pathOffsetField == null) throw new Exception("PathOffset field is null");
                pathOffsetField.SetValue(null, filesDir + "/");

                AuthBridge.ApplyEnvironment();

                var mountZip = LaunchMountZip;
                var mountDir = LaunchMountDir;
                var connectAddr = LaunchConnectAddress;
                var ss14Addr = LaunchSs14Address;
                var username = LaunchUsername;
                var token = LaunchToken;
                var userId = LaunchUserId;
                var pubKey = LaunchPubKey;

                var authPath = Path.Combine(filesDir, "auth.json");
                if (File.Exists(authPath))
                {
                    try
                    {
                        var json = File.ReadAllText(authPath);
                        using var doc = System.Text.Json.JsonDocument.Parse(json);
                        var root = doc.RootElement;
                        if (string.IsNullOrEmpty(username) && root.TryGetProperty("username", out var u))
                            username = u.GetString();
                        if (string.IsNullOrEmpty(token) && root.TryGetProperty("token", out var tok))
                            token = tok.GetString();
                        if (string.IsNullOrEmpty(userId) && root.TryGetProperty("userId", out var uid))
                            userId = uid.GetString();
                    }
                    catch {}
                }

                if (!string.IsNullOrEmpty(token))
                {
                    System.Environment.SetEnvironmentVariable("ROBUST_AUTH_TOKEN", token);
                }
                if (!string.IsNullOrEmpty(userId))
                {
                    System.Environment.SetEnvironmentVariable("ROBUST_AUTH_USERID", userId);
                }
                if (!string.IsNullOrEmpty(pubKey))
                {
                    System.Environment.SetEnvironmentVariable("ROBUST_AUTH_PUBKEY", pubKey);
                }
                if (string.IsNullOrEmpty(System.Environment.GetEnvironmentVariable("ROBUST_AUTH_SERVER")))
                {
                    System.Environment.SetEnvironmentVariable("ROBUST_AUTH_SERVER", "https://auth.spacestation14.com/");
                }

                var hasZip = !string.IsNullOrEmpty(mountZip) && File.Exists(mountZip);

                // The insets listener runs on the UI thread after the window is laid
                // out. Main() runs on the SDL thread and used to read the decor before
                // it had any insets, which is why the cutout frame was missing.
                WaitForSafeAreaInsets();

                var args = new List<string>
                {
                    "--cvar", "thread.parallel_count=4",
                    "--cvar", "light.blur=false",
                    "--cvar", "light.soft_shadows=false",
                    "--cvar", "light.ambient_occlusion=false",
                    "--cvar", "light.resolution_scale=0.15",
                    "--cvar", "light.max_light_count=32",
                    "--cvar", "light.max_occluder_count=96",
                    "--cvar", "light.max_shadowcasting_lights=32",
                    "--cvar", "light.wall_bleed=true",
                    "--cvar", "parallax.enabled=false",
                    "--cvar", "viewport.scale_render=false",
                    "--cvar", "display.vsync=false",
                    "--cvar", "display.max_fps=0",
                    "--cvar", "audio.max_ambient_sources=16",
                    "--cvar", "audio.max_channels=24",
                    "--cvar", "net.logging=false",
                    "--cvar", "mobile.touch_controls=true",
                    "--cvar", "hud.fps_counter_visible=true",
#if DEBUG
                    "--cvar", "hud.version_watermark=true",
                    "--cvar", "log.level=Debug",
#else
                    "--cvar", "hud.version_watermark=false",
                    "--cvar", "log.level=Info",
#endif
                };

                if (!string.IsNullOrEmpty(mountDir) && Directory.Exists(mountDir))
                {
                    args.Add("--mount-dir");
                    args.Add(mountDir);
                }
                else if (hasZip)
                {
                    args.Add("--mount-zip");
                    args.Add(mountZip!);
                }

                if (!string.IsNullOrEmpty(connectAddr))
                {
                    args.Add("--connect");
                    args.Add("--connect-address");
                    args.Add(connectAddr);
                    if (!string.IsNullOrEmpty(ss14Addr))
                    {
                        args.Add("--ss14-address");
                        args.Add(ss14Addr);
                    }
                    args.Add("--launcher");
                    if (!string.IsNullOrEmpty(username))
                    {
                        args.Add("--username");
                        args.Add(username);
                    }

                }

                // Defaults and launcher extras are passed on the connect path and the
                // offline path alike: the offline menu needs the same resolution, UI
                // scale and graphics settings as a server launch.
                foreach (var (cvarName, val) in LaunchBuildCVars)
                {
                    // Launcher metadata lives under the build.* namespace, while user settings
                    // and graphics defaults are ordinary engine/content CVars and must keep their name.
                    var isBuildMeta = cvarName is "engine_version" or "fork_id" or "version" or "download_url"
                        or "manifest_url" or "manifest_download_url" or "manifest_hash" or "hash" or "source_url" or "source_commit_id";
                    var cvarKey = isBuildMeta ? $"build.{cvarName}" : cvarName;
                    args.Add("--cvar");
                    args.Add($"{cvarKey}={val}");
                    global::Android.Util.Log.Info("SS14Launch", $"[Main] CVar: {cvarKey}={val}");
                }

                // Cutout frame in physical render pixels: the engine keeps the same
                // physical border on screen whatever the render resolution is.
                args.Add("--cvar");
                args.Add($"mobile.safe_area_left={SafeInset(_safeAreaLeft)}");
                args.Add("--cvar");
                args.Add($"mobile.safe_area_right={SafeInset(_safeAreaRight)}");
                args.Add("--cvar");
                args.Add($"mobile.safe_area_top={SafeInset(_safeAreaTop)}");
                args.Add("--cvar");
                args.Add($"mobile.safe_area_bottom={SafeInset(_safeAreaBottom)}");

                var gcType = robustClient.GetType("Robust.Client.GameController");
                if (gcType == null) throw new Exception("Robust.Client.GameController type is null");
                var startMethod = gcType.GetMethod("Start", BindingFlags.Public | BindingFlags.Static);
                if (startMethod == null) throw new Exception("Start method on GameController is null");

                var optsType = robustClient.GetType("Robust.Client.GameControllerOptions");
                var options = Activator.CreateInstance(optsType!);
                optsType?.GetProperty("Sandboxing")?.SetValue(options, false);

                // Load content assemblies into the default ALC (Assembly.LoadFrom), like the PC client.
                // Engine assemblies come from the APK, content assemblies from FilesDir.
                var contentStart = true;
                global::Android.Util.Log.Info("SS14Launch", $"[Main] Starting GameController: contentStart={contentStart}, hasZip={hasZip}, mountZip={mountZip}, connectAddr={connectAddr}, user={username}");

                startMethod.Invoke(null, new object?[] { args.ToArray(), options, contentStart, null });
            }

            catch (Exception ex)
            {
                LogCrash("Main", ex);
                throw;
            }
        }

    private int _safeAreaLeft;
    private int _safeAreaRight;
    private int _safeAreaTop;
    private int _safeAreaBottom;
    private volatile bool _safeAreaReady;

    /// <summary>
    /// UI scale per render resolution. A smaller render target is stretched to the
    /// whole screen, so the scale shrinks with it to keep the UI the same physical
    /// size. Hard-coded on purpose: the resolution is the only setting.
    /// </summary>
    private static string UIScaleForResolution(string? resolution) => resolution switch
    {
        "720p" => "1.50",
        "900p" => "1.75",
        "1080p" => "2.00",
        _ => "2.25",
    };

    /// <summary>
    /// Render surface height for a display_resolution extra; 0 means native.
    /// </summary>
    private static int RenderHeightForResolution(string? resolution) => resolution switch
    {
        "720p" => 720,
        "900p" => 900,
        "1080p" => 1080,
        _ => 0,
    };

    /// <summary>
    /// Display cutout insets are measured in physical screen pixels, while the
    /// engine works in the downscaled render surface. Scale them so the safe area
    /// is the same fraction of the screen the launcher editor draws on.
    /// </summary>
    private float SurfaceScale()
    {
        try
        {
            var metrics = Resources?.DisplayMetrics;
            if (metrics == null)
                return 1f;
            var screenH = Math.Min(metrics.WidthPixels, metrics.HeightPixels);
            if (screenH <= 0)
                return 1f;
            var targetH = RenderHeightForResolution(Intent?.GetStringExtra("display_resolution"));
            return targetH > 0 && targetH < screenH ? targetH / (float)screenH : 1f;
        }
        catch
        {
            return 1f;
        }
    }

    private int SafeInset(int value) => (int)Math.Round(value * SurfaceScale());

    /// <summary>
    /// Insets are read on the UI thread from the window callback. Main() runs on
    /// the SDL thread, so it waits for that callback instead of reading the decor
    /// before the window has any insets.
    /// </summary>
    private void InstallSafeAreaListener()
    {
        try
        {
            var decor = Window?.DecorView;
            if (decor == null)
                return;

            if (!OperatingSystem.IsAndroidVersionAtLeast(28))
            {
                CaptureSafeAreaInsets();
                return;
            }

            decor.SetOnApplyWindowInsetsListener(new SafeAreaInsetsListener(OnWindowInsetsChanged));
            decor.RequestApplyInsets();
        }
        catch (Exception ex)
        {
            global::Android.Util.Log.Warn("SS14Launch", $"InstallSafeAreaListener failed: {ex.Message}");
        }
    }

    private void OnWindowInsetsChanged(View view, WindowInsets insets)
    {
        try
        {
            ApplySafeArea(insets, Resources?.DisplayMetrics);
        }
        catch
        {
            // Insets are cosmetic: the percentage frame still applies.
        }
    }

    private void WaitForSafeAreaInsets()
    {
        var deadline = SystemClock.UptimeMillis() + 2000;
        while (!_safeAreaReady && SystemClock.UptimeMillis() < deadline)
            System.Threading.Thread.Sleep(20);

        if (!_safeAreaReady)
        {
            global::Android.Util.Log.Warn("SS14Launch", "[SafeArea] listener did not fire in 2s, reading insets directly");
            CaptureSafeAreaInsets();
        }

        global::Android.Util.Log.Info("SS14Launch",
            $"[SafeArea] final ready={_safeAreaReady} surfaceScale={SurfaceScale():F3} " +
            $"physical l={_safeAreaLeft} t={_safeAreaTop} r={_safeAreaRight} b={_safeAreaBottom} " +
            $"passed l={SafeInset(_safeAreaLeft)} t={SafeInset(_safeAreaTop)} r={SafeInset(_safeAreaRight)} b={SafeInset(_safeAreaBottom)}");
    }

    private void ApplySafeArea(WindowInsets? insets, global::Android.Util.DisplayMetrics? metrics)
    {
        if (metrics == null || metrics.WidthPixels <= 0 || metrics.HeightPixels <= 0)
            return;

        var screenW = Math.Max(metrics.WidthPixels, metrics.HeightPixels);
        var screenH = Math.Min(metrics.WidthPixels, metrics.HeightPixels);
        var (left, top, right, bottom) = MobileSafeArea.Measure(insets, screenW, screenH);

        _safeAreaLeft = left;
        _safeAreaTop = top;
        _safeAreaRight = right;
        _safeAreaBottom = bottom;
        _safeAreaReady = true;

        global::Android.Util.Log.Info("SS14Launch",
            $"[SafeArea] l={left} t={top} r={right} b={bottom} screen={screenW}x{screenH}");
    }

    /// <summary>
    /// Last resort for devices that never dispatch an insets callback: Main()
    /// calls this after the wait times out.
    /// </summary>
    private void CaptureSafeAreaInsets()
    {
        try
        {
            var (left, top, right, bottom) = MobileSafeArea.MeasureWindow(this);
            _safeAreaLeft = left;
            _safeAreaTop = top;
            _safeAreaRight = right;
            _safeAreaBottom = bottom;
            _safeAreaReady = true;
        }
        catch
        {
            // Insets are cosmetic here: fall back to the percentage frame.
        }
    }

    private sealed class SafeAreaInsetsListener : Java.Lang.Object, View.IOnApplyWindowInsetsListener
    {
        private readonly Action<View, WindowInsets> _apply;

        public SafeAreaInsetsListener(Action<View, WindowInsets> apply) => _apply = apply;

        public WindowInsets OnApplyWindowInsets(View view, WindowInsets insets)
        {
            _apply(view, insets);
            return insets;
        }
    }
    }

    /// <summary>
    /// Safe area frame in physical screen pixels. The game passes it to the engine
    /// (scaled to the render surface) and the launcher pad editor draws it as is,
    /// so the black border is the same in the preview and in the game.
    /// </summary>
    internal static class MobileSafeArea
    {
        // Even when Android reports no inset, the gesture strip and the rounded
        // corners live in the bottom strip; keep a thin frame there.
        private const float BottomFrameFraction = 0.015f;

        // The player asked for a camera frame three times wider than the cutout:
        // the reported inset reads as a narrow black line.
        private const float FrameMultiplier = 3f;

        public static (int Left, int Top, int Right, int Bottom) Measure(
            WindowInsets? insets, int screenWidth, int screenHeight)
        {
            var screenW = Math.Max(screenWidth, screenHeight);
            var screenH = Math.Min(screenWidth, screenHeight);

            var left = 0;
            var top = 0;
            var right = 0;
            var cameraBottom = 0;
            var gestureBottom = 0;

            if (insets != null && OperatingSystem.IsAndroidVersionAtLeast(28))
            {
                if (OperatingSystem.IsAndroidVersionAtLeast(30))
                {
                    // Hidden navigation bars still own the bottom gesture strip.
                    gestureBottom = insets.GetInsets(WindowInsets.Type.SystemGestures()).Bottom;
                    // The cutout stays reported while the system bars are hidden.
                    var cut = insets.GetInsetsIgnoringVisibility(WindowInsets.Type.DisplayCutout());
                    left = cut.Left;
                    top = cut.Top;
                    right = cut.Right;
                    cameraBottom = cut.Bottom;
                }

                var cutout = insets.DisplayCutout;
                if (cutout != null)
                {
                    left = Math.Max(left, cutout.SafeInsetLeft);
                    top = Math.Max(top, cutout.SafeInsetTop);
                    right = Math.Max(right, cutout.SafeInsetRight);
                    cameraBottom = Math.Max(cameraBottom, cutout.SafeInsetBottom);

                    // Some devices fill only the bounding rects. Take the depth of
                    // the rects that touch a window edge.
                    var rects = cutout.BoundingRects;
                    if (rects != null)
                    {
                        foreach (var rect in rects)
                        {
                            if (rect.Left <= 1)
                                left = Math.Max(left, rect.Right);
                            else if (rect.Right >= screenW - 1)
                                right = Math.Max(right, screenW - rect.Left);
                            if (rect.Top <= 1)
                                top = Math.Max(top, rect.Bottom);
                            else if (rect.Bottom >= screenH - 1)
                                cameraBottom = Math.Max(cameraBottom, screenH - rect.Top);
                        }
                    }
                }
            }

            // Only the camera gets the multiplier. The gesture strip is already wide
            // and tripling it put a huge black bar over the bottom of the screen.
            var bottom = Math.Max(
                (int)Math.Round(cameraBottom * FrameMultiplier),
                Math.Max(gestureBottom, (int)Math.Round(screenH * BottomFrameFraction)));

            return (
                (int)Math.Round(left * FrameMultiplier),
                (int)Math.Round(top * FrameMultiplier),
                (int)Math.Round(right * FrameMultiplier),
                bottom);
        }

        /// <summary>
        /// Reads the frame from the activity window. RootWindowInsets are what the
        /// window was laid out with; CurrentWindowMetrics is the fallback for a
        /// window that has not dispatched insets yet.
        /// </summary>
        public static (int Left, int Top, int Right, int Bottom) MeasureWindow(Activity? activity)
        {
            var metrics = activity?.Resources?.DisplayMetrics;
            if (metrics == null || metrics.WidthPixels <= 0 || metrics.HeightPixels <= 0)
                return (0, 0, 0, 0);

            WindowInsets? insets = null;
            if (OperatingSystem.IsAndroidVersionAtLeast(28))
            {
                insets = activity!.Window?.DecorView?.RootWindowInsets;
                if (insets == null && OperatingSystem.IsAndroidVersionAtLeast(30))
                {
                    var windowManager =
                        activity.GetSystemService(global::Android.Content.Context.WindowService) as IWindowManager;
                    insets = windowManager?.CurrentWindowMetrics?.WindowInsets;
                }
            }

            return Measure(insets, metrics.WidthPixels, metrics.HeightPixels);
        }
    }
}
