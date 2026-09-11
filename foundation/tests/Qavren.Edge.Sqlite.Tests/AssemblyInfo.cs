using Xunit;

// SqliteConnection.ClearAllPools() is process-wide, and both EdgeTestHost.DisposeAsync and the
// SqliteLifecycleObserver call it. A test that asserts "the physical connection is STILL pooled"
// (MemoryPressure_BelowCritical_LeavesThePoolAlone) therefore cannot run while any other test
// class is tearing a host down. Collections run serially in this assembly for that reason.
[assembly: CollectionBehavior(DisableTestParallelization = true)]
