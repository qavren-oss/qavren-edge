using System.Globalization;
using System.Text;
using Qavren.Edge.Ingestion;

namespace Qavren.Edge.Sample.Pages;

/// <summary>
/// Spec 16 / plan Task 8.2: ONE page, not a second sample app. Pick a location, run
/// <see cref="IIngestionPipeline.RunAsync(IngestionSource, string?, IngestionRunOptions?, CancellationToken)"/>
/// under a chosen <see cref="IngestionBudget"/> with <see cref="IProgress{T}"/> live, Stop and Prune,
/// the finished <see cref="IngestionRunResult"/> rendered in full including
/// <see cref="IngestionRunResult.SuspendReason"/>, and <see cref="IIngestionPipeline.GetStatusAsync"/>'s
/// counts underneath. The Search page's "ingested chunks" lane then reads what this page wrote.
/// <para>
/// <b>The location picker is platform-split, and that is the point of the page.</b> Windows and
/// Mac Catalyst walk a folder path with <see cref="IngestionSource.Folder"/>. Android and iOS hand
/// <see cref="FilePicker"/> results to <c>IngestionSource.Items</c>, because a SAF
/// <c>content://</c> tree is not a path <c>Directory.EnumerateFiles</c> can walk and an iOS picked
/// URL is security-scoped (spec 11.3). Shipping the desktop path on mobile would make this sample
/// a demonstration of the one shape a mobile consumer cannot use.
/// </para>
/// </summary>
public partial class IngestPage : ContentPage
{
    /// <summary>
    /// The mobile source id. STABLE across runs on purpose: every state row is keyed on
    /// (collection, source_id, document_id), so an id that changes per session turns every re-run
    /// into a full re-index (spec 11.3).
    /// </summary>
    private const string PickedSourceId = "user-picked";

    /// <summary>
    /// The desktop source id. Also stable, and deliberately NOT derived from the folder path: a
    /// Completed run prunes state rows the run did not stamp, so switching folders under one id
    /// removes the previous folder's chunks - which is what "this sample's one corpus" means.
    /// </summary>
    private const string FolderSourceId = "sample-folder";

    private static readonly (string Label, IngestionBudget Budget)[] Budgets =
    [
        ("Unlimited", IngestionBudget.Unlimited),
        ("Quick (20 s wall time)", IngestionBudget.Quick),
        ("Background (5 min wall time)", IngestionBudget.Background),
    ];

    private readonly IIngestionPipeline _pipeline;
    private readonly IProgress<IngestionProgress> _progress;
    private bool _busy;

#if ANDROID || IOS
    private List<FileResult> _picked = [];
#endif
#if MACCATALYST
    private Foundation.NSUrl? _pickedFolderUrl;
#endif

    public IngestPage(IIngestionPipeline pipeline)
    {
        InitializeComponent();
        _pipeline = pipeline;

        // Progress<T> posts to the SynchronizationContext it was constructed on - the UI thread
        // here - so RenderProgress touches controls without a dispatcher hop.
        _progress = new Progress<IngestionProgress>(RenderProgress);

        BudgetPicker.ItemsSource = Budgets.Select(b => b.Label).ToArray();
        BudgetPicker.SelectedIndex = 0;

#if ANDROID || IOS
        DesktopLocation.IsVisible = false;
#else
        MobileLocation.IsVisible = false;
#endif
    }

    protected override async void OnAppearing()
    {
        base.OnAppearing();
        await RefreshStatusAsync();
    }

    // ---------------------------------------------------------------- location: the platform split

