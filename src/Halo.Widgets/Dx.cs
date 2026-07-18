using Vortice.DXGI;
using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.Direct2D1;
using Vortice.DirectComposition;
using Vortice.DirectWrite;
using Halo.Shared;

namespace Halo.Widgets;

/// <summary>Shared DirectX stack: one D3D11 device + D2D device + DComp device + DWrite factory
/// for all widget windows (plan §8). Recreated wholesale on device-loss.</summary>
public sealed class Dx : IDisposable
{
    public ID3D11Device D3D { get; private set; } = null!;
    public IDXGIDevice DxgiDevice { get; private set; } = null!;
    public IDXGIFactory2 DxgiFactory { get; private set; } = null!;
    public ID2D1Device D2DDevice { get; private set; } = null!;
    public IDCompositionDevice CompDevice { get; private set; } = null!;
    public IDWriteFactory DWrite { get; private set; } = null!;
    public IDWriteFontCollection1? CustomFonts { get; private set; }

    public event Action? DeviceRecreated;

    public Dx(string assetsFontDir)
    {
        CreateCore();
        LoadFonts(assetsFontDir);
    }

    private void CreateCore()
    {
        var flags = DeviceCreationFlags.BgraSupport;
        Vortice.Direct3D11.D3D11.D3D11CreateDevice(null, DriverType.Hardware, flags,
            [Vortice.Direct3D.FeatureLevel.Level_11_1, Vortice.Direct3D.FeatureLevel.Level_11_0, Vortice.Direct3D.FeatureLevel.Level_10_0],
            out ID3D11Device? d3d).CheckError();
        D3D = d3d!;
        DxgiDevice = D3D.QueryInterface<IDXGIDevice>();
        using var adapter = DxgiDevice.GetAdapter();
        DxgiFactory = adapter.GetParent<IDXGIFactory2>();
        D2DDevice = Vortice.Direct2D1.D2D1.D2D1CreateDevice(DxgiDevice, new Vortice.Direct2D1.CreationProperties
        {
            ThreadingMode = ThreadingMode.SingleThreaded,
            DebugLevel = DebugLevel.None,
            Options = DeviceContextOptions.None,
        });
        CompDevice = Vortice.DirectComposition.DComp.DCompositionCreateDevice<IDCompositionDevice>(DxgiDevice);
        DWrite = Vortice.DirectWrite.DWrite.DWriteCreateFactory<IDWriteFactory>(Vortice.DirectWrite.FactoryType.Shared);
    }

    private void LoadFonts(string fontDir)
    {
        try
        {
            if (!Directory.Exists(fontDir)) { Log.Warn($"font dir missing: {fontDir}"); return; }
            var f5 = DWrite.QueryInterface<IDWriteFactory5>();
            var builder = f5.CreateFontSetBuilder();
            foreach (var ttf in Directory.EnumerateFiles(fontDir, "*.ttf"))
            {
                using var fileRef = f5.CreateFontFileReference(ttf);
                builder.AddFontFile(fileRef);
            }
            using var fontSet = builder.CreateFontSet();
            CustomFonts = f5.CreateFontCollectionFromFontSet(fontSet);
            Log.Info($"custom fonts loaded from {fontDir}");
        }
        catch (Exception ex)
        {
            Log.Error("font load failed (falling back to system fonts)", ex);
        }
    }

    /// <summary>Recreate everything after device removal / driver reset (plan §11).</summary>
    public void Recreate(string fontDir)
    {
        DisposeCore();
        CreateCore();
        LoadFonts(fontDir);
        DeviceRecreated?.Invoke();
    }

    private void DisposeCore()
    {
        CustomFonts?.Dispose(); CustomFonts = null;
        CompDevice?.Dispose();
        D2DDevice?.Dispose();
        DxgiFactory?.Dispose();
        DxgiDevice?.Dispose();
        D3D?.Dispose();
    }

    public void Dispose() => DisposeCore();
}
