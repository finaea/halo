using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Resources;

namespace Halo.Settings.Services;

/// <summary>
/// Frames of <c>assets\halo.ico</c>, picked explicitly rather than left to WPF.
/// </summary>
/// <remarks>
/// <para>
/// halo.ico carries seven frames (16, 24, 32, 48, 64, 128, 256) and the assembly stores all of
/// them. Bound straight to <c>Image.Source</c>, WPF hands back the <b>16-px</b> one and scales it
/// up, which is what looked low-resolution at 28 and 48 units.
/// </para>
/// <para>
/// <b>Do not replace this with a plain <c>Source="pack://…/halo.ico"</c> or with a
/// <c>BitmapImage</c> carrying <c>DecodePixelWidth</c>.</b> That hint selects the frame for a
/// <c>file://</c> URI but is <b>silently ignored for <c>pack://application</c> resources</b>,
/// which is where these images actually live — so the obvious-looking fix compiles, reads
/// correctly, and changes nothing. It was tried: mean |Laplacian| over the rendered About icon
/// moved 10.136 → 10.126, i.e. −0.1%, against +52.3% for the explicit frame below. A −0.1%
/// difference is invisible by eye, so this is a trap you can only fall out of by measuring.
/// </para>
/// <para>
/// To re-check by eye rather than by metric: the 128-px frame has a pale highlight arc across
/// the top-left of the ring and the 16-px frame does not. If the arc is missing on screen, the
/// small frame is being drawn.
/// </para>
/// </remarks>
public static class AppIcon
{
    private static readonly Uri Source = new("pack://application:,,,/assets/halo.ico", UriKind.Absolute);

    /// <summary>Smallest frame at least 128 px — comfortable for anything up to 128 units.</summary>
    public static ImageSource Large { get; } = Frame(128);

    private static ImageSource Frame(int minimumPixels)
    {
        try
        {
            StreamResourceInfo info = Application.GetResourceStream(Source)
                ?? throw new FileNotFoundException("halo.ico is not embedded in this assembly.");
            using Stream stream = info.Stream;
            var decoder = new IconBitmapDecoder(stream,
                BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
            BitmapFrame frame = decoder.Frames
                .OrderBy(candidate => candidate.PixelWidth)
                .FirstOrDefault(candidate => candidate.PixelWidth >= minimumPixels)
                ?? decoder.Frames[^1];
            frame.Freeze();
            return frame;
        }
        catch
        {
            // A soft icon beats no icon: fall back to whatever WPF picks on its own.
            var fallback = new BitmapImage();
            try
            {
                fallback.BeginInit();
                fallback.UriSource = Source;
                fallback.CacheOption = BitmapCacheOption.OnLoad;
                fallback.EndInit();
                fallback.Freeze();
            }
            catch { /* Leaves an empty BitmapImage; the Image simply draws nothing. */ }
            return fallback;
        }
    }
}
