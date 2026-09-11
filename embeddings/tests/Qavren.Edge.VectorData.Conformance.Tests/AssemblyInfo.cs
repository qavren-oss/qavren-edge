using Xunit;

// Every fixture in this assembly shares ONE EdgeTestStore, which means one SQLite file. Running
// collections in parallel would have several suites writing that file - and its vec0 and FTS5
// shadow tables - at once, which surfaces as intermittent SQLITE_BUSY rather than as a provider
// bug. The conformance suites are I/O-bound against a local file; serialising them costs seconds.
[assembly: CollectionBehavior(DisableTestParallelization = true)]
