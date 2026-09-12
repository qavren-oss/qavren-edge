using Qavren.Edge.Chat.Tests;
using Xunit;

// Serial, on purpose, and for a reason the tier-1 half of this project cannot see: tier 2 runs
// real ORT GenAI natives in THIS process. The GenAI C API is documented as not thread safe, the
// OgaHandle and ORT's OrtEnv are both process-wide singletons, and three tier-1 tests assert
// OrtEnv.IsCreated == false - which is true only until the first tier-2 host starts. Running
// collections in parallel would race a tier-1 assertion about a process-wide flag against a
// tier-2 test flipping it. Sub-project 2's conformance project makes the same call for the same
// reason.
[assembly: CollectionBehavior(DisableTestParallelization = true)]

// ...and ordered, so the collections that load natives run LAST. Tier 1 asserts the state of the
// process BEFORE any native is touched; tier 2 and tier 3 then prove the natives. The orderer is
// what turns "usually" into "always" - xunit's default collection order is deliberately random.
[assembly: TestCollectionOrderer(typeof(NativesLastCollectionOrderer))]
