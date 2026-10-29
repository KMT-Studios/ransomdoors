using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Forms;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using WpfApplication = System.Windows.Application;
using XamlAnimatedGif;

namespace rans0m;

public partial class Overlay : Window
{
    private readonly KeyboardHook _keyboardHook = new();
    private readonly Border[] _dpbSegments;
    private readonly DispatcherTimer _topMostTimer;
    private readonly CancellationTokenSource _shutdown = new();

    private TopMostEnforcer? _topMostEnforcer;
    private NotifyIcon? _trayIcon;
    private CancellationTokenSource? _spawnDelayCts;
    private CancellationTokenSource? _twitchCts;
    private TwitchBridge? _twitch;
    private DispatcherTimer? _downloadTextTimer;
    private DispatcherTimer? _downloadBarTimer;
    private DispatcherTimer? _shakeTimer;
    private int _shakeTicksRemaining;
    private DateTimeOffset _lastNoSpawnWarning = DateTimeOffset.MinValue;

    public Overlay()
    {
        InitializeComponent();

        _dpbSegments = [Seg0, Seg1, Seg2, Seg3, Seg4, Seg5, Seg6, Seg7, Seg8, Seg9];
        _topMostTimer = new DispatcherTimer(DispatcherPriority.Render, Dispatcher)
        {
            Interval = TimeSpan.FromSeconds(1),
        };

        // Subscribed before the images decode, so a GIF that finishes loading while the overlay is
        // parked still gets paused instead of animating forever in the background.
        AnimationBehavior.AddLoadedHandler(staticBg, OnGifLoaded);
        AnimationBehavior.AddLoadedHandler(redVignette, OnGifLoaded);
        AnimationBehavior.AddLoadedHandler(img_attack, OnGifLoaded);

        _keyboardHook.KeyPressed += OnKeyPressed;
        _keyboardHook.Hook();

        Closing += OnClosing;
    }

    private void OnKeyPressed(Keys key) => Global.State.MouseSpy.OnKeyPressed(key);

    private async void Window_Loaded(object sender, RoutedEventArgs e)
    {
        Global.OverlayWindow = this;

        await ResetRansom();

        // Click-through must be applied after the handle exists and before any ransom starts.
        MakeClickThrough();

        Left = 0;
        Top = 0;
        Width = SystemParameters.PrimaryScreenWidth;
        Height = SystemParameters.PrimaryScreenHeight;
        WindowState = WindowState.Maximized;

        RegisterFileTypes();

        SetupTrayIcon();
        SetupTopMost();

        if (!Config.LoadConfig())
        {
            new ConfigWindow().ShowDialog();
        }

        RansomLoop();

        StartTwitch();
    }

    /// <summary>
    /// Starts the Twitch redemption listener, if the user configured one. Failures are logged and
    /// swallowed: the app has to stay usable as a plain prank even when Twitch is unreachable.
    /// </summary>
    private void StartTwitch()
    {
        if (!Config.Current.Twitch.Enabled)
        {
            return;
        }

        _twitch = new TwitchBridge();
        _twitch.Log += message => Console.WriteLine($"[twitch] {message}");

        _twitchCts = new CancellationTokenSource();

        _ = Task.Run(async () =>
        {
            try
            {
                await _twitch.StartAsync(_twitchCts.Token);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[twitch] could not start: {ex.Message}");
            }
        });
    }

    private async Task StopTwitchAsync()
    {
        if (_twitch is null)
        {
            return;
        }

        try
        {
            if (_twitchCts is not null)
            {
                await _twitchCts.CancelAsync();
            }

            await _twitch.StopAsync();
            await _twitch.DisposeAsync();
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[twitch] shutdown problem: {ex.Message}");
        }
        finally
        {
            _twitch = null;
        }
    }

    /// <summary>
    /// Starts the redemption listener, or stops it if Twitch was just turned off. Called from the
    /// config window on close, because enabling Twitch there would otherwise do nothing until the
    /// next launch: with timer spawning suppressed, a bridge that never starts means nothing ever
    /// spawns at all.
    /// </summary>
    public async void ApplyTwitchSettingsAsync()
    {
        try
        {
            if (!Config.Current.Twitch.Enabled)
            {
                await StopTwitchAsync();
                return;
            }

            // Already listening, so leave the existing connection and subscription alone.
            if (_twitch is not null)
            {
                return;
            }

            StartTwitch();
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[twitch] could not apply settings: {ex.Message}");
        }
    }

