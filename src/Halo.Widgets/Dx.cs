using System.Diagnostics;
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

    private static readonly ComponentLog Log2 = Log.For("dx");

    /// <summary>
    /// Level for the native-call breadcrumbs below. <see cref="LogLevel.Info"/> deliberately, not
    /// Debug: the default level is <c>info</c> (<c>settings.json &gt; diagnostics &gt; logLevel</c>)
    /// and <see cref="Log.Durable"/> drops any record below the current level — so a Debug
    /// breadcrumb would be missing from precisely the logs these exist for, the ones a user sends
    /// after the process died with no window, no tray icon and no error dialog. Fourteen lines
    /// once per startup is the entire cost.
    /// </summary>
    private const LogLevel CrumbLevel = LogLevel.Info;

    public Dx(string assetsFontDir)
    {
        CreateCore();
        LoadFonts(assetsFontDir);
    }

    /// <summary>
    /// Announce a native call synchronously, and return the stamp <see cref="Done"/> needs.
    ///
    /// <para>Every call in <see cref="CreateCore"/> ends up inside a GPU driver, where an access
    /// violation takes the process down outright — no managed exception, no unwind, no
    /// <c>finally</c>. The only evidence that survives is what already reached the disk, which is
    /// why these are <see cref="Log.Durable"/> and not queued: a breadcrumb still sitting in the
    /// async queue when the process dies is not a breadcrumb.</para>
    ///
    /// <para>The pair is the point. A line before the call proves only that it was <i>attempted</i>;
    /// it takes the matching <c>done</c> to tell "died inside it" from "died after it returned".</para>
    ///
    /// <para><b>Startup and device-recreate only</b> — this must never reach the per-frame render
    /// path.</para>
    /// </summary>
    private static long Begin(string call)
    {
        Log2.Durable(CrumbLevel, $"native begin: {call}");
        return Stopwatch.GetTimestamp();
    }

    private static void Done(string call, long startedQpc)
        => Log2.Durable(CrumbLevel, $"native done: {call} "
            + $"({(Stopwatch.GetTimestamp() - startedQpc) * 1000.0 / Stopwatch.Frequency:0.0} ms)");

    private void CreateCore()
    {
        var flags = DeviceCreationFlags.BgraSupport;

        long t = Begin("D3D11CreateDevice");
        Vortice.Direct3D11.D3D11.D3D11CreateDevice(null, DriverType.Hardware, flags,
            [Vortice.Direct3D.FeatureLevel.Level_11_1, Vortice.Direct3D.FeatureLevel.Level_11_0, Vortice.Direct3D.FeatureLevel.Level_10_0],
            out ID3D11Device? d3d).CheckError();
        Done("D3D11CreateDevice", t);
        D3D = d3d!;

        t = Begin("ID3D11Device.QueryInterface<IDXGIDevice>");
        DxgiDevice = D3D.QueryInterface<IDXGIDevice>();
        Done("ID3D11Device.QueryInterface<IDXGIDevice>", t);

        t = Begin("IDXGIDevice.GetAdapter");
        using var adapter = DxgiDevice.GetAdapter();
        Done("IDXGIDevice.GetAdapter", t);

        LogAdapter(adapter);

        t = Begin("IDXGIAdapter.GetParent<IDXGIFactory2>");
        DxgiFactory = adapter.GetParent<IDXGIFactory2>();
        Done("IDXGIAdapter.GetParent<IDXGIFactory2>", t);

        t = Begin("D2D1CreateDevice");
        D2DDevice = Vortice.Direct2D1.D2D1.D2D1CreateDevice(DxgiDevice, new Vortice.Direct2D1.CreationProperties
        {
            ThreadingMode = ThreadingMode.SingleThreaded,
            DebugLevel = DebugLevel.None,
            Options = DeviceContextOptions.None,
        });
        Done("D2D1CreateDevice", t);

        t = Begin("DCompositionCreateDevice");
        CompDevice = Vortice.DirectComposition.DComp.DCompositionCreateDevice<IDCompositionDevice>(DxgiDevice);
        Done("DCompositionCreateDevice", t);

        t = Begin("DWriteCreateFactory");
        DWrite = Vortice.DirectWrite.DWrite.DWriteCreateFactory<IDWriteFactory>(Vortice.DirectWrite.FactoryType.Shared);
        Done("DWriteCreateFactory", t);
    }

    /// <summary>
    /// Which GPU the overlay actually landed on, and the feature level D3D settled for. Nothing
    /// logged this before, and a single line reading <c>Microsoft Basic Render Driver</c> answers a
    /// whole class of "widgets won't start" / "the widgets look wrong" reports on its own.
    /// <para>Durable, like the breadcrumbs around it, so it is on disk in the right order relative
    /// to them — knowing the adapter was the basic render driver <i>and</i> that the process then
    /// died in <c>D2D1CreateDevice</c> is the useful pair of facts. Never allowed to throw: this is
    /// a diagnostic, and it must not be the reason startup fails.</para>
    /// </summary>
    private void LogAdapter(IDXGIAdapter adapter)
    {
        try
        {
            AdapterDescription ad = adapter.Description;
            Log2.Durable(LogLevel.Info,
                $"adapter: {ad.Description?.Trim()} (vendor 0x{ad.VendorId:X4} device 0x{ad.DeviceId:X4}, "
                + $"{(ulong)ad.DedicatedVideoMemory / (1024 * 1024)} MB dedicated) · feature level {D3D.FeatureLevel}");
        }
        catch (Exception ex)
        {
            Log2.Warn($"could not read the adapter description: {ex.GetType().Name}: {ex.Message}");
        }
    }

    private void LoadFonts(string fontDir)
    {
        try
        {
            if (!Directory.Exists(fontDir)) { Log2.Warn($"font dir missing: {fontDir}"); return; }
            var f5 = DWrite.QueryInterface<IDWriteFactory5>();
            var builder = f5.CreateFontSetBuilder();
            foreach (var ttf in Directory.EnumerateFiles(fontDir, "*.ttf"))
            {
                using var fileRef = f5.CreateFontFileReference(ttf);
                builder.AddFontFile(fileRef);
            }
            using var fontSet = builder.CreateFontSet();
            CustomFonts = f5.CreateFontCollectionFromFontSet(fontSet);
            Log2.Info($"custom fonts loaded from {fontDir}");
        }
        catch (Exception ex)
        {
            Log2.Error("font load failed (falling back to system fonts)", ex);
        }
    }

    /// <summary>Recreate everything after device removal / driver reset (plan §11).</summary>
    public void Recreate(string fontDir)
    {
        // Durable, and before DisposeCore: the caller's "recreating..." line is queued, so without
        // this the second set of native begin/done crumbs in a log has nothing to attribute it to.
        Log2.Durable(CrumbLevel, "recreating the device stack after device loss");
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
