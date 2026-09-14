using System.Runtime.CompilerServices;

// The test project needs to point a MetricsWriter at a private section, because constructing one
// CLEARS the whole section — a test using the real name silently wipes a running collector's
// metrics. Shadowing SharedMemoryLayout cannot achieve that from outside: SectionName is a const,
// so it is baked into this assembly at compile time and a consumer referencing Halo.Metrics.dll
// keeps getting the real one (measured 2026-09-14).
//
// The seam is therefore `internal` rather than a public parameter: this package is a published
// third-party contract (see Halo.Metrics.csproj) and widening it to serve a test would be a
// permanent, unrevertable decision made for the wrong reason. Whether third parties should get the
// same escape hatch is a separate question, on its own merits.
//
// No strong-naming anywhere in the repo, so the simple assembly name is enough. Renaming the test
// project breaks its build with a compile error — loud, not silent.
[assembly: InternalsVisibleTo("Halo.Tests")]