    private void RegisterFileTypes()
    {
        (string Extension, string IconPath, string FileTypeName)[] fileTypes =
        [
            (".gold1", "Gold/Gold1.ico", "GoldFile1"),
            (".gold2", "Gold/Gold2.ico", "GoldFile2"),
            (".gold3", "Gold/Gold3.ico", "GoldFile3"),
            (".gold4", "Gold/Gold4.ico", "GoldFile4"),
            (".gold5", "Gold/Gold5.ico", "GoldFile5"),
            (".gold6", "Gold/HoneyPot.ico", "GoldFile6"),
            (".crucifix", "crucifix.ico", "CrucifixFile"),
        ];

        foreach ((string extension, string iconPath, string fileTypeName) in fileTypes)
        {
            FileTypeRegister.RegisterIconForExtension(extension, Global.GetBytesFromResource(iconPath), fileTypeName);
        }
    }

    private void OnClosing(object? sender, CancelEventArgs e)
    {
        // Hide the tray icon before shutdown so it does not linger as a ghost.
        _trayIcon?.Visible = false;

        _shutdown.Cancel();
        _keyboardHook.Dispose();

        // Best effort: the socket is killed synchronously below, but the HTTP revoke may not
        // finish before the process exits. Startup clears any leftovers, so this is not critical.
        if (_twitch is not null)
        {
            _twitchCts?.Cancel();
            _ = StopTwitchAsync();
        }

        FullCleanup();
    }

    /// <summary>
    /// Removes every trace RANS0M leaves on the system: coin files, the drawer temp folder,
    /// the file type registrations and their icons, the wallpaper, and the cursor.
    /// </summary>
    private void FullCleanup()
    {
        GoldCoinManager.DeleteAllCoins();
        FileTypeRegister.UnregisterAllGoldFileTypes();
        WallpaperUpdater.RestoreWallpaper();
        CursorUpdater.RestoreCursor();
    }

    /// <summary>
    /// Keeps the overlay pinned above other windows. <see cref="Window.Topmost"/> on its own is
    /// not enough because other topmost windows get placed above it.
    /// </summary>
    /// <remarks>
    /// The overlay is disabled while a ransom is active. Otherwise it would keep shoving a
    /// fullscreen click-through window to the very front of the z-order, which is exactly what
    /// pushed the ransom window behind whatever the user was using.
    /// </remarks>
    private void SetupTopMost()
    {
        _topMostEnforcer = new TopMostEnforcer(this, TimeSpan.FromSeconds(1));

        _topMostTimer.Tick += (_, _) => UpdateTopMostEnforcement();

        // Setup runs after the initial ResetRansom, so the app is normally already idle here and
        // there is nothing to keep in the top band.
        if (_encounterActive)
        {
            _topMostEnforcer.Start();
            _topMostTimer.Start();
        }
    }

    /// <summary>
    /// Whether a ransom encounter is currently running. Outside one the overlay is parked, because
    /// a fullscreen transparent window that is still visible costs compositor time continuously.
    /// </summary>
    private bool _encounterActive;

    /// <summary>
    /// Whether the animated GIFs should currently be running. The animators do not exist until each
    /// image finishes decoding, so this records the intent that is applied whenever one appears.
    /// </summary>
    private bool _gifsShouldAnimate;

    /// <summary>
    /// Parks the overlay while no encounter is running.
    /// </summary>
    /// <remarks>
    /// The overlay is a fullscreen, per-pixel-transparent, topmost window. Leaving it visible with
    /// every element at zero opacity still makes the compositor blend a fullscreen alpha layer on
    /// every pass, and the background GIF keeps decoding frames regardless of opacity, so an idle
    /// app costs a steady slice of CPU and GPU that OBS pays for in its capture path. Hiding the
    /// window and pausing the animators drops that to nothing; <see cref="ExitIdleState"/> brings
    /// it back before an encounter draws anything.
    /// </remarks>
    private void EnterIdleState()
    {
        _gifsShouldAnimate = false;

        SetGifAnimating(staticBg, playing: false);
        SetGifAnimating(redVignette, playing: false);
        SetGifAnimating(img_attack, playing: false);

        if (Visibility != Visibility.Hidden)
        {
            Visibility = Visibility.Hidden;
        }

        _topMostTimer.Stop();
        _topMostEnforcer?.SetEnabled(false);
    }

