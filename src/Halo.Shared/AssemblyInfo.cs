using System.Runtime.CompilerServices;

// Log is process-global static state by design: 150+ call sites across three exes, no DI container,
// and a logger that needs an injected instance is a logger that is not available in the two places
// it matters most — a static field initializer and a crash handler.
//
// That makes it untestable without a reset, and the reset is `internal` rather than public on
// purpose. A public Log.Reset() would be one careless call away from wiping a running process's
// destination and drop counters, and the counters are the evidence that the logger itself is
// healthy. Tests get the seam; production code cannot reach it.
//
// No strong-naming anywhere in the repo, so the simple assembly name is enough. Renaming the test
// project breaks its build with a compile error — loud, not silent.
[assembly: InternalsVisibleTo("Halo.Tests")]