    /// <summary>
    /// Builds the source for the current platform, or returns null with the reason on the status
    /// line. <see cref="IngestionSource.Folder"/> raises 6051 HERE - before any document - when
    /// the root is missing or unreadable, so callers wrap this in the same try as the run.
    /// </summary>
    private IngestionSource? BuildSource()
    {
#if ANDROID || IOS
        if (_picked.Count == 0)
        {
            StatusLabel.Text = "Pick at least one file first.";
            return null;
        }

        // THE MOBILE SHAPE (spec 11.3). The handle stays opaque to SP3; this page's OpenAsync is
        // what interprets it.
        return IngestionSource.Items(_picked.Select(ToItem).ToList(), PickedSourceId);
#else
        var path = FolderEntry.Text?.Trim();
        if (string.IsNullOrEmpty(path))
        {
            StatusLabel.Text = "Type or browse to a folder first.";
            return null;
        }

        // THE DESKTOP SHAPE: a real filesystem path, walked recursively, document ids relative to it.
        return IngestionSource.Folder(path, sourceId: FolderSourceId);
#endif
    }

#if ANDROID || IOS
    private static DocumentSourceItem ToItem(FileResult file) => new(
        // Stable id the app chooses - the file name, NOT the raw content:// string, which the OS
        // re-issues between sessions and which would make every re-run a full re-index.
        DocumentId: file.FileName,
        MediaType: file.ContentType ?? IngestionMediaTypes.FromExtension(file.FileName),
        // Called TWICE per document (hash pass, then extraction pass); see OpenAsync.
        OpenAsync: ct => OpenAsync(file, ct),
        Path: file.FullPath,   // diagnostics only
        SizeBytes: null);      // legitimately unknown: spec 9.1's counted-read gate is for exactly this

    private static async ValueTask<Stream> OpenAsync(FileResult file, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
#if IOS
        // Security-scoped URL: Start before EVERY open, Stop when THAT stream is disposed. SP3
        // opens each document twice, so a delegate that starts the scope once outside and never
        // stops it leaks it (spec 11.3, 16).
        var url = Foundation.NSUrl.FromFilename(file.FullPath);
        var scoped = url.StartAccessingSecurityScopedResource();
        try
        {
            return new ScopedStream(await file.OpenReadAsync().ConfigureAwait(false), url, scoped);
        }
        catch
        {
            if (scoped)
            {
                url.StopAccessingSecurityScopedResource();
            }

            throw;
        }
#else
        // Android: a SAF content:// stream is typically NOT seekable. SP3 buffers a non-seekable
        // stream once below ExtractionOptions.NonSeekableBufferLimitBytes and raises 6053 above
        // it (spec 7.1), so nothing here has to copy it.
        return await file.OpenReadAsync().ConfigureAwait(false);
#endif
    }
#endif

#if IOS
    /// <summary>Releases the security scope when the stream SP3 opened is disposed.</summary>
    private sealed class ScopedStream(Stream inner, Foundation.NSUrl url, bool scoped) : Stream
    {
        public override bool CanRead => inner.CanRead;

        public override bool CanSeek => inner.CanSeek;

        public override bool CanWrite => false;

        public override long Length => inner.Length;

        public override long Position
        {
            get => inner.Position;
            set => inner.Position = value;
        }

        public override void Flush() => inner.Flush();

        public override int Read(byte[] buffer, int offset, int count) => inner.Read(buffer, offset, count);

        public override int Read(Span<byte> buffer) => inner.Read(buffer);

        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) =>
            inner.ReadAsync(buffer, cancellationToken);

        public override long Seek(long offset, SeekOrigin origin) => inner.Seek(offset, origin);

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                inner.Dispose();
                if (scoped)
                {
                    url.StopAccessingSecurityScopedResource();
                }
            }

            base.Dispose(disposing);
        }
    }
