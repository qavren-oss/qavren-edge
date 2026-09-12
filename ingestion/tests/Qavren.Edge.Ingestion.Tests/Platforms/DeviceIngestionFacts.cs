using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using Qavren.Edge.Ingestion.Pdf;
using Qavren.Edge.Ingestion.Tests.Integration;
using Qavren.Edge.Ingestion.Tests.Runtime;
using Qavren.Edge.Lifecycle;
using Xunit;

namespace Qavren.Edge.Ingestion.Tests.Platforms;

/// <summary>
/// Spec 14.6's device-only assertions this project owns (plan Task 7.1 Step 9): a 200-page PDF
/// inside a <c>Quick</c> budget under an allocation ceiling, critical memory pressure mid-run
/// through the real platform lifecycle hub, and the WHOLE observer chain's <c>Sleeping</c> cost
/// against the number that decides whether an iOS app is killed.
/// </summary>
/// <remarks>
/// This file compiles for EVERY TFM, <c>net10.0</c> included - only its
/// <see cref="DeviceFactAttribute"/>s skip on the host. It uses no platform API, so it needs no
/// <c>Platforms\**</c> compile guard in the csproj; what makes these device facts is the runtime
/// they execute on - the real Android <c>.so</c> and iOS xcframework natives, the real SQLite
/// WAL checkpoint, a real device's allocator.
/// </remarks>
[SuppressMessage(
    "Reliability",
    "CA2007:Consider calling ConfigureAwait on the awaited task",
    Justification =
        "xunit ships a CA2007 suppressor for test methods, but it recognises [Fact]/[Theory] " +
        "literally and not a derived attribute, so every await under [DeviceFact] trips the rule " +
        "on the net10.0 lane (the device lanes already NoWarn it in the csproj). Its fix - " +
        "ConfigureAwait(false) in a test body - is what xUnit1030 forbids.")]
public sealed class DeviceIngestionFacts
{
    private const int Pages = 200;

    /// <summary>
    /// Total managed allocation over the whole run (<c>GC.GetTotalAllocatedBytes</c> is cumulative,
    /// so this is every byte allocated, not the peak heap). A host x64 run over the same 200 pages
    /// measured 534 MB on 2026-09-11, almost all of it PdfPig's per-page parsing garbage; the
    /// ceiling is set at roughly twice that, so it catches a regression that buffers every page's
    /// content at once or re-parses the document per chunk, without failing on allocator noise.
    /// </summary>
    private const long AllocationCeilingBytes = 1024L * 1024 * 1024;

    /// <summary>Spec 14.6: the whole chain, with margin inside iOS's ~5 s window.</summary>
    private static readonly TimeSpan SleepingCeiling = TimeSpan.FromSeconds(3);

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    [DeviceFact]
    public async Task A_two_hundred_page_pdf_ingests_inside_a_Quick_budget_under_the_allocation_ceiling()
    {
        var pdf = GeneratedPdf.Build(Pages);
        using var host = await IntegrationHost.StartAsync(new IntegrationHostOptions
        {
            AfterIngestion = edge => edge.AddPdfExtractor(),
        });

        var source = IngestionSource.Single(
            "long.pdf",
            IngestionMediaTypes.Pdf,
            _ => new ValueTask<Stream>(new MemoryStream(pdf, writable: false)),
            sourceId: "device",
            sizeBytes: pdf.Length);

        var allocatedBefore = GC.GetTotalAllocatedBytes(precise: true);
        var clock = Stopwatch.StartNew();

        var result = await host.Pipeline.RunAsync(
            source, options: new IngestionRunOptions { Budget = IngestionBudget.Quick }, cancellationToken: Token);

        clock.Stop();
        var allocated = GC.GetTotalAllocatedBytes(precise: true) - allocatedBefore;
        TestContext.Current.TestOutputHelper?.WriteLine(string.Format(
            CultureInfo.InvariantCulture,
            "200-page PDF: {0} chunks in {1} ms, {2:F1} MB allocated.",
            result.ChunksAdded,
            clock.ElapsedMilliseconds,
            allocated / 1048576.0));

        // Inside the budget: Completed, not Suspended on budget:duration.
        Assert.Equal(IngestionRunOutcome.Completed, result.Outcome);
        Assert.Null(result.SuspendReason);
        Assert.Equal(1, result.DocumentsIndexed);
        Assert.True(result.ChunksAdded >= Pages / 4, $"expected a real text layer across 200 pages, got {result.ChunksAdded} chunks");
        Assert.True(
            allocated < AllocationCeilingBytes,
            $"ingesting a 200-page PDF allocated {allocated / 1048576.0:F1} MB, over the {AllocationCeilingBytes / 1048576} MB ceiling");
    }

    [DeviceFact]
    public async Task Critical_memory_pressure_mid_run_suspends_with_memory_critical_into_a_resumable_state()
    {
        using var host = await IntegrationHost.StartAsync();
        var gated = new GatedSource(gateAt: 2);
        for (var i = 0; i < 5; i++)
        {
            gated.Add($"doc{i}.txt", Prose.Paragraph(i, 30));
        }

        var run = host.Pipeline.RunAsync(gated, cancellationToken: Token);
        await gated.Reached.WaitAsync(TimeSpan.FromSeconds(30), Token).ConfigureAwait(true);

        // Through SP1's hub, which is what the platform bridge raises on - not SP3's observer alone.
        await host.Lifecycle.RaiseMemoryPressureAsync(EdgeMemoryPressure.Critical, Token);
        gated.Release();

        var suspended = await run.ConfigureAwait(true);

        Assert.Equal(IngestionRunOutcome.Suspended, suspended.Outcome);
        Assert.Equal("memory:critical", suspended.SuspendReason);
        Assert.Equal(2, suspended.DocumentsIndexed);
        Assert.Equal(2, await Db.CountAsync(host.Database, Db.DocumentTable));

        await host.Lifecycle.RaiseResumedAsync(Token);
        var plain = new RecordingSource("gated");
        for (var i = 0; i < 5; i++)
        {
            plain.Add($"doc{i}.txt", Prose.Paragraph(i, 30));
        }

        var resumed = await host.Pipeline.RunAsync(plain, cancellationToken: Token);

        Assert.Equal(IngestionRunOutcome.Completed, resumed.Outcome);
        Assert.Equal(2, resumed.DocumentsSkipped);
        Assert.Equal(3, resumed.DocumentsIndexed);
        Assert.Equal(5, await Db.CountAsync(host.Database, Db.DataTable));
    }

    /// <summary>
    /// <c>RaiseSleepingAsync</c> - the whole observer chain, not SP3's link - with a run active and
    /// one full-text collection. SP3's own observer is asserted under <c>SleepGraceBudget</c>
    /// elsewhere; the number that decides whether an iOS app is killed is
    /// <c>SP3 + SP2's merge + SP1's WAL checkpoint</c>, and spec 12's arithmetic is only credible
    /// if something measures the sum.
    /// </summary>
    [DeviceFact]
    public async Task RaiseSleepingAsync_returns_inside_three_seconds_with_a_run_active_and_a_full_text_collection()
    {
        using var host = await IntegrationHost.StartAsync();

        // Several hundred chunks, so SP2's merge and SP1's checkpoint have real pages to work on.
        var warm = new RecordingSource("warm");
        for (var i = 0; i < 12; i++)
        {
            warm.Add($"warm{i}.txt", Prose.Document(25, 120, firstIndex: i * 100));
        }

        var written = await host.Pipeline.RunAsync(warm, cancellationToken: Token);
        Assert.True(written.ChunksAdded >= 300, $"expected several hundred chunks, got {written.ChunksAdded}");

        var gated = new GatedSource(gateAt: 2);
        for (var i = 0; i < 6; i++)
        {
            gated.Add($"doc{i}.txt", Prose.Document(3, 60, firstIndex: i * 10));
        }

        var run = host.Pipeline.RunAsync(gated, cancellationToken: Token);
        await gated.Reached.WaitAsync(TimeSpan.FromSeconds(30), Token).ConfigureAwait(true);

        var clock = Stopwatch.StartNew();
        await host.Lifecycle.RaiseSleepingAsync(Token);
        clock.Stop();

        TestContext.Current.TestOutputHelper?.WriteLine(string.Format(
            CultureInfo.InvariantCulture, "RaiseSleepingAsync (SP3 + SP2 merge + SP1 checkpoint): {0} ms.", clock.ElapsedMilliseconds));

        Assert.True(
            clock.Elapsed < SleepingCeiling,
            $"RaiseSleepingAsync took {clock.Elapsed.TotalMilliseconds:F0} ms with a run active; the ceiling is " +
            $"{SleepingCeiling.TotalSeconds:F0} s to leave margin inside iOS's ~5 s window. A failure here is a " +
            "signal to lower SleepGraceBudget, not to raise the ceiling.");

        gated.Release();
        var result = await run.ConfigureAwait(true);

        Assert.Equal(IngestionRunOutcome.Suspended, result.Outcome);
        Assert.Equal("lifecycle:sleeping", result.SuspendReason);
        Assert.Contains(host.Logs.Lines, l => l.StartsWith("803|", StringComparison.Ordinal));
    }
}
