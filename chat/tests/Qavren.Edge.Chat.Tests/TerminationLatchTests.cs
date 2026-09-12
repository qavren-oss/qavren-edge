using Qavren.Edge.Chat.Internal;
using Xunit;

namespace Qavren.Edge.Chat.Tests;

/// <summary>
/// The regression guard for the <c>terminate_session</c> process abort. GenAI 0.15.2 leaves
/// <c>OgaGenerator_IsDone</c> outside its C-API <c>OGA_TRY</c>/<c>OGA_CATCH</c> boundary
/// (<c>src/ort_genai_c.cpp:470</c>) while <c>Generator::IsDone</c> opens with
/// <c>ThrowErrorIfSessionTerminated</c> and throws
/// <c>std::runtime_error("Session in Terminated state, exiting!")</c>
/// (<c>src/generators.cpp:784</c> and <c>src/generators.cpp:51</c>), so that throw unwinds into the
/// P/Invoke frame, finds no handler and reaches <c>std::terminate</c> - SIGABRT, exit 134, no
/// catchable .NET exception. <see cref="TerminationLatchedGenerator"/> is what stops the call from
/// happening at all. Here the abort is modelled as a managed throw, so a regression FAILS this test
/// instead of killing the test host.
/// </summary>
public class TerminationLatchTests
{
    [Fact]
    public void IsDoneNeverReachesATerminatedGeneratorAndStillReportsDone()
    {
        var inner = new NativeLikeGenerator();
        using var guarded = new TerminationLatchedGenerator(inner);

        Assert.False(guarded.IsDone());
        Assert.Equal(1, inner.IsDoneCalls);

        guarded.SetRuntimeOption("terminate_session", "1");

        Assert.True(guarded.IsTerminated);
        Assert.True(guarded.IsDone());
        Assert.True(guarded.IsDone());

        // The native call was never made again. That is the whole fix.
        Assert.Equal(1, inner.IsDoneCalls);
    }

    [Fact]
    public void ClearingTheFlagWithZeroUnlatchesIt()
    {
        var inner = new NativeLikeGenerator();
        using var guarded = new TerminationLatchedGenerator(inner);

        guarded.SetRuntimeOption("terminate_session", "1");
        Assert.True(guarded.IsTerminated);

        // GenAI accepts "0" and clears session_terminated_ (src/models/model.cpp:149-160).
        guarded.SetRuntimeOption("terminate_session", "0");

        Assert.False(guarded.IsTerminated);
        Assert.False(guarded.IsDone());
        Assert.Equal(1, inner.IsDoneCalls);
    }

    [Fact]
    public async Task ATerminateFromAnotherThreadNeverInterleavesWithIsDone()
    {
        // Tier-2's TerminateActiveGenerationFromAnotherThread... stress loop, with no natives: the
        // decode loop reads IsDone while another thread sets terminate_session. Before the latch,
        // the window between "TerminationRequested is still false" and the IsDone call was wide
        // enough - a decode, a StringBuilder append and an awaited channel write - to abort the
        // process, which is why the crash was timing-dependent and lane-dependent.
        const int Rounds = 200;

        for (var round = 0; round < Rounds; round++)
        {
            var inner = new NativeLikeGenerator();
            using var guarded = new TerminationLatchedGenerator(inner);

            var hammer = Task.Run(() =>
            {
                for (var i = 0; i < 50; i++)
                {
                    guarded.SetRuntimeOption("terminate_session", "1");
                }
            }, TestContext.Current.CancellationToken);

            // A call that got through to the terminated inner generator throws here, which is the
            // managed stand-in for the abort.
            for (var i = 0; i < 200 && !guarded.IsDone(); i++)
            {
                _ = guarded.TokenCount();
            }

            await hammer.ConfigureAwait(true);
            Assert.True(guarded.IsTerminated);
            Assert.True(guarded.IsDone());
        }
    }

    [Fact]
    public void ATerminateThatLosesTheRaceWithDisposalDoesNotReachTheNative()
    {
        var inner = new NativeLikeGenerator();
        var guarded = new TerminationLatchedGenerator(inner);

        guarded.Dispose();
        guarded.Dispose();

        // Calling into a freed native generator is an access violation, not an exception, so the
        // latch swallows a terminate that lost the race with disposal.
        guarded.SetRuntimeOption("terminate_session", "1");

        Assert.Equal(1, inner.DisposeCalls);
        Assert.Empty(inner.RuntimeOptions);
        Assert.True(guarded.IsDone());
        Assert.Equal(0, inner.IsDoneCalls);
    }

    /// <summary>
    /// A generator with GenAI 0.15.2's terminated-state behaviour, entry point for entry point:
    /// <c>IsDone</c>, <c>AppendTokens</c> and <c>GenerateNextToken</c> throw once
    /// <c>terminate_session</c> is set; <c>TokenCount</c> and <c>GetSequence</c> do not check it.
    /// </summary>
    private sealed class NativeLikeGenerator : IChatGenerator
    {
        private bool _terminated;
        private int _isDoneCalls;

        public int IsDoneCalls => Volatile.Read(ref _isDoneCalls);

        public int DisposeCalls { get; private set; }

        public List<(string Key, string Value)> RuntimeOptions { get; } = [];

        public bool IsDone()
        {
            ThrowIfTerminated();
            Interlocked.Increment(ref _isDoneCalls);
            return false;
        }

        public void AppendTokens(ReadOnlySpan<int> tokens) => ThrowIfTerminated();

        public ulong TokenCount() => 0;

        public void GenerateNextToken() => ThrowIfTerminated();

        public int LastToken() => -1;

        public void SetRuntimeOption(string key, string value)
        {
            RuntimeOptions.Add((key, value));
            if (key == "terminate_session")
            {
                Volatile.Write(ref _terminated, value != "0");
            }
        }

        public void Dispose() => DisposeCalls++;

        private void ThrowIfTerminated()
        {
            if (Volatile.Read(ref _terminated))
            {
                throw new InvalidOperationException("Session in Terminated state, exiting!");
            }
        }
    }
}
