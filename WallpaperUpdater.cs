using System.Drawing;
using System.IO;
using System.Runtime.InteropServices;
using Microsoft.Win32;

namespace rans0m;

/// <summary>
/// Swaps the desktop wallpaper for a flat dark red while a ransom is active, and puts the
/// original one back afterwards.
/// </summary>
public sealed class WallpaperUpdater
{
    private const uint SPI_SETDESKWALLPAPER = 20;
    private const uint SPIF_UPDATEINIFILE = 0x01;
    private const uint SPIF_SENDCHANGE = 0x02;

    private static readonly Lock Gate = new();

    /// <summary>
    /// The wallpaper to restore. Null until <see cref="SetDarkRedWallpaper"/> has run, so a
    /// restore with nothing saved cannot blank the desktop.
    /// </summary>
    private static string? _originalWallpaper;

    [DllImport("user32.dll", CharSet = CharSet.Auto)]
    private static extern int SystemParametersInfo(uint uAction, uint uParam, string lpvParam, uint fuWinIni);

    private static string TempImagePath => Path.Combine(Path.GetTempPath(), "darkred_wallpaper.bmp");

    public static void SetDarkRedWallpaper()
    {
        // Runs off the UI thread because it generates and writes an image; blocking here would
        // drop a frame right as the red flash plays.
        Task.Run(() =>
        {
            try
            {
                string? original = ReadOriginalWallpaper();

                string tempPath = TempImagePath;
                using (Bitmap bitmap = new(1920, 1080))
                {
                    using (Graphics graphics = Graphics.FromImage(bitmap))
                    {
                        graphics.Clear(Color.FromArgb(50, 0, 0));
                    }

                    bitmap.Save(tempPath, System.Drawing.Imaging.ImageFormat.Bmp);
                }

                SystemParametersInfo(SPI_SETDESKWALLPAPER, 0, tempPath, SPIF_UPDATEINIFILE | SPIF_SENDCHANGE);

                // Only remember the original once the swap succeeded, otherwise a later
                // restore would point at a wallpaper we never replaced.
                lock (Gate)
                {
                    _originalWallpaper = original;
                }
            }
            catch (Exception)
            {
                // Wallpaper is cosmetic, so a failure here must not break the encounter.
            }
        });
    }

    private static string? ReadOriginalWallpaper()
    {
        using RegistryKey? key = Registry.CurrentUser.OpenSubKey(@"Control Panel\Desktop");
        return key?.GetValue("Wallpaper") as string;
    }

    public static void RestoreWallpaper()
    {
        try
        {
            string? original;
            lock (Gate)
            {
                original = _originalWallpaper;
                _originalWallpaper = null;
            }

            if (!string.IsNullOrEmpty(original))
            {
                SystemParametersInfo(SPI_SETDESKWALLPAPER, 0, original, SPIF_UPDATEINIFILE | SPIF_SENDCHANGE);
            }

            string tempPath = TempImagePath;
            if (File.Exists(tempPath))
            {
                File.Delete(tempPath);
            }
        }
        catch (Exception)
        {
            // Same reasoning as above: never let cleanup break the app.
        }
    }
}