using Xunit;

// SqliteConnection.ClearAllPools() is process-wide. SP1's SqliteLifecycleObserver calls it on every
// host stop, and IntegrationHost.Dispose and several durability tests call it directly - so every
// test class in this assembly that disposes an IngestionTestHost or an IntegrationHost clears the
// pooled physical connections of every OTHER database in the process. Under xunit v3's default
// class-level parallelism that surfaces as ObjectDisposedException on a sqlite3 handle inside
// EdgeDatabase.OpenCoreAsync, or as a run that silently loses a batch (four documents of twelve
// re-indexed on a second pass that should skip them all) - two in nine full runs on the wave-12
// box, never when a test runs alone. Same hazard and same remedy as foundation's
// Qavren.Edge.Sqlite.Tests/AssemblyInfo.cs: collections run serially in this assembly.
[assembly: CollectionBehavior(DisableTestParallelization = true)]
