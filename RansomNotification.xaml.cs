using System.IO;
using System.Windows;
using System.Windows.Threading;

namespace rans0m;

public partial class RansomNotification : Window
{
    private readonly DispatcherTimer _timer;
    private readonly GlitchIdle _glitchIdle;
    private TopMostEnforcer? _topMostEnforcer;
    private int _knownTimeLeft = -1;

    public RansomNotification()
    {
        InitializeComponent();

        _timer = new DispatcherTimer(DispatcherPriority.Normal, Dispatcher)
        {
            Interval = TimeSpan.FromSeconds(1),
        };
        _timer.Tick += OnTimerTick;

        _glitchIdle = new GlitchIdle(this, spawnTaunts: true);
    }

    private void Window_Loaded(object sender, RoutedEventArgs e)
    {
        Global.HideSystemMenu(this);

        UpdateCountdown(Global.State.RansomTimeLeft);

        Global.RandomPosWindow(this);

        // The ransom window is what the user has to interact with, so it owns the front of the
        // z-order and re-asserts it far more aggressively than the overlay does.
        _topMostEnforcer = new TopMostEnforcer(this, TimeSpan.FromMilliseconds(250));
        _topMostEnforcer.Start();

        // Taunt windows are normal-top windows so the ransom stays readable on top of them.
        for (int i = 0; i < 9; i++)
        {
            new TauntWindow().Show();
        }

        _glitchIdle.Start();
        _timer.Start();
    }

    private void OnClosing(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        _timer.Stop();
        _glitchIdle.Dispose();
    }

    private void OnTimerTick(object? sender, EventArgs e)
    {
        txt_ransomLeft.Text = Global.State.RansomLeft.ToString();

        UpdateCountdown(Global.State.RansomTimeLeft);
    }

    /// <summary>
    /// Formats the countdown, skipping the refresh when the value has not changed so the label
    /// does not flicker once a second as its width is recomputed.
    /// </summary>
    private void UpdateCountdown(int secondsLeft)
    {
        if (secondsLeft == _knownTimeLeft)
        {
            return;
        }

        _knownTimeLeft = secondsLeft;
        txt_timer.Text = $"TIME: {secondsLeft / 60:D2}:{secondsLeft % 60:D2}";
    }

    private void Window_DragEnter(object sender, DragEventArgs e)
    {
        if (e.Data.GetDataPresent(DataFormats.FileDrop))
        {
            e.Effects = DragDropEffects.Link;
        }
    }

    private void Window_Drop(object sender, DragEventArgs e)
    {
        if (e.Data.GetData(DataFormats.FileDrop) is not string[] files)
        {
            return;
        }

        bool sfxPlayed = false;

        foreach (string file in files)
        {
            string extension = Path.GetExtension(file);

            if (extension.StartsWith(".gold", StringComparison.OrdinalIgnoreCase))
            {
                RedeemCoin(file, extension, ref sfxPlayed);
            }
            else if (extension.Equals(".crucifix", StringComparison.OrdinalIgnoreCase))
            {
                RedeemCrucifix(file);
            }
        }

        if (Global.State.RansomLeft <= 0)
        {
            Global.State.UnderRansom = false;

            if (Global.State.CrucifixUsed)
            {
                new CrucifixWindow(Left, Top).Show();
            }
            else
            {
                new ThankYou(Left, Top).Show();
            }

            Close();
        }
    }

    private void RedeemCoin(string file, string extension, ref bool sfxPlayed)
    {
        if (!int.TryParse(extension.Replace(".gold", "", StringComparison.OrdinalIgnoreCase), out int goldType))
        {
            return;
        }

        // Rejects corrupt or foreign files, and any coin already spent this encounter.
        if (!TryClaimCoin(file))
        {
            return;
        }

        if (!sfxPlayed)
        {
            sfxPlayed = true;
            SoundHelper.Create(Global.GetResourceStream("Sounds/cash.wav")).Play();
        }

        Global.State.RansomLeft -= CoinValues.GetValue(goldType);

        TryDelete(file);
    }

    private void RedeemCrucifix(string file)
    {
        if (!TryClaimCoin(file))
        {
            return;
        }

        TryDelete(file);

        Global.State.CrucifixUsed = true;
        Global.State.RansomLeft = 0;
    }

    /// <summary>
    /// Decrypts a dropped marker file and marks its id as spent. Returns false for files that
    /// were not created by this app or that have already been paid in.
    /// </summary>
    private static bool TryClaimCoin(string file)
    {
        Dictionary<string, string>? data = GoldCoinManager.DecryptCoinFile(file);

        if (data is null || !data.TryGetValue("RANSOM_COIN", out string? coinId) || coinId is null)
        {
            return false;
        }

        return Global.State.TrySpendCoin(coinId);
    }

    /// <summary>
    /// Best-effort delete. A locked or already-removed file must not stop the ransom from
    /// being paid off.
    /// </summary>
    private static void TryDelete(string file)
    {
        try
        {
            File.Delete(file);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}