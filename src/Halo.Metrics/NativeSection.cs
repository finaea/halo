using System.Runtime.InteropServices;

namespace Halo.Metrics;

/// <summary>
/// Minimal named-section wrapper. We roll our own instead of MemoryMappedFile because the
/// collector runs elevated while widgets run at medium IL: the section must be created with
/// an explicit DACL granting read access to non-elevated same-user processes.
/// </summary>
public sealed unsafe class NativeSection : IDisposable
{
    private nint _handle;
    public byte* Base { get; private set; }
    public bool IsWriter { get; }

    private NativeSection(nint handle, byte* @base, bool writer)
    {
        _handle = handle;
        Base = @base;
        IsWriter = writer;
    }

    /// <summary>
    /// Create (writer side). Grants GENERIC_ALL to SYSTEM/Admins, GENERIC_READ to Everyone.
    ///
    /// A named section that already exists is <b>reused</b>, not resized — Windows hands back the
    /// existing object and ignores the size argument. That is the collector-restart case and it
    /// must keep working, but it also means the view can be smaller than the caller asked for, and
    /// <see cref="MetricsWriter"/> memsets <paramref name="size"/> bytes the moment it gets one.
    /// So the view is mapped with the real length (not 0 = "whatever is there") and an existing
    /// section that is too small fails here, loudly, instead of corrupting memory past the view.
    /// </summary>
    public static NativeSection Create(string name, int size)
    {
        // D: DACL; A;;GR;;;WD = allow generic-read to Everyone; BA = builtin admins; SY = system.
        // S:(ML;;NW;;;ME) = medium mandatory label / no-write-up so medium-IL readers are fine.
        // This exact SDDL is what lets a medium-IL widget read a high-IL collector — keep verbatim.
        const string sddl = "D:(A;;GA;;;SY)(A;;GA;;;BA)(A;;GR;;;WD)S:(ML;;NW;;;ME)";
        nint sd = 0;
        if (!ConvertStringSecurityDescriptorToSecurityDescriptorW(sddl, 1, out sd, out _))
            throw new InvalidOperationException($"SDDL conversion failed: {Marshal.GetLastWin32Error()}");
        try
        {
            var sa = new SECURITY_ATTRIBUTES { nLength = (uint)sizeof(SECURITY_ATTRIBUTES), lpSecurityDescriptor = sd, bInheritHandle = 0 };
            nint h = CreateFileMappingW(-1, &sa, PAGE_READWRITE, 0, (uint)size, name);
            int createError = Marshal.GetLastWin32Error();
            if (h == 0)
                throw new InvalidOperationException($"CreateFileMapping failed: {createError}");
            bool reused = createError == ERROR_ALREADY_EXISTS;

            // Asking for the real length is the check: a view longer than the section is refused,
            // so a pre-existing undersized section cannot be mapped at all.
            byte* p = (byte*)MapViewOfFile(h, FILE_MAP_WRITE, 0, 0, (nuint)size);
            if (p == null)
            {
                int mapError = Marshal.GetLastWin32Error();
                CloseHandle(h);
                throw new InvalidOperationException(reused
                    ? $"'{name}' already exists and is smaller than the {size} bytes Halo needs (MapViewOfFile failed: {mapError}). " +
                      "Another process owns that name — close it (or end an older Halo.Collector) and start the collector again."
                    : $"MapViewOfFile failed: {mapError}");
            }
            return new NativeSection(h, p, writer: true);
        }
        finally
        {
            LocalFree(sd);
        }
    }

    /// <summary>Open existing (reader side). Returns null if the section does not exist yet.</summary>
    public static NativeSection? OpenReadOnly(string name)
    {
        nint h = OpenFileMappingW(FILE_MAP_READ, 0, name);
        if (h == 0) return null;
        byte* p = (byte*)MapViewOfFile(h, FILE_MAP_READ, 0, 0, 0);
        if (p == null) { CloseHandle(h); return null; }
        return new NativeSection(h, p, writer: false);
    }

    public void Dispose()
    {
        if (Base != null) { UnmapViewOfFile((nint)Base); Base = null; }
        if (_handle != 0) { CloseHandle(_handle); _handle = 0; }
        GC.SuppressFinalize(this);
    }

    ~NativeSection() => Dispose();

    private const uint PAGE_READWRITE = 0x04;
    private const uint FILE_MAP_READ = 0x0004;
    private const uint FILE_MAP_WRITE = 0x0002;
    private const int ERROR_ALREADY_EXISTS = 183;

    [StructLayout(LayoutKind.Sequential)]
    private struct SECURITY_ATTRIBUTES { public uint nLength; public nint lpSecurityDescriptor; public int bInheritHandle; }

    [DllImport("kernel32", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern nint CreateFileMappingW(nint hFile, SECURITY_ATTRIBUTES* lpAttributes, uint flProtect, uint dwMaximumSizeHigh, uint dwMaximumSizeLow, string lpName);

    [DllImport("kernel32", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern nint OpenFileMappingW(uint dwDesiredAccess, int bInheritHandle, string lpName);

    [DllImport("kernel32", SetLastError = true)]
    private static extern nint MapViewOfFile(nint hFileMappingObject, uint dwDesiredAccess, uint dwFileOffsetHigh, uint dwFileOffsetLow, nuint dwNumberOfBytesToMap);

    [DllImport("kernel32", SetLastError = true)]
    private static extern bool UnmapViewOfFile(nint lpBaseAddress);

    [DllImport("kernel32", SetLastError = true)]
    private static extern bool CloseHandle(nint hObject);

    [DllImport("advapi32", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool ConvertStringSecurityDescriptorToSecurityDescriptorW(string StringSecurityDescriptor, uint StringSDRevision, out nint SecurityDescriptor, out uint SecurityDescriptorSize);

    [DllImport("kernel32")]
    private static extern nint LocalFree(nint hMem);
}
