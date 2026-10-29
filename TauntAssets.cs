using System.Windows.Media.Imaging;

namespace rans0m;

/// <summary>
/// The pool of images and window titles used by the taunt popups.
/// </summary>
/// <remarks>
/// Images are loaded once, frozen, and shared. Loading them per window multiplied ~5MB of
/// JPEG/PNG decoding across every taunt popup.
/// </remarks>
public sealed class TauntAssets
{
    private static readonly Lazy<IReadOnlyList<BitmapImage>> Images = new(LoadImages, isThreadSafe: true);

    public static IReadOnlyList<string> Titles { get; } = new[]
    {
        "RANS0M",
        "MOSNAR",
        "RANSOM",
        "M0NARS",
        "YOU ARE AN IDIOT",
        "Untitled",
        "Untitled (3)",
        "I FOUND YOU",
        "RANSOM.exe",
        "RAANNNSSSSOOOOOMMMMMM",
        "times up",
        "GIVE MONEY",
        "ERROR",
        "DHAUFGH",
        "_________",
        "IMG.JPG",
    };

    public static IReadOnlyList<BitmapImage> TauntImages => Images.Value;

    private static IReadOnlyList<BitmapImage> LoadImages()
    {
        string[] paths =
        [
            "Taunts/glitch1.jpg",
            "Taunts/glitch2.jpeg",
            "Taunts/glitch3.jpg",
            "Taunts/glitch4.jpg",
            "Taunts/glitch5.jpg",
            "Taunts/idiot.png",
            "Taunts/tauntface.png",
            "Taunts/tauntflower.png",
        ];

        List<BitmapImage> images = new(paths.Length);
        foreach (string path in paths)
        {
            images.Add(Global.LoadBitmapImage($"pack://application:,,,/Assets/{path}"));
        }

        return images;
    }
}