#endif

    private async void OnPickFiles(object? sender, EventArgs e)
    {
#if ANDROID || IOS
        try
        {
            var picked = await FilePicker.Default.PickMultipleAsync(new PickOptions
            {
                PickerTitle = "Documents to ingest",
            });
            // PickMultipleAsync returns null when the picker is dismissed without a choice.
            _picked = picked?.ToList() ?? [];
            PickedLabel.Text = _picked.Count == 0
                ? "Nothing picked."
                : $"{_picked.Count} file(s): " + string.Join(", ", _picked.Select(f => f.FileName));
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            StatusLabel.Text = "Picker failed: " + ex.Message;
        }
#else
        StatusLabel.Text = "File picking is the mobile path; on desktop browse to a folder.";
        await Task.CompletedTask;
#endif
    }

    private async void OnBrowse(object? sender, EventArgs e)
    {
#if WINDOWS
        // MAUI 10 ships no FolderPicker; WinRT's needs the window handle for an unpackaged app.
        if (Window?.Handler?.PlatformView is not MauiWinUIWindow native)
        {
            StatusLabel.Text = "No native window to attach the folder picker to.";
            return;
        }

        var picker = new Windows.Storage.Pickers.FolderPicker();
        picker.FileTypeFilter.Add("*");
        WinRT.Interop.InitializeWithWindow.Initialize(picker, native.WindowHandle);
        var folder = await picker.PickSingleFolderAsync();
        if (folder is not null)
        {
            FolderEntry.Text = folder.Path;
        }
#elif MACCATALYST
        // A folder from the document picker is a security-scoped URL: the scope is entered around
        // the whole run (see EnterFolderScope), not per open, because IngestionSource.Folder walks
        // the directory itself and every open under it rides on the folder's scope.
        var url = await PickFolderAsync();
        if (url?.Path is { } path)
        {
            _pickedFolderUrl = url;
            FolderEntry.Text = path;
        }
#else
        StatusLabel.Text = "Browse is the desktop path; on mobile pick files.";
        await Task.CompletedTask;
#endif
    }

#if MACCATALYST
    private static Task<Foundation.NSUrl?> PickFolderAsync()
    {
        var tcs = new TaskCompletionSource<Foundation.NSUrl?>();
        var picker = new UIKit.UIDocumentPickerViewController([UniformTypeIdentifiers.UTTypes.Folder], asCopy: false)
        {
            AllowsMultipleSelection = false,
        };
        picker.DidPickDocumentAtUrls += (_, args) => tcs.TrySetResult(args.Urls.FirstOrDefault());
        picker.WasCancelled += (_, _) => tcs.TrySetResult(null);

        var host = Platform.GetCurrentUIViewController();
        if (host is null)
        {
            tcs.TrySetResult(null);
            return tcs.Task;
        }

        host.PresentViewController(picker, animated: true, completionHandler: null);
        return tcs.Task;
    }

    private sealed class FolderScope(Foundation.NSUrl url) : IDisposable
    {
        public void Dispose() => url.StopAccessingSecurityScopedResource();
    }
#endif

    /// <summary>Mac Catalyst only: holds the picked folder's security scope for one operation.</summary>
    // Two bodies rather than one with an #if inside: the Mac Catalyst shape returns the concrete
    // FolderScope (CA1859 under -warnaserror) and reads _pickedFolderUrl, the others are static
    // no-ops (CA1822). Callers see one `using var scope = EnterFolderScope();` either way.
#if MACCATALYST
    private FolderScope? EnterFolderScope()
    {
        var url = _pickedFolderUrl;
        return url is not null && url.StartAccessingSecurityScopedResource() ? new FolderScope(url) : null;
    }
#else
    private static IDisposable? EnterFolderScope() => null;
