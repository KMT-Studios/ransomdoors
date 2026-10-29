using System.Windows;
using System.Windows.Media;

namespace rans0m;

public partial class TauntWindow : Window
{
    private readonly GlitchIdle _glitchIdle;
    private bool _closed;

    public TauntWindow()
    {
        InitializeComponent();

        _glitchIdle = new GlitchIdle(this);
        Closed += (_, _) =>
        {
            _closed = true;
            _glitchIdle.Dispose();
        };
    }

    private void Window_Loaded(object sender, RoutedEventArgs e)
    {
        Global.HideSystemMenu(this);

        _ = PlayTauntSpawnSoundAsync();

        Title = Global.Rng.Pick(TauntAssets.Titles);
        Background = new ImageBrush(Global.Rng.Pick(TauntAssets.TauntImages));
        Width = Global.Rng.Next(200, 400);
        Height = Global.Rng.Next(200, 400);

        Global.RandomPosWindow(this);

        _glitchIdle.Reanchor();
        _glitchIdle.Start();

        _ = CloseAfterDelayAsync();
    }

    private static async Task PlayTauntSpawnSoundAsync()
    {
        using System.IO.Stream stream = Global.GetResourceStream("Sounds/tauntSpawn.wav");
        SoundHelper.Create(stream).Play();
    }

    private async Task CloseAfterDelayAsync()
    {
        await Task.Delay(Global.Rng.Next(4000, 10 * 1000));

        // The reset sweep may have already closed this while the timer was counting down.
        if (_closed)
        {
            return;
        }

        using System.IO.Stream stream = Global.GetResourceStream("Sounds/tauntLeave.wav");
        SoundHelper.Create(stream).Play();

        Close();
    }
}