    /// <summary>
    /// Brings the overlay back on screen for an encounter. Must run before any element is shown,
    /// or the first phase is drawn into a hidden window.
    /// </summary>
    private void ExitIdleState()
    {
        _gifsShouldAnimate = true;

        if (Visibility != Visibility.Visible)
        {
            Visibility = Visibility.Visible;
        }

        SetGifAnimating(staticBg, playing: true);
        SetGifAnimating(redVignette, playing: true);
        SetGifAnimating(img_attack, playing: true);

        _topMostTimer.Start();

        // SetupTopMost skips starting the enforcer while parked, so it is started here instead.
        _topMostEnforcer?.Start();
        UpdateTopMostEnforcement();
    }

    /// <summary>
    /// Starts or pauses an animated GIF. The animators run off the render loop, so a zero-opacity
    /// image is still decoded every frame unless they are explicitly paused.
    /// </summary>
    /// <remarks>
    /// <c>GetAnimator</c> returns null until the image has finished decoding, which happens well
    /// after <see cref="Window_Loaded"/>. Every GIF therefore declares
    /// <c>AnimationBehavior.AutoStart="False"</c> and is driven from here instead, and
    /// <see cref="OnGifLoaded"/> re-applies <see cref="_gifsShouldAnimate"/> once an animator
    /// finally exists. Applying the state inline in the load callback does not stick, because the
    /// decoder restarts the animation right after the handler returns.
    /// </remarks>
    private void SetGifAnimating(FrameworkElement element, bool playing)
    {
        Animator? animator = AnimationBehavior.GetAnimator(element);

        if (animator is null)
        {
            return;
        }

        if (playing)
        {
            animator.Play();
        }
        else
        {
            animator.Pause();
        }
    }

