using System.Runtime.InteropServices;
using Halo.Shared;

namespace Halo.Collector;

/// <summary>
/// Maps volume letters to physical-disk model strings (IOCTL_VOLUME_GET_VOLUME_DISK_EXTENTS, or
/// IOCTL_STORAGE_GET_DEVICE_NUMBER as a fallback, + IOCTL_STORAGE_QUERY_PROPERTY) so LHM storage
/// hardware (named by model) can be matched
/// to the drive letters the widgets show. User-mode, no admin needed.
/// </summary>
public static class DriveMap
{
    private static readonly ComponentLog Log2 = Log.For("drive-map");

    /// <summary>letter (upper) → disk model string, e.g. 'C' → "Samsung SSD 990 PRO 2TB".</summary>
    public static Dictionary<char, string> LetterToModel(IEnumerable<char> letters)
    {
        var byDisk = new Dictionary<uint, string>();
        var result = new Dictionary<char, string>();
        foreach (char cRaw in letters)
        {
            char c = char.ToUpperInvariant(cRaw);
            try
            {
                uint? disk = GetDiskNumber(c);
                if (disk == null) continue;
                if (!byDisk.TryGetValue(disk.Value, out string? model))
                {
                    model = GetDiskModel(disk.Value) ?? "";
                    byDisk[disk.Value] = model;
                }
                if (model.Length > 0) result[c] = model;
            }
            catch (Exception ex)
            {
                Log2.Warn($"{c}: {ex.Message}");
            }
        }
        return result;
    }

    private static unsafe uint? GetDiskNumber(char letter)
    {
        using var h = CreateFileW($"\\\\.\\{letter}:", 0, FILE_SHARE_READ | FILE_SHARE_WRITE, 0, OPEN_EXISTING, 0, 0);
        if (h.IsInvalid) return null;

        // VOLUME_DISK_EXTENTS works for both basic and dynamic (LDM) volumes —
        // IOCTL_STORAGE_GET_DEVICE_NUMBER fails on dynamic volumes (observed: drive H).
        // Layout: NumberOfDiskExtents(4) pad(4) then extents[]: DiskNumber(4) pad(4)
        // StartingOffset(8) ExtentLength(8). A spanned volume returns several extents;
        // the first disk is good enough for a temperature readout.
        byte* buf = stackalloc byte[8 + 4 * 24];
        if (DeviceIoControlP(h, IOCTL_VOLUME_GET_VOLUME_DISK_EXTENTS, null, 0, buf, 8 + 4 * 24, out _, 0))
        {
            uint extents = *(uint*)buf;
            if (extents >= 1) return *(uint*)(buf + 8);
            return null;
        }

        // fallback for exotic volume stacks
        var sdn = new STORAGE_DEVICE_NUMBER();
        return DeviceIoControl(h, IOCTL_STORAGE_GET_DEVICE_NUMBER, 0, 0, ref sdn, (uint)Marshal.SizeOf<STORAGE_DEVICE_NUMBER>(), out _, 0)
            ? sdn.DeviceNumber : null;
    }

    private const int DescriptorBufSize = 1024;

    private static unsafe string? GetDiskModel(uint disk)
    {
        using var h = CreateFileW($"\\\\.\\PhysicalDrive{disk}", 0, FILE_SHARE_READ | FILE_SHARE_WRITE, 0, OPEN_EXISTING, 0, 0);
        if (h.IsInvalid) return null;
        var query = new STORAGE_PROPERTY_QUERY { PropertyId = 0 /*StorageDeviceProperty*/, QueryType = 0 };
        byte* buf = stackalloc byte[DescriptorBufSize];
        if (!DeviceIoControlP(h, IOCTL_STORAGE_QUERY_PROPERTY, &query, (uint)Marshal.SizeOf<STORAGE_PROPERTY_QUERY>(), buf, DescriptorBufSize, out uint returned, 0))
            return null;
        // STORAGE_DEVICE_DESCRIPTOR: Version(0) Size(4) DeviceType(8) DeviceTypeModifier(9)
        // RemovableMedia(10) CommandQueueing(11) VendorIdOffset(12) ProductIdOffset(16) ...
        int limit = (int)Math.Min(returned, DescriptorBufSize);
        if (limit < 20) return null;
        int vendorOffset = *(int*)(buf + 12);
        int productOffset = *(int*)(buf + 16);
        string vendor = ReadAnsi(buf, vendorOffset, limit);
        string product = ReadAnsi(buf, productOffset, limit);
        string model = (vendor.Trim() + " " + product.Trim()).Trim();
        return model.Length > 0 ? model : null;
    }

    /// <summary>Bounds-checked NUL-terminated ANSI read inside the descriptor buffer.</summary>
    private static unsafe string ReadAnsi(byte* buf, int offset, int limit)
    {
        if (offset <= 0 || offset >= limit) return "";
        int len = 0;
        while (offset + len < limit && buf[offset + len] != 0) len++;
        return System.Text.Encoding.ASCII.GetString(buf + offset, len);
    }

    private const uint FILE_SHARE_READ = 1, FILE_SHARE_WRITE = 2, OPEN_EXISTING = 3;
    private const uint IOCTL_STORAGE_GET_DEVICE_NUMBER = 0x2D1080;
    private const uint IOCTL_STORAGE_QUERY_PROPERTY = 0x2D1400;
    private const uint IOCTL_VOLUME_GET_VOLUME_DISK_EXTENTS = 0x560000;

    [StructLayout(LayoutKind.Sequential)]
    private struct STORAGE_DEVICE_NUMBER { public uint DeviceType, DeviceNumber, PartitionNumber; }

    [StructLayout(LayoutKind.Sequential)]
    private struct STORAGE_PROPERTY_QUERY { public int PropertyId, QueryType; public byte AdditionalParameters; }

    [DllImport("kernel32", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern Microsoft.Win32.SafeHandles.SafeFileHandle CreateFileW(string name, uint access, uint share, nint sa, uint disposition, uint flags, nint template);

    [DllImport("kernel32", SetLastError = true)]
    private static extern bool DeviceIoControl(Microsoft.Win32.SafeHandles.SafeFileHandle h, uint code, nint inBuf, uint inSize, ref STORAGE_DEVICE_NUMBER outBuf, uint outSize, out uint returned, nint overlapped);

    [DllImport("kernel32", SetLastError = true, EntryPoint = "DeviceIoControl")]
    private static extern unsafe bool DeviceIoControlP(Microsoft.Win32.SafeHandles.SafeFileHandle h, uint code, void* inBuf, uint inSize, void* outBuf, uint outSize, out uint returned, nint overlapped);
}
