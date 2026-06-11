using System.IO;
using System.Windows.Media.Imaging;

namespace Attractor.App;

public static class ImageLoader
{
    /// <summary>
    /// Decode a bitmap fully into memory and freeze it: the file on the
    /// (network) share is never locked, and the result is thread-safe to
    /// hand to the UI thread.
    /// </summary>
    public static BitmapImage? LoadFrozen(string? path)
    {
        if (path is null || !File.Exists(path))
            return null;
        try
        {
            using var stream = new MemoryStream(File.ReadAllBytes(path));
            var bmp = new BitmapImage();
            bmp.BeginInit();
            bmp.CacheOption = BitmapCacheOption.OnLoad;
            bmp.StreamSource = stream;
            bmp.EndInit();
            bmp.Freeze();
            return bmp;
        }
        catch (Exception ex) when (ex is IOException or NotSupportedException or UnauthorizedAccessException)
        {
            return null; // bad/unreadable art is cosmetic, never fatal
        }
    }
}
