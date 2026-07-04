// AssemblyFixtures.cs — session 15 (no-ADB auto-disable task, incidental fix).
//
// Adding AdbAutoDisableTests.cs (a new test CLASS, hence a new xUnit
// collection by default) shifted thread-pool/GC timing enough to newly
// surface an already-known-but-previously-dormant race: EndToEndWiringTests
// runs the REAL native DXGI/D3D11 capture pipeline (see its own header
// comment on "process-global native state"/session 11's AccessViolation
// note), and xUnit parallelizes different test collections by default --
// so once a second unrelated test class existed, it could run concurrently
// with EndToEndWiringTests' real-capture test and perturb its timing enough
// to crash the whole test host process (Test Run Aborted, observed here
// once, gone after this fix). AdbAutoDisableTests itself never touches
// native capture (it injects a NoOpFrameSource) -- the fix is simply to stop
// xUnit from running test collections concurrently in this assembly at all,
// which matches how these tests already behaved back when EndToEndWiringTests
// was the only class present (i.e. this restores, rather than changes, the
// effective concurrency this suite has always assumed).
using Xunit;

[assembly: CollectionBehavior(DisableTestParallelization = true)]
