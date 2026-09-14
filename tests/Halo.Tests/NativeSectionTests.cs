using Halo.Metrics;
using Xunit;

namespace Halo.Tests;

/// <summary>
/// Ticket 01, finding 8 — <see cref="NativeSection.Create"/> maps its real length and refuses a
/// pre-existing section too small to hold it.
///
/// <para><b>This file deliberately depends on nothing but <c>NativeSection</c>.</b> It never
/// constructs a <see cref="MetricsWriter"/>, <see cref="MetricsReader"/> or
/// <see cref="CollectorSession"/>, so it needs no private-section plumbing and cannot touch a
/// running collector: <c>Create</c> already takes the section name as an ordinary parameter.
/// Keep it that way — it is the part of the ticket's coverage that stands on its own.</para>
/// </summary>
public class NativeSectionTests
{
    /// <summary>Unique per test, so xUnit's parallel runner cannot make two tests share a name.</summary>
    private static string Section([System.Runtime.CompilerServices.CallerMemberName] string caller = "")
        => $@"Local\Halo.Tests.{Environment.ProcessId}.{caller}";

    [Fact]
    public void Create_refuses_a_pre_existing_section_that_is_too_small()
    {
        string name = Section();
        // Squat the name with a section far smaller than the writer is about to clear. Before the
        // fix, Create mapped with dwNumberOfBytesToMap = 0 — "however big the existing section
        // happens to be" — and handed back a 4 KB view that MetricsWriter then memset 364,800
        // bytes of. Asking for the real length is what turns that into a clean failure.
        using var squatter = System.IO.MemoryMappedFiles.MemoryMappedFile.CreateNew(name, 4096);

        var ex = Assert.Throws<InvalidOperationException>(
            () => NativeSection.Create(name, SharedMemoryLayout.TotalSize));

        // The message has to name the cause: startup failing with a bare Win32 code sends whoever
        // hits this hunting through kernel32 docs instead of closing the other process.
        Assert.Contains("already exists and is smaller", ex.Message);
        Assert.Contains(name, ex.Message);
    }

    [Fact]
    public void Create_still_re_attaches_to_a_same_size_section()
    {
        // The legitimate case, and the one that must not regress: a collector restart re-attaches
        // to the section its own previous process left behind.
        string name = Section();
        using var existing = System.IO.MemoryMappedFiles.MemoryMappedFile.CreateNew(
            name, SharedMemoryLayout.TotalSize);

        using var s = NativeSection.Create(name, SharedMemoryLayout.TotalSize);
        Assert.True(s.IsWriter);
    }

    [Fact]
    public void Create_accepts_a_larger_pre_existing_section()
    {
        // Only the requested length is mapped, so a bigger section is harmless.
        string name = Section();
        using var existing = System.IO.MemoryMappedFiles.MemoryMappedFile.CreateNew(
            name, SharedMemoryLayout.TotalSize * 2L);

        using var s = NativeSection.Create(name, SharedMemoryLayout.TotalSize);
        Assert.True(s.IsWriter);
    }
}