#endif

    // ---------------------------------------------------------------- run / stop / prune

    private async void OnIngest(object? sender, EventArgs e)
    {
        if (_busy)
        {
            return;
        }

        var budget = Budgets[Math.Max(0, BudgetPicker.SelectedIndex)].Budget;

        SetBusy(true);
        ResultLabel.Text = "-";
        DocumentList.ItemsSource = null;
        StatusLabel.Text = "Running...";

        try
        {
            using var scope = EnterFolderScope();
            var source = BuildSource();
            if (source is null)
            {
                return;
            }

            var result = await _pipeline.RunAsync(
                source,
                MauiProgram.ChunksCollectionName,
                new IngestionRunOptions { Budget = budget, Progress = _progress });

            RenderResult(result);
            StatusLabel.Text = result.SuspendReason is { } reason
                ? $"Run {result.Outcome} - {reason}."
                : $"Run {result.Outcome}.";
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            StatusLabel.Text = "Run threw: " + ex.Message;
        }
        finally
        {
            SetBusy(false);
            await RefreshStatusAsync();
        }
    }

    private void OnStop(object? sender, EventArgs e)
    {
        // Lands on the next committed boundary; becomes IngestionRunResult.SuspendReason.
        _pipeline.RequestStop("caller:stop");
        StatusLabel.Text = "Stop requested - the run ends on its next committed boundary.";
    }

    private async void OnPrune(object? sender, EventArgs e)
    {
        if (_busy)
        {
            return;
        }

        SetBusy(true);
        StatusLabel.Text = "Pruning...";

        try
        {
            using var scope = EnterFolderScope();
            var source = BuildSource();
            if (source is null)
            {
                return;
            }

            // Full enumeration and sweep, no write phase - the answer to "a budgeted run never
            // prunes" (spec 9.6).
            var pruned = await _pipeline.PruneAsync(source, MauiProgram.ChunksCollectionName);
            StatusLabel.Text = $"Pruned {pruned} document(s) the source no longer yields.";
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            StatusLabel.Text = "Prune threw: " + ex.Message;
        }
        finally
        {
            SetBusy(false);
            await RefreshStatusAsync();
        }
    }

    private void SetBusy(bool busy)
    {
        _busy = busy;
        IngestButton.IsEnabled = !busy;
        PruneButton.IsEnabled = !busy;
        StopButton.IsEnabled = busy;
        BudgetPicker.IsEnabled = !busy;
    }

    // ---------------------------------------------------------------- rendering

    private void RenderProgress(IngestionProgress p)
    {
        StageLabel.Text = $"Stage: {p.Stage}   (run {p.RunId})";
        DocumentsLabel.Text =
            $"Documents: seen {p.DocumentsSeen}, indexed {p.DocumentsIndexed}, skipped {p.DocumentsSkipped}, failed {p.DocumentsFailed}";
        ChunksLabel.Text =
            $"Chunks: added {p.ChunksAdded}, removed {p.ChunksRemoved}   (effective write batch {p.EffectiveWriteBatchSize})";
        CurrentLabel.Text = "Current: " + (p.CurrentDocumentId ?? "-") + (p.CurrentPage is { } page ? $"  page {page}" : string.Empty);
    }

    /// <summary>The whole record, SuspendReason included - a null is shown as "(none)", never omitted.</summary>
    private void RenderResult(IngestionRunResult r)
    {
        var sb = new StringBuilder();
        sb.Append(CultureInfo.InvariantCulture, $"RunId           {r.RunId}").AppendLine();
        sb.Append(CultureInfo.InvariantCulture, $"Collection      {r.CollectionName}").AppendLine();
        sb.Append(CultureInfo.InvariantCulture, $"SourceId        {r.SourceId}").AppendLine();
        sb.Append(CultureInfo.InvariantCulture, $"Outcome         {r.Outcome}").AppendLine();
        sb.Append(CultureInfo.InvariantCulture, $"SuspendReason   {r.SuspendReason ?? "(none)"}").AppendLine();
        sb.Append(CultureInfo.InvariantCulture, $"DocumentsSeen   {r.DocumentsSeen}").AppendLine();
        sb.Append(CultureInfo.InvariantCulture, $"DocumentsSkipped {r.DocumentsSkipped}").AppendLine();
        sb.Append(CultureInfo.InvariantCulture, $"DocumentsIndexed {r.DocumentsIndexed}").AppendLine();
        sb.Append(CultureInfo.InvariantCulture, $"DocumentsRemoved {r.DocumentsRemoved}").AppendLine();
        sb.Append(CultureInfo.InvariantCulture, $"DocumentsFailed {r.DocumentsFailed}").AppendLine();
        sb.Append(CultureInfo.InvariantCulture, $"ChunksAdded     {r.ChunksAdded}").AppendLine();
        sb.Append(CultureInfo.InvariantCulture, $"ChunksRemoved   {r.ChunksRemoved}").AppendLine();
        sb.Append(CultureInfo.InvariantCulture, $"ChunksUnchanged {r.ChunksUnchanged}").AppendLine();
        sb.Append(CultureInfo.InvariantCulture, $"ChunksRepaired  {r.ChunksRepaired}").AppendLine();
        sb.Append(CultureInfo.InvariantCulture, $"TokensEmbedded  {r.TokensEmbedded}").AppendLine();
        sb.Append(CultureInfo.InvariantCulture, $"EmbedCalls      {r.EmbedCalls}").AppendLine();
        sb.Append(CultureInfo.InvariantCulture, $"Duration        {r.Duration}").AppendLine();
        sb.Append(CultureInfo.InvariantCulture, $"RecipeHash      {r.RecipeHash}").AppendLine();
        sb.Append(CultureInfo.InvariantCulture, $"Failure         {Describe(r.Failure)}");
        ResultLabel.Text = sb.ToString();

        DocumentList.ItemsSource = r.Documents
            .Select(d => string.Create(
                CultureInfo.InvariantCulture,
                $"{d.Outcome,-11} {d.DocumentId}  [{d.ExtractorId ?? "-"}]  +{d.ChunksAdded} -{d.ChunksRemoved} ={d.ChunksUnchanged} ~{d.ChunksRepaired}  {d.TokensEmbedded} tok  {d.Duration.TotalMilliseconds:F0} ms")
                + (d.Failure is { } f ? "  " + Describe(f) : string.Empty))
            .ToArray();
    }

    private static string Describe(IngestionFailure? failure) =>
        failure is null
            ? "(none)"
            : string.Create(
                CultureInfo.InvariantCulture,
                $"{(int)failure.Code} {failure.Code}: {failure.Message}")
              + (failure.Remediation is { } fix ? " Remediation: " + fix : string.Empty);

    private async Task RefreshStatusAsync()
    {
        try
        {
            var s = await _pipeline.GetStatusAsync(MauiProgram.ChunksCollectionName);

            var sb = new StringBuilder();
            sb.Append(CultureInfo.InvariantCulture, $"Collection        {s.CollectionName}").AppendLine();
            sb.Append(CultureInfo.InvariantCulture, $"RecipeHash        {s.RecipeHash}").AppendLine();
            sb.Append(CultureInfo.InvariantCulture, $"DocumentCount     {s.DocumentCount}").AppendLine();
            sb.Append(CultureInfo.InvariantCulture, $"IndexedCount      {s.IndexedCount}").AppendLine();
            sb.Append(CultureInfo.InvariantCulture, $"FailedCount       {s.FailedCount}").AppendLine();
            sb.Append(CultureInfo.InvariantCulture, $"NoTextLayerCount  {s.NoTextLayerCount}").AppendLine();
            sb.Append(CultureInfo.InvariantCulture, $"ChunkCount        {s.ChunkCount}").AppendLine();
            sb.Append(CultureInfo.InvariantCulture, $"LastRunId         {s.LastRunId ?? "(none)"}").AppendLine();
            sb.Append(CultureInfo.InvariantCulture, $"LastOutcome       {(s.LastOutcome is { } o ? o.ToString() : "(none)")}").AppendLine();
            sb.Append(CultureInfo.InvariantCulture, $"LastSuspendReason {s.LastSuspendReason ?? "(none)"}").AppendLine();
            sb.Append(CultureInfo.InvariantCulture, $"LastRunUtc        {(s.LastRunUtc is { } t ? t.ToString("u", CultureInfo.InvariantCulture) : "(none)")}");
            CollectionStatusLabel.Text = sb.ToString();

            // recipeStaleDocuments + staleDocuments, labelled together (spec 12, 16): the ONLY sense
            // of "outstanding" that is cheap to compute. Content drift on disk is not in this number.
            var outstanding = s.RecipeStaleCount + s.StaleDocumentCount;
            KnownOutstandingLabel.Text =
                $"Known outstanding: {outstanding}   (recipe-stale {s.RecipeStaleCount} + stale {s.StaleDocumentCount})";
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            CollectionStatusLabel.Text = "GetStatusAsync threw: " + ex.Message;
            KnownOutstandingLabel.Text = "Known outstanding: -";
        }
    }
}