    /// <summary>
    /// Applies the intended animation state once a GIF finishes decoding, on a later dispatcher tick
    /// so it survives the decoder restarting the animation after the load callback returns.
    /// </summary>
    private void OnGifLoaded(object sender, RoutedEventArgs e)
    {
        DependencyObject owner = (DependencyObject)sender;

        Dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(() =>
            SetGifAnimating((FrameworkElement)owner, _gifsShouldAnimate)));
    }

    private void UpdateTopMostEnforcement()
    {
        // A dedicated ransom window is visible and owns the front of the z-order instead, and while
        // parked there is nothing on screen to keep in the top band.
        _topMostEnforcer?.SetEnabled(_encounterActive && !Global.State.UnderRansom);
    }

    private void SetupTrayIcon()
    {
        ContextMenuStrip trayMenu = new();

        trayMenu.Items.Add("Configuration").Click += (_, _) => new ConfigWindow().Show();
        trayMenu.Items.Add("Close").Click += (_, _) => WpfApplication.Current.Shutdown();

        _trayIcon = new NotifyIcon
        {
            Icon = System.Drawing.Icon.ExtractAssociatedIcon(Environment.ProcessPath ?? string.Empty)
                ?? System.Drawing.SystemIcons.Application,
            ContextMenuStrip = trayMenu,
            Text = "RANS0M",
            Visible = true,
        };
    }

    private const int GWL_EXSTYLE = -20;
    private const int WS_EX_TRANSPARENT = 0x00000020;
    private const int WS_EX_LAYERED = 0x00080000;
    private const int WS_EX_TOOLWINDOW = 0x00000080;
    private const int WS_EX_NOACTIVATE = 0x08000000;

    /// <summary>
    /// Makes the overlay ignore the mouse so the user can keep working, and keeps it out of
    /// the taskbar and Alt+Tab.
    /// </summary>
    private void MakeClickThrough()
    {
        IntPtr hwnd = new WindowInteropHelper(this).Handle;

        int extendedStyle = Global.GetWindowLong(hwnd, GWL_EXSTYLE);
        Global.SetWindowLong(hwnd, GWL_EXSTYLE, extendedStyle
            | WS_EX_TRANSPARENT
            | WS_EX_LAYERED
            | WS_EX_TOOLWINDOW
            | WS_EX_NOACTIVATE);
    }

    // -------------------- CORE --------------------

    /// <summary>
    /// Returns the app to its ready state and clears any visual trace of a ransom.
    /// </summary>
    public async Task ResetRansom()
    {
        await Task.Run(GoldCoinManager.DeleteAllCoins);

        Global.State.Reset();

        foreach (TauntWindow tauntWindow in WpfApplication.Current.Windows.OfType<TauntWindow>().ToArray())
        {
            tauntWindow.Close();
        }

        StopEffectTimers();

        redVignette.Opacity = 0;
        staticBg.Opacity = 0;

        img_idle.Opacity = 0;
        img_attack.Opacity = 0;
        img_stopsign.Opacity = 0;
        vb_download.Opacity = 0;
        vb_download.Width = 668;
        vb_download.Height = 88;

        foreach (Border segment in _dpbSegments)
        {
            segment.Background = Brushes.Black;
        }

        Background = Brushes.Transparent;
        WallpaperUpdater.RestoreWallpaper();
        CursorUpdater.RestoreCursor();

        // The encounter is over, so stop paying for a fullscreen transparent window and a
        // fullscreen background GIF that nothing is currently visible through.
        _encounterActive = false;
        EnterIdleState();
    }

    private void StopEffectTimers()
    {
        _downloadTextTimer?.Stop();
        _downloadBarTimer?.Stop();
        _shakeTimer?.Stop();
    }

    /// <summary>
    /// Shakes the attack image around its center position for a fixed number of ticks.
    /// </summary>
    private void StartShake(Thickness center, int ticks)
    {
        _shakeTicksRemaining = ticks;

        _shakeTimer?.Stop();
        _shakeTimer = new DispatcherTimer(DispatcherPriority.Render, Dispatcher)
        {
            Interval = TimeSpan.FromMilliseconds(20),
        };

        _shakeTimer.Tick += (_, _) =>
        {
            if (_shakeTicksRemaining-- <= 0)
            {
                _shakeTimer?.Stop();
                img_attack.Margin = center;
                return;
            }

            img_attack.Margin = Global.CombineThickness(center, new Thickness(
                Global.Rng.Next(-40, 40), Global.Rng.Next(-40, 40), 0, 0));
        };

        _shakeTimer.Start();
    }

    /// <summary>
    /// Repeatedly summons the entity at a random interval until the app shuts down.
    /// </summary>
    private async void RansomLoop()
    {
        while (!_shutdown.IsCancellationRequested)
        {
            // Twitch-only mode: the entity should appear when chat pays for it, not on a timer.
            // Checked here rather than in the config window so it applies even if the checkbox is
            // still ticked from an earlier session.
            if (Config.Current.Twitch.Enabled)
            {
                // Suppressing the timer only makes sense if redemptions are actually being watched.
                // If the bridge died or never started, warn instead of leaving the app silently
                // incapable of spawning anything.
                if (_twitch is null || !_twitch.IsReady)
                {
                    WarnNoSpawnSource();
                }

                await InterruptibleDelay(250);
                continue;
            }

            if (!Config.Current.SpawnAutomatically)
            {
                await InterruptibleDelay(250);
                continue;
            }

            int minDelay = Math.Min(Config.Current.MinSpawnDelay, Config.Current.MaxSpawnDelay) * 1000;
            int maxDelay = Math.Max(Config.Current.MinSpawnDelay, Config.Current.MaxSpawnDelay) * 1000;

            // A zero max delay would make Random.Next throw rather than mean "spawn now".
            await InterruptibleDelay(Global.Rng.Next(minDelay, Math.Max(minDelay + 1, maxDelay)));

            if (_shutdown.IsCancellationRequested)
            {
                break;
            }

            // Automatic spawns are always the dodgeable version.
            await Dispatcher.InvokeAsync(() => SpawnRansom(false));
        }
    }

    /// <summary>
    /// Tells the user once that Twitch is on but no redemption listener is live, which otherwise
    /// looks identical to "the app is broken". Throttled, since the spawn loop polls often.
    /// </summary>
    private void WarnNoSpawnSource()
    {
        if (DateTimeOffset.UtcNow - _lastNoSpawnWarning < TimeSpan.FromMinutes(1))
        {
            return;
        }

        _lastNoSpawnWarning = DateTimeOffset.UtcNow;

        string detail = _twitch is null
            ? "The redemption listener is not running."
            : $"The redemption listener is not ready. {_twitch.LastError}";

        string message =
            "Twitch is on, so automatic spawning is off, but nothing will spawn. "
            + $"{detail} Open the configuration window and press Connect, or turn Twitch off to go "
            + "back to timed spawns.";

        Console.WriteLine($"[twitch] {message}");

        // A tray balloon rather than a modal, since this can fire while the user is mid-stream and
        // a dialog would steal focus from whatever they are doing.
        _trayIcon?.ShowBalloonTip(10000, "RANS0M will not spawn", message, ToolTipIcon.Warning);
    }

    /// <summary>
    /// Cuts the current wait short so a config change applies right away.
    /// </summary>
    public void RetriggerSpawnLoop() => _spawnDelayCts?.Cancel();

    private async Task InterruptibleDelay(int ms)
    {
        _spawnDelayCts?.Dispose();
        _spawnDelayCts = new CancellationTokenSource();

        try
        {
            await Task.Delay(ms, _spawnDelayCts.Token);
        }
        catch (OperationCanceledException)
        {
            // Expected: the delay was cut short on purpose.
        }
    }

    /// <summary>
    /// Runs one full ransom encounter.
    /// </summary>
    /// <param name="forceAttack">
    /// When true the warning phase is ignored and the attack lands even if the user never moved the
    /// cursor. Wired to the more expensive channel points reward so viewers can pay to not be spared.
    /// </param>
    public async Task SpawnRansom(bool forceAttack = false)
    {
        if (!Global.State.CanAttack)
        {
            return;
        }

        Global.State.CanAttack = false;
        _encounterActive = true;
        ExitIdleState();

        try
        {
            // A forced attack still plays the warning, so the victim has no visual cue that their
            // stillness will not save them. The only difference is that the flinch no longer matters.
            bool mouseMoved = await RansomWarning();

            if (!forceAttack && !mouseMoved)
            {
                // The user held still, so they dodged it.
                await ResetRansom();
                return;
            }

            int generatedGold = 0;
            try
            {
                generatedGold = await Task.Run(GoldCoinManager.GenerateCoins);
            }
            catch (IOException)
            {
                generatedGold = 0;
            }
            catch (UnauthorizedAccessException)
            {
                generatedGold = 0;
            }

            if (generatedGold <= 0)
            {
                // Should not happen, but without any coins the ransom would be unpayable.
                await CrashJumpscare();
                return;
            }

            await DownloadJumpscare();

            if (await Ransomed(generatedGold))
            {
                Global.State.UnderRansom = false;
                await CrashJumpscare();
            }
        }
        catch (Exception)
        {
            // An unexpected failure must not leave the app stuck mid-encounter.
            await ResetRansom();
        }
    }

    // ------------------- RANSOM PHASES -------------------

    /// <summary>
    /// First phase: plays the spawn sound, shows the face, and watches for a flinch.
    /// </summary>
    /// <returns>true if the user moved the mouse or pressed a key during the warning phase</returns>
    public async Task<bool> RansomWarning()
    {
        SoundHandle spawnSound = SoundHelper.Create(Global.GetResourceStream("Sounds/spawn.wav"));
        spawnSound.Play();

        // Show the face at a random spot first, so it appears to notice you.
        Global.RandomPosControl(img_idle);
        img_idle.Opacity = 100;

        await Task.Delay(500);

        Global.State.MouseSpy.Begin();

        // Center the face and show the stop sign.
        img_stopsign.Opacity = 100;
        Global.CenterControl(img_idle);
        img_idle.Margin = new Thickness(img_idle.Margin.Left, img_idle.Margin.Top + 50, 0, 0);
        Background = new SolidColorBrush(Color.FromRgb(40, 0, 0));

        await Task.Delay(500);

        bool flinched = Global.State.MouseSpy.HasMoved();
        Global.State.MouseSpy.End();

        img_stopsign.Opacity = 0;
        Background = new SolidColorBrush(Color.FromArgb(100, 100, 0, 0));

        // A flinch resolves fast; holding still leaves it on screen much longer.
        await Task.Delay(flinched ? 25 : 100);

        Background = Brushes.Transparent;
        img_idle.Opacity = 0;

        return flinched;
    }

    /// <summary>
    /// Second phase: the jumpscare and the fake download progress.
    /// </summary>
    public async Task DownloadJumpscare()
    {
        SoundHandle attackSound = SoundHelper.Create(Global.GetResourceStream("Sounds/attack.wav"));
        attackSound.Play();

        Global.CenterControl(img_attack);
        Thickness attackCenter = img_attack.Margin;
        img_attack.Opacity = 100;
        Background = new SolidColorBrush(Color.FromRgb(120, 0, 0));
        staticBg.Opacity = 0.05;

        StartShake(attackCenter, 40);

        await Task.Delay(800);

        img_attack.Opacity = 0;

        SoundHandle installSound = SoundHelper.Create(Global.GetResourceStream("Sounds/install.wav"));
        installSound.Play();

        vb_download.Opacity = 100;
        StartDownloadEffect();

        await Task.Delay(1200);

        StopEffectTimers();

        Background = Brushes.Transparent;
        img_attack.Opacity = 0;
        vb_download.Opacity = 0;
        staticBg.Opacity = 0;

        foreach (Border segment in _dpbSegments)
        {
            segment.Background = Brushes.Black;
        }
    }

    /// <summary>
    /// Drives the cycling download text and the filling progress bar.
    /// </summary>
    private void StartDownloadEffect()
    {
        string[] frames = ["DOWNLOADING", "DOWNLOADING.", "DOWNLOADING..", "DOWNLOADING..."];
        Brush white = new SolidColorBrush(Color.FromRgb(255, 255, 255));
        Brush red = new SolidColorBrush(Color.FromRgb(255, 0, 0));
        LinearGradientBrush fill = new()
        {
            StartPoint = new System.Windows.Point(0.5, 0),
            EndPoint = new System.Windows.Point(0.5, 1),
            GradientStops =
            {
                new GradientStop(Color.FromArgb(255, 204, 0, 0), 0),
                new GradientStop(Color.FromArgb(255, 150, 0, 0), 0.5),
                new GradientStop(Color.FromArgb(255, 204, 0, 0), 1),
            },
        };

        int frameIndex = 0;
        _downloadTextTimer?.Stop();
        _downloadTextTimer = new DispatcherTimer(DispatcherPriority.Render, Dispatcher)
        {
            Interval = TimeSpan.FromMilliseconds(120),
        };
        _downloadTextTimer.Tick += (_, _) =>
        {
            frameIndex = (frameIndex + 1) % frames.Length;
            txt_download.Content = frames[frameIndex];

            // Flip color every full cycle for a flicker.
            txt_download.Foreground = frameIndex == 0 ? red : white;
        };
        _downloadTextTimer.Start();

        int segmentIndex = 0;
        _downloadBarTimer?.Stop();
        _downloadBarTimer = new DispatcherTimer(DispatcherPriority.Render, Dispatcher)
        {
            Interval = TimeSpan.FromMilliseconds(120),
        };
        _downloadBarTimer.Tick += (_, _) =>
        {
            vb_download.Width += 20;
            vb_download.Height += 20;

            if (segmentIndex < _dpbSegments.Length)
            {
                _dpbSegments[segmentIndex].Background = fill;
                segmentIndex++;
            }
        };
        _downloadBarTimer.Start();

        // Glitch the text and bar in place.
        StartShakeTextAndBar(40);
    }

    private void StartShakeTextAndBar(int ticks)
    {
        DispatcherTimer? timer = null;
        int remaining = ticks;

        timer = new DispatcherTimer(DispatcherPriority.Render, Dispatcher)
        {
            Interval = TimeSpan.FromMilliseconds(40),
        };
        timer.Tick += (_, _) =>
        {
            if (remaining-- <= 0)
            {
                timer.Stop();
                return;
            }

            txt_download.Margin = new Thickness(Global.Rng.Next(-5, 5), Global.Rng.Next(-5, 5), 0, 0);
            downloadProgressBar.Margin = new Thickness(Global.Rng.Next(-20, 20), Global.Rng.Next(-20, 20), 0, 0);
        };
        timer.Start();
    }

    /// <summary>
    /// Third phase: the actual ransom window, the countdown, and the music layers.
    /// </summary>
    /// <param name="generatedGold">Total value of the coins actually generated for this run</param>
    /// <returns>true if the user failed to pay in time</returns>
    public async Task<bool> Ransomed(int generatedGold)
    {
        WallpaperUpdater.SetDarkRedWallpaper();
        CursorUpdater.SetInfectedCursor();

        SolidColorBrush flash = new(Color.FromArgb(255, 255, 0, 0));
        Background = flash;
        flash.BeginAnimation(SolidColorBrush.ColorProperty, new ColorAnimation
        {
            From = Color.FromArgb(255, 255, 0, 0),
            To = Color.FromArgb(0, 255, 0, 0),
            Duration = TimeSpan.FromMilliseconds(500),
        });

        redVignette.Opacity = 100;

        SoundHandle layer1 = SoundHelper.Create(Global.GetResourceStream("Sounds/layer1.wav"));
        SoundHandle layer2 = SoundHelper.Create(Global.GetResourceStream("Sounds/layer2.wav"));
        SoundHandle layer3 = SoundHelper.Create(Global.GetResourceStream("Sounds/layer3.wav"));

        int duration = Config.Current.InfectionDuration;

        // Clamp so an unlucky generation run can never leave the ransom unpayable.
        Global.State.RansomLeft = Math.Min(Config.Current.RansomAmount, generatedGold);
        Global.State.RansomTimeLeft = duration;
        Global.State.UnderRansom = true;

        // Wakes the countdown immediately on payment instead of at the next tick.
        TaskCompletionSource paidSignal = new(TaskCreationOptions.RunContinuationsAsynchronously);

        Global.State.RansomPayed = () =>
        {
            layer1.Stop();
            layer2.Stop();
            layer3.Stop();

            Dispatcher.InvokeAsync(ResetRansom);
            paidSignal.TrySetResult();
        };

        RansomNotification? ransomNotification = null;

        await Dispatcher.InvokeAsync(() =>
        {
            ransomNotification = new RansomNotification();
            ransomNotification.Show();
        }, DispatcherPriority.Normal);

        // Music layers escalate across the encounter, reserving the last stretch for layer3.
        const int layer3Seconds = 26;
        int remainingSeconds = Math.Max(0, duration - layer3Seconds);
        int layer1Seconds = remainingSeconds / 2;
        int layer2Seconds = remainingSeconds - layer1Seconds;

        layer1.PlayLooping();
        bool startedLayer2 = false;
        bool startedLayer3 = false;

        int elapsed = 0;
        while (elapsed < duration)
        {
            Task delay = Task.Delay(1000);
            Task completed = await Task.WhenAny(delay, paidSignal.Task);
            if (completed == paidSignal.Task)
            {
                return false;
            }

            if (!Global.State.UnderRansom)
            {
                return false;
            }

            elapsed++;
            Global.State.RansomTimeLeft = Math.Max(0, duration - elapsed);

            if (!startedLayer2 && elapsed >= layer1Seconds)
            {
                startedLayer2 = true;
                layer1.Stop();
                layer2.PlayLooping();
            }
            else if (startedLayer2 && !startedLayer3 && elapsed >= layer1Seconds + layer2Seconds)
            {
                startedLayer3 = true;
                layer2.Stop();
                layer3.Play();
            }
        }

        // Ran out of time.
        ransomNotification?.Close();
        redVignette.Opacity = 0;

        return true;
    }

    /// <summary>
    /// Shows the jumpscare, then optionally runs a command or shuts the machine down.
    /// </summary>
    public async Task CrashJumpscare()
    {
        SoundHandle attackSound = SoundHelper.Create(Global.GetResourceStream("Sounds/attack.wav"));
        attackSound.Play();

        Background = new SolidColorBrush(Color.FromRgb(100, 0, 0));
        staticBg.Opacity = 0.05;

        Global.CenterControl(img_attack);
        Thickness attackCenter = img_attack.Margin;
        img_attack.Opacity = 100;

        StartShake(attackCenter, 25);

        await Task.Delay(1000);

        if (Config.Current.ExecCMDOnDeath)
        {
            RunConfiguredCommand(Config.Current.CMDOnDeath);
        }

        if (Config.Current.CrashOnDeath)
        {
            Process.Start("shutdown", "/s /t 0");
        }

        await ResetRansom();
    }

    /// <summary>
    /// Runs the user-supplied command. Split on the first space so a command with arguments
    /// keeps them, and quoting is respected where it is present.
    /// </summary>
    private static void RunConfiguredCommand(string commandLine)
    {
        if (string.IsNullOrWhiteSpace(commandLine))
        {
            return;
        }

        string[] parts = commandLine.Split(' ', 2);
        string fileName = parts[0];
        string arguments = parts.Length > 1 ? parts[1] : string.Empty;

        try
        {
            Process.Start(fileName, arguments);
        }
        catch (System.ComponentModel.Win32Exception)
        {
            // The configured command could not be launched. Intentionally silent: this is a
            // prank app, and erroring out here would leave the app wedged mid-jumpscare.
        }
    }
}