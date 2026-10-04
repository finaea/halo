using Vortice.WIC;

namespace Halo.Widgets.Harness;

/// <summary>
/// A rendered panel: premultiplied BGRA, tightly packed (stride = <c>Width × 4</c>). PNG in and out
/// goes through WIC, which un-premultiplies on the way out and re-premultiplies on the way in, so
/// a decoded golden and a fresh render are compared in the same format the renderer produced.
/// </summary>
public sealed class PanelImage(int width, int height, byte[] pixels)
{
    public int Width { get; } = width;
    public int Height { get; } = height;
    public byte[] Pixels { get; } = pixels;

    public byte[] EncodePng()
    {
        using var wic = new IWICImagingFactory();
        using var ms = new MemoryStream();
        using (var encoder = wic.CreateEncoder(ContainerFormatGuids.Png))
        {
            encoder.Initialize(ms);
            using var frame = encoder.CreateNewFrame(out var props);
            frame.Initialize(props);
            frame.SetSize((uint)Width, (uint)Height);
            Guid pf = PixelFormat.Format32bppBGRA;
            frame.SetPixelFormat(ref pf);
            unsafe
            {
                fixed (byte* p = Pixels)
                {
                    using var source = wic.CreateBitmapFromMemory((uint)Width, (uint)Height,
                        PixelFormat.Format32bppPBGRA, (uint)(Width * 4), (uint)Pixels.Length, p);
                    frame.WriteSource(source);
                }
            }
            frame.Commit();
            encoder.Commit();
            props?.Dispose();
        }
        return ms.ToArray();
    }

    public void SavePng(string path)
    {
        string? dir = Path.GetDirectoryName(Path.GetFullPath(path));
        if (dir != null) Directory.CreateDirectory(dir);
        File.WriteAllBytes(path, EncodePng());
    }

    public static PanelImage DecodePng(byte[] png)
    {
        using var wic = new IWICImagingFactory();
        using var ms = new MemoryStream(png);
        using var decoder = wic.CreateDecoderFromStream(ms);
        using var frame = decoder.GetFrame(0);
        using var converter = wic.CreateFormatConverter();
        converter.Initialize(frame, PixelFormat.Format32bppPBGRA);
        var size = frame.Size;
        var pixels = new byte[size.Width * size.Height * 4];
        unsafe
        {
            fixed (byte* p = pixels)
                converter.CopyPixels((uint)(size.Width * 4), (uint)pixels.Length, (nint)p);
        }
        return new PanelImage(size.Width, size.Height, pixels);
    }
}
