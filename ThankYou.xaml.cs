using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace rans0m;

public partial class ThankYou : Window
{
    private readonly double _previousLeft;
    private readonly double _previousTop;
    private SoundHandle? _sound;

    public ThankYou(double left, double top)
    {
        InitializeComponent();

        Global.HideSystemMenu(this);

        _previousLeft = left;
        _previousTop = top;
    }

    private async void Window_Loaded(object sender, RoutedEventArgs e)
    {
        Global.HideSystemMenu(this);

        // Signals the encounter to stop the music and clear itself. Null when the window is
        // opened outside a live ransom, e.g. from the designer.
        Global.State.RansomPayed?.Invoke();

        Brush originalBackground = grid.Background;
        grid.Background = new SolidColorBrush(Color.FromRgb(0, 0, 0));
        img_thx.Opacity = 0;
        img_oksign.Opacity = 0;

        Left = _previousLeft;
        Top = _previousTop;

        // Grow toward the center of the screen, easing in two steps.
        double totalWidth = ActualWidth * 1.3;
        double totalHeight = ActualHeight * 1.3;
        double centerLeft = (Global.ScreenBounds.Width / 2) - totalWidth / 2;
        double centerTop = (Global.ScreenBounds.Height / 2) - totalHeight / 2;

        await Task.Delay(200);

        Left = Global.Lerp(Left, centerLeft, 0.5);
        Top = Global.Lerp(Top, centerTop, 0.5);
        Width = Global.Lerp(Width, totalWidth, 0.5);
        Height = Global.Lerp(Height, totalHeight, 0.5);

        await Task.Delay(100);

        Left = centerLeft;
        Top = centerTop;
        Width = totalWidth;
        Height = totalHeight;

        await Task.Delay(200);

        grid.Background = originalBackground;
        img_ransom.Opacity = 0;
        img_oksign.Opacity = 1;

        _sound = SoundHelper.Create(Global.GetResourceStream("Sounds/thankyou.wav"));
        _sound.Play();

        Global.CenterWindow(this);

        DoubleAnimation growOkSign = new()
        {
            From = 0.1,
            To = 1.0,
            Duration = new Duration(TimeSpan.FromMilliseconds(300)),
        };

        scaleOkSign.BeginAnimation(ScaleTransform.ScaleXProperty, growOkSign);
        scaleOkSign.BeginAnimation(ScaleTransform.ScaleYProperty, growOkSign);

        DoubleAnimation growThanks = new()
        {
            From = 0.1,
            To = 1.0,
            Duration = new Duration(TimeSpan.FromMilliseconds(200)),
        };

        await Task.Delay(400);

        if (!IsLoaded)
        {
            return;
        }

        img_thx.Opacity = 100;
        scaleTxtThx.BeginAnimation(ScaleTransform.ScaleXProperty, growThanks);
        scaleTxtThx.BeginAnimation(ScaleTransform.ScaleYProperty, growThanks);

        await Task.Delay(4000);

        Close();
    }

    private void OnClosing(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        _sound?.Stop();
    }
}