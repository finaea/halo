using System.Runtime.InteropServices;
using Halo.Shared;

namespace Halo.Collector;

/// <summary>
/// Maps volume letters to physical-disk model strings (IOCTL_STORAGE_GET_DEVICE_NUMBER +
/// IOCTL_STORAGE_QUERY_PROPERTY) so LHM storage hardware (named by model) can be matched
/// to the drive letters the widgets show. User-mode, no admin needed.
/// </summary>
public static class DriveMap
{
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
                Log.Warn($"drive map {c}: {ex.Message}");
            }
        }
        return result;
    }

    private static uint? GetDiskNumber(char letter)
    {
        using var h = CreateFileW($"\\\\.\\{letter}:", 0, FILE_SHARE_READ | FILE_SHARE_WRITE, 0, OPEN_EXISTING, 0, 0);
        if (h.IsInvalid) return null;
        var sdn = new STORAGE_DEVICE_NUMBER();
        return DeviceIoControl(h, IOCTL_STORAGE_GET_DEVICE_NUMBER, 0, 0, ref sdn, (uint)Marshal.SizeOf<STORAGE_DEVICE_NUMBER>(), out _, 0)
            ? sdn.DeviceNumber : null;
    }

    private static unsafe string? GetDiskModel(uint disk)
    {
        using var h = CreateFileW($"\\\\.\\PhysicalDrive{disk}", 0, FILE_SHARE_READ | FILE_SHARE_WRITE, 0, OPEN_EXISTING, 0, 0);
        if (h.IsInvalid) return null;
        var query = new STORAGE_PROPERTY_QUERY { PropertyId = 0 /*StorageDeviceProperty*/, QueryType = 0 };
        byte* buf = stackalloc byte[1024];
        if (!DeviceIoControlP(h, IOCTL_STORAGE_QUERY_PROPERTY, &query, (uint)Marshal.SizeOf<STORAGE_PROPERTY_QUERY>(), buf, 1024, out _, 0))
            return null;
        // STORAGE_DEVICE_DESCRIPTOR: ProductIdOffset at byte 12 from start
        int productOffset = *(int*)(buf + 12);
        int vendorOffset = *(int*)(buf + 8);
        string vendor = vendorOffset > 0 ? ReadAnsi(buf + vendorOffset) : "";
        string product = productOffset > 0 ? ReadAnsi(buf + productOffset) : "";
        string model = (vendor.Trim() + " " + product.Trim()).Trim();
        return model.Length > 0 ? model : null;
    }

    private static unsafe string ReadAnsi(byte* p)
    {
        int len = 0;
        while (len < 256 && p[len] != 0) len++;
        return System.Text.Encoding.ASCII.GetString(p, len);
    }

    private const uint FILE_SHARE_READ = 1, FILE_SHARE_WRITE = 2, OPEN_EXISTING = 3;
    private const uint IOCTL_STORAGE_GET_DEVICE_NUMBER = 0x2D1080;
    private const uint IOCTL_STORAGE_QUERY_PROPERTY = 0x2D1400;

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
