using System.Diagnostics;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media.Imaging;
using System.Windows.Navigation;
using Attractor.Core.Diagnostics;

namespace Attractor.App;

/// <summary>
/// "Help → About" dialog: app name/version, the project link, and a Cold Beam
/// Games cross-promo. The two art slots (logo + Beat Hazard promo) ship empty and
/// stay hidden until a matching graphic is dropped into assets/img, so the build
/// runs before any art exists — see <see cref="TrySetImage"/>.
/// </summary>
public partial class AboutWindow : Window
{
    // Where the "Beat Hazard" link / promo image points. Defaults to the Cold Beam
    // Games site (always valid); repoint to the Beat Hazard Steam page if preferred.
    private const string BeatHazardUrl = "https://coldbeamgames.com";

    public AboutWindow()
    {
        InitializeComponent();
        SourceInitialized += (_, _) => Dwm.ApplyDarkTitleBar(this);

        var version = Assembly.GetExecutingAssembly().GetName().Version?.ToString(3) ?? "dev";
        VersionText.Text = $"v{version}";

        // One source of truth for the Beat Hazard target — the text link and the
        // promo image both use BeatHazardUrl.
        BeatHazardLink.NavigateUri = new Uri(BeatHazardUrl);

        // Prefer the format most natural for each (logo → PNG for transparency,
        // promo → JPG photo), but accept either; first match wins.
        TrySetImage(LogoImage, LogoSlot, "coldbeam_logo.png", "coldbeam_logo.jpg");
        TrySetImage(BeatHazardImage, BeatHazardSlot, "beat_hazard.jpg", "beat_hazard.png");
    }

    // Show a bundled graphic if one of the candidate files exists as a resource;
    // otherwise leave the slot hidden. Loaded with OnLoad so no file handle is held.
    private static void TrySetImage(Image image, UIElement slot, params string[] fileNames)
    {
        foreach (var name in fileNames)
        {
            try
            {
                var bmp = new BitmapImage();
                bmp.BeginInit();
                bmp.UriSource = new Uri($"pack://application:,,,/assets/img/{name}");
                bmp.CacheOption = BitmapCacheOption.OnLoad;
                bmp.EndInit();
                image.Source = bmp;
                slot.Visibility = Visibility.Visible;
                return;
            }
            catch (Exception)
            {
                // Not bundled yet — try the next candidate, else the slot stays hidden.
            }
        }
    }

    private void Hyperlink_RequestNavigate(object sender, RequestNavigateEventArgs e) =>
        OpenUrl(e.Uri.AbsoluteUri);

    private void BeatHazardImage_MouseUp(object sender, MouseButtonEventArgs e) =>
        OpenUrl(BeatHazardUrl);

    private void Close_Click(object sender, RoutedEventArgs e) => Close();

    private static void OpenUrl(string url)
    {
        try
        {
            Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            App.Log.Warn($"couldn't open {url}: {ex.Message}");
        }
    }
}
