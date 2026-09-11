using System.Globalization;
using Microsoft.Extensions.Logging;
using Qavren.Edge.Sqlite;

namespace Qavren.Edge.Ingestion.Internal;

/// <summary>
/// Spec 10.1's sequential stages, in one class:
/// <c>enumerate → [size gate] → hash → [hash gate] → extract → chunk → hash chunks → diff →
/// embed+upsert (windowed) → delete → repair → state row</c>. No channels, no parallel stages.
/// </summary>
internal sealed class IngestionRunner
{
    private const string SuspendCallerStop = "caller:stop";

    private readonly IngestionRegistration _registration;
    private readonly IngestionStateStore _state;
    private readonly IEdgeDatabase _database;
    private readonly ChunkWriter _writer;
    private readonly IChunkTokenizer _tokenizer;
    private readonly IIngestionThrottle _throttle;
    private readonly DocumentExtractorRegistry _extractors;
    private readonly IngestionRunControl _control;
    private readonly TimeProvider _time;
    private readonly ILogger _logger;
    private readonly string _dataTable;

    public IngestionRunner(
        IngestionRegistration registration,
        IngestionStateStore state,
        IEdgeDatabase database,
        ChunkWriter writer,
        IChunkTokenizer tokenizer,
        IIngestionThrottle throttle,
        DocumentExtractorRegistry extractors,
        IngestionRunControl control,
        TimeProvider timeProvider,
        ILogger logger,
        string dataTable)
    {
        _registration = registration;
        _state = state;
        _database = database;
        _writer = writer;
        _tokenizer = tokenizer;
        _throttle = throttle;
        _extractors = extractors;
        _control = control;
        _time = timeProvider;
        _logger = logger;
        _dataTable = dataTable;
    }

    /// <summary>
    /// The most recent progress, or null between runs. It lives on the registration so spec 12's
    /// three <c>activeRun*</c> keys can read it from a synchronous <c>Describe()</c>.
    /// </summary>
    public IngestionProgress? Current
    {
        get => _registration.ActiveProgress;
        private set => _registration.ActiveProgress = value;
    }

    /// <summary>
    /// Mints a run id. The pipeline calls this before it closes the concurrency gate, so
    /// <see cref="EdgeErrorCode.IngestionRunAlreadyActive"/> (6008) can name the run that is
    /// actually active rather than a placeholder.
    /// </summary>
    public static string NewRunId() => Guid.NewGuid().ToString("N");

    public async Task<IngestionRunResult> RunAsync(
        IngestionSource source, IngestionRunOptions options, string runId, CancellationToken cancellationToken)
    {
        var opts = _registration.Options;

        // The extractor-INDEPENDENT baseline. Every document composes its own recipe from this one
        // by substituting the SELECTED extractor's fingerprint (spec 9.2), and it is that
        // per-document hash that is written to and compared against recipe_hash.
        var recipe = _registration.Recipe
            ?? throw new EdgeIngestionException(
                EdgeErrorCode.TokenCounterMissing,
                "The ingestion recipe has not been frozen. The order-400 startup task did not run.")
            {
                Remediation = "Start the Qavren.Edge host (EdgeHost/AddEdge) before calling RunAsync.",
            };

        var startedUtc = _time.GetUtcNow();
        var startTimestamp = _time.GetTimestamp();
        var counters = new RunCounters();
        var documents = new List<IngestionDocumentResult>();
        var seenIds = new HashSet<string>(StringComparer.Ordinal);
        var liveIds = new HashSet<string>(StringComparer.Ordinal);

        var budget = options.Budget ?? IngestionBudget.Unlimited;
        var deleteMissing = options.DeleteMissing ?? opts.DeleteMissingDocuments;
        var configuredBatch = options.WriteBatchSize ?? opts.WriteBatchSize;
        var failFast = options.FailFast || !opts.ContinueOnDocumentError;

        var outcome = IngestionRunOutcome.Completed;
        string? suspendReason = null;
        IngestionFailure? runFailure = null;
        var consecutiveErrors = 0;
        var loggedRecipeDrift = false;
        var lastProgress = startTimestamp;

        await _state.VerifyAsync(cancellationToken).ConfigureAwait(false);
        await _state.BeginRunAsync(
            opts.CollectionName, runId, source.Id, recipe.HashValue, startedUtc, cancellationToken)
            .ConfigureAwait(false);

        _control.BeginRun();
        IngestionLog.RunStarted(_logger, runId, source.Id, opts.CollectionName);

        try
        {
            await foreach (var item in source.EnumerateAsync(cancellationToken).ConfigureAwait(false))
            {
                Report(IngestionStage.Enumerating, currentDocumentId: null, force: false);

                var gate = await AwaitGateAsync(
                    budget, configuredBatch, counters, startTimestamp, documentBoundary: true, cancellationToken)
                    .ConfigureAwait(false);
                if (gate.Stop)
                {
                    outcome = gate.Outcome;
                    suspendReason = gate.Reason;
                    break;
                }

                if (options.Filter is { } filter && !filter(item))
                {
                    continue;
                }

                counters.DocumentsSeen++;
                liveIds.Add(item.DocumentId);

                if (!seenIds.Add(item.DocumentId))
                {
                    // 6054. Duplicate detection belongs to the RUNNER, not to any source: Items
                    // takes a consumer-supplied enumerable SP3 does not control, and Folder can
                    // legitimately produce the same id twice under overlapping patterns. The
                    // second occurrence never reaches the extractor, so the first document's
                    // chunks stay intact.
                    var duplicate = new IngestionFailure(
                        EdgeErrorCode.IngestionDuplicateDocumentId,
                        string.Format(
                            CultureInfo.InvariantCulture,
                            "Document id '{0}' was yielded more than once by source '{1}' in run {2}.",
                            item.DocumentId,
                            source.Id,
                            runId),
                        ExtractorId: null,
                        "Document ids must be unique within a source. Check for overlapping search patterns.");

                    counters.DocumentsFailed++;
                    documents.Add(new IngestionDocumentResult(
                        item.DocumentId, IngestionDocumentOutcome.Failed, null, 0, 0, 0, 0, 0, TimeSpan.Zero, duplicate));
                    IngestionLog.DocumentFailed(
                        _logger, item.DocumentId, duplicate.Code, duplicate.Message, null);
                    continue;
                }

                DocumentRun documentRun;
                try
                {
                    documentRun = await ProcessDocumentAsync(
                        source,
                        item,
                        runId,
                        recipe,
                        options,
                        configuredBatch,
                        budget,
                        counters,
                        startTimestamp,
                        () => loggedRecipeDrift,
                        () => loggedRecipeDrift = true,
                        cancellationToken).ConfigureAwait(false);
                }
                catch (EdgeIngestionException ex) when (ex.Code is EdgeErrorCode.IngestionRecipeChanged)
                {
                    // A run-tier fault by construction: a recipe change dirties the whole corpus,
                    // so failing one document and continuing would leave a collection half in each
                    // recipe, which is the exact condition StrictRecipe exists to refuse.
                    throw;
                }
                catch (EdgeIngestionException ex) when (ex.Code is EdgeErrorCode.IngestionEmbeddingFailed)
                {
                    outcome = IngestionRunOutcome.Suspended;
                    suspendReason = "embed:failed";
                    runFailure = Failure(ex);
                    break;
                }
                catch (EdgeIngestionException ex) when (ex.Code is EdgeErrorCode.IngestionCheckpointWriteFailed)
                {
                    // An unwritten checkpoint means the next run redoes work it believes is done.
                    outcome = IngestionRunOutcome.Failed;
                    runFailure = Failure(ex);
                    break;
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    var failure = Failure(ex);
                    documentRun = DocumentRun.Failed(item.DocumentId, failure, TimeSpan.Zero);
                    IngestionLog.DocumentFailed(_logger, item.DocumentId, failure.Code, failure.Message, ex);

                    if (failFast)
                    {
                        throw;
                    }

                    counters.DocumentsFailed++;
                    documents.Add(documentRun.Result);
                    consecutiveErrors++;
                    if (consecutiveErrors >= opts.AbortAfterConsecutiveErrors)
                    {
                        throw new EdgeIngestionException(
                            EdgeErrorCode.IngestionRunAborted,
                            string.Format(
                                CultureInfo.InvariantCulture,
                                "Run {0} aborted after {1} consecutive document failures.",
                                runId,
                                consecutiveErrors),
                            ex)
                        {
                            RunId = runId,
                            SourceId = source.Id,
                            Remediation =
                                "Something systemic is failing, not one document. Raise " +
                                "IngestionOptions.AbortAfterConsecutiveErrors only once you know what.",
                        };
                    }

                    Report(IngestionStage.Enumerating, item.DocumentId, force: true);
                    continue;
                }

                if (documentRun.Abandoned)
                {
                    outcome = IngestionRunOutcome.Suspended;
                    suspendReason = documentRun.SuspendReason;
                    break;
                }

                counters.Apply(documentRun.Result);
                documents.Add(documentRun.Result);
                consecutiveErrors = documentRun.Result.Outcome is IngestionDocumentOutcome.Failed
                    ? consecutiveErrors + 1
                    : 0;

                if (documentRun.Result.Outcome is IngestionDocumentOutcome.Failed)
                {
                    if (failFast && documentRun.Thrown is { } original)
                    {
                        throw original;
                    }

                    if (consecutiveErrors >= opts.AbortAfterConsecutiveErrors)
                    {
                        throw new EdgeIngestionException(
                            EdgeErrorCode.IngestionRunAborted,
                            string.Format(
                                CultureInfo.InvariantCulture,
                                "Run {0} aborted after {1} consecutive document failures.",
                                runId,
                                consecutiveErrors),
                            documentRun.Thrown)
                        {
                            RunId = runId,
                            SourceId = source.Id,
                        };
                    }
                }

                Report(IngestionStage.Enumerating, item.DocumentId, force: true);
            }

            if (outcome is IngestionRunOutcome.Completed && deleteMissing)
            {
                // Spec 9.6: pruning is skipped entirely on a Suspended, Cancelled or Failed run. A
                // partial enumeration is not evidence a document is gone.
                Report(IngestionStage.Pruning, currentDocumentId: null, force: true);
                counters.DocumentsRemoved += await PruneAsync(
                    source.Id, liveIds, cancellationToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            outcome = IngestionRunOutcome.Cancelled;
            suspendReason = SuspendCallerStop;
            if (options.ThrowOnCancellation)
            {
                _control.EndRun();
                await FinishAsync(
                    runId, outcome, suspendReason, counters, startTimestamp, error: null,
                    cancellationToken: CancellationToken.None)
                    .ConfigureAwait(false);
                throw;
            }
        }
        catch (Exception ex)
        {
            outcome = IngestionRunOutcome.Failed;
            runFailure = Failure(ex);
            IngestionLog.RunFailed(_logger, runId, ex.Message, ex);
            if (failFast || ex is EdgeIngestionException { Code: EdgeErrorCode.IngestionRecipeChanged or EdgeErrorCode.IngestionRunAborted })
            {
                _control.EndRun();
                Current = null;
                await FinishAsync(
                    runId, outcome, suspendReason, counters, startTimestamp, ex.Message, CancellationToken.None)
                    .ConfigureAwait(false);
                throw;
            }
        }
        finally
        {
            _control.EndRun();

            // Spec 12's activeRun* keys read this off the registration, so it has to be cleared on
            // EVERY exit - including the two that rethrow - or a faulted run stays "active" in
            // diagnostics forever.
            Current = null;
        }

        if (outcome is IngestionRunOutcome.Suspended)
        {
            IngestionLog.RunSuspended(_logger, runId, suspendReason ?? "unknown");
        }
        else if (outcome is IngestionRunOutcome.Completed)
        {
            IngestionLog.RunCompleted(
                _logger,
                runId,
                counters.DocumentsIndexed,
                counters.DocumentsSkipped,
                counters.DocumentsFailed,
                counters.ChunksAdded);
        }

        await FinishAsync(
            runId, outcome, suspendReason, counters, startTimestamp, runFailure?.Message, CancellationToken.None)
            .ConfigureAwait(false);

        Current = null;

        // Spec 13.3: cancellation surfaces as an OUTCOME, with no exception, because a cancelled
        // run carries counters a caller wants to read and an OperationCanceledException throws them
        // away. ThrowOnCancellation opts back in - after the run row is finished, so the state on
        // disk is the same either way.
        if (outcome is IngestionRunOutcome.Cancelled && options.ThrowOnCancellation)
        {
            cancellationToken.ThrowIfCancellationRequested();
            throw new OperationCanceledException(
                "The ingestion run was stopped by RequestStop and IngestionRunOptions.ThrowOnCancellation is set.");
        }

        return new IngestionRunResult(
            runId,
            opts.CollectionName,
            source.Id,
            outcome,
            suspendReason,
            counters.DocumentsSeen,
            counters.DocumentsSkipped,
            counters.DocumentsIndexed,
            counters.DocumentsRemoved,
            counters.DocumentsFailed,
            counters.ChunksAdded,
            counters.ChunksRemoved,
            counters.ChunksUnchanged,
            counters.ChunksRepaired,
            counters.TokensEmbedded,
            counters.EmbedCalls,
            _time.GetElapsedTime(startTimestamp),
            recipe.Hash,
            documents,
            runFailure);

        void Report(IngestionStage stage, string? currentDocumentId, bool force)
        {
            var progress = new IngestionProgress(
                runId,
                stage,
                counters.DocumentsSeen,
                counters.DocumentsIndexed,
                counters.DocumentsSkipped,
                counters.DocumentsFailed,
                counters.ChunksAdded,
                counters.ChunksRemoved,
                currentDocumentId,
                CurrentPage: null,
                _control.ApplyShrink(configuredBatch));

            Current = progress;

            // At most once per 100 ms PLUS once per document boundary - never per chunk. A
            // ten-thousand-chunk corpus would otherwise marshal ten thousand callbacks onto a UI
            // thread to report something whose unit of meaning is the document.
            var elapsed = _time.GetElapsedTime(lastProgress);
            if (!force && elapsed < TimeSpan.FromMilliseconds(100))
            {
                return;
            }

            lastProgress = _time.GetTimestamp();
            options.Progress?.Report(progress);
        }
    }

    /// <summary>
    /// Spec 9.6's explicit sweep: a full enumeration with no write phase, for the consumer who only
    /// ever runs under a <c>MaxDuration</c> budget and therefore never prunes.
    /// </summary>
    public async Task<int> PruneAsync(IngestionSource source, CancellationToken cancellationToken)
    {
        var live = new HashSet<string>(StringComparer.Ordinal);
        await foreach (var item in source.EnumerateAsync(cancellationToken).ConfigureAwait(false))
        {
            live.Add(item.DocumentId);
        }

        return await PruneAsync(source.Id, live, cancellationToken).ConfigureAwait(false);
    }

    public async Task<int> RemoveSourceAsync(string sourceId, CancellationToken cancellationToken)
    {
        var rows = await _state.ListDocumentsAsync(
            _registration.CollectionName, sourceId, cancellationToken).ConfigureAwait(false);

        foreach (var row in rows)
        {
            await _writer.DeleteDocumentChunksAsync(sourceId, row.DocumentId, cancellationToken).ConfigureAwait(false);
        }

        await _state.DeleteSourceAsync(_registration.CollectionName, sourceId, cancellationToken).ConfigureAwait(false);
        return rows.Count;
    }

    public async Task<int> RemoveDocumentAsync(
        string sourceId, string documentId, CancellationToken cancellationToken)
    {
        await _writer.DeleteDocumentChunksAsync(sourceId, documentId, cancellationToken).ConfigureAwait(false);
        return await _state.DeleteDocumentAsync(
            _registration.CollectionName, sourceId, documentId, cancellationToken).ConfigureAwait(false);
    }

    private async Task<int> PruneAsync(
        string sourceId, HashSet<string> live, CancellationToken cancellationToken)
    {
        var rows = await _state.ListDocumentsAsync(
            _registration.CollectionName, sourceId, cancellationToken).ConfigureAwait(false);

        var pruned = 0;
        foreach (var row in rows)
        {
            if (live.Contains(row.DocumentId))
            {
                continue;
            }

            await _writer.DeleteDocumentChunksAsync(sourceId, row.DocumentId, cancellationToken).ConfigureAwait(false);
            await _state.DeleteDocumentAsync(
                _registration.CollectionName, sourceId, row.DocumentId, cancellationToken).ConfigureAwait(false);
            pruned++;
        }

        if (pruned != 0)
        {
            IngestionLog.StaleDocumentsPruned(_logger, sourceId, pruned);
        }

        return pruned;
    }

    private async Task<DocumentRun> ProcessDocumentAsync(
        IngestionSource source,
        DocumentSourceItem item,
        string runId,
        IngestionRecipe recipe,
        IngestionRunOptions options,
        int configuredBatch,
        IngestionBudget budget,
        RunCounters counters,
        long startTimestamp,
        Func<bool> driftLogged,
        Action markDriftLogged,
        CancellationToken cancellationToken)
    {
        var opts = _registration.Options;
        var collection = opts.CollectionName;
        var started = _time.GetTimestamp();
        var stored = await _state.GetDocumentAsync(
            collection, source.Id, item.DocumentId, cancellationToken).ConfigureAwait(false);

        // ---- Extractor selection comes FIRST, because spec 9.2's ExtractorFingerprint is the
        // SELECTED extractor's and both the timestamp gate and the hash gate compare against the
        // recipe hash. Resolution is pure item metadata - media type, extension, CanExtract - so it
        // opens nothing and costs nothing. A document no extractor accepts gets its own recipe, so
        // registering an extractor for it later retries it automatically.
        _ = _extractors.TryResolve(item, out var extractor);
        var documentRecipe = RecipeComposition.WithExtractor(
            recipe,
            extractor is null ? RecipeComposition.NoExtractor : RecipeComposition.Fingerprint(extractor));

        // ---- Size gate, enforcement point 1: the source declared a size, so the file is never
        // opened at all (spec 9.1).
        if (item.SizeBytes is { } declared && declared > opts.MaxDocumentBytes)
        {
            return await RecordFailureAsync(
                item, source.Id, runId, stored, TooLarge(item, declared, opts.MaxDocumentBytes),
                started, cancellationToken).ConfigureAwait(false);
        }

        // ---- Timestamp gate (spec 9.4 step 1). Off by default: matching size and mtime is not
        // evidence a file is unchanged.
        if (!options.Force
            && opts.SkipUnchangedByTimestamp
            && stored is not null
            && stored.SizeBytes == item.SizeBytes
            && stored.ModifiedUtc == item.LastModifiedUtc
            && stored.RecipeHash == documentRecipe.HashValue
            && IngestionDocumentStatus.IsTerminal(stored.Status))
        {
            await _state.StampAsync(collection, source.Id, item.DocumentId, runId, cancellationToken)
                .ConfigureAwait(false);
            IngestionLog.DocumentSkipped(_logger, item.DocumentId, "timestamp");
            return DocumentRun.Skipped(item.DocumentId, stored.ExtractorId, _time.GetElapsedTime(started));
        }

        // ---- Hash pass: OpenAsync call ONE of two.
        ContentHash? contentHash;
        var hashStream = await SourceStream.OpenSeekableAsync(
            item, opts.Extraction, source.Id, cancellationToken).ConfigureAwait(false);
        await using (hashStream.ConfigureAwait(false))
        {
            contentHash = await ContentHash.OfStreamAsync(
                hashStream, opts.MaxDocumentBytes, SourceStream.BufferSize, cancellationToken).ConfigureAwait(false);
        }

        // ---- Size gate, enforcement point 2: the counted read hit the ceiling. Recorded Failed
        // with NO content hash, because the hash of a truncated read would be a lie and storing it
        // would make a later shrink of the file look unchanged.
        if (contentHash is not { } hash)
        {
            return await RecordFailureAsync(
                item, source.Id, runId, stored, TooLarge(item, null, opts.MaxDocumentBytes),
                started, cancellationToken).ConfigureAwait(false);
        }

        // ---- Hash gate (spec 9.4 step 2).
        if (!options.Force
            && stored is not null
            && stored.ContentHash == hash
            && stored.RecipeHash == documentRecipe.HashValue
            && IngestionDocumentStatus.IsTerminal(stored.Status))
        {
            await _state.StampAsync(collection, source.Id, item.DocumentId, runId, cancellationToken)
                .ConfigureAwait(false);
            IngestionLog.DocumentSkipped(_logger, item.DocumentId, "hash");
            return DocumentRun.Skipped(item.DocumentId, stored.ExtractorId, _time.GetElapsedTime(started));
        }

        // ---- Recipe drift (spec 13.3, error 6009). Both branches, and which one a consumer is on
        // is not a detail: a run that throws and a run that silently re-embeds a whole corpus are
        // the two most different outcomes this package has.
        if (stored is not null && !stored.RecipeHash.IsZero && stored.RecipeHash != documentRecipe.HashValue)
        {
            if (opts.StrictRecipe)
            {
                throw new EdgeIngestionException(
                    EdgeErrorCode.IngestionRecipeChanged,
                    string.Format(
                        CultureInfo.InvariantCulture,
                        "Document '{0}' was indexed under recipe {1}; the current recipe is {2}.",
                        item.DocumentId,
                        stored.RecipeHash.ToHex(),
                        documentRecipe.Hash))
                {
                    RunId = runId,
                    SourceId = source.Id,
                    DocumentId = item.DocumentId,
                    Remediation =
                        "Re-index deliberately by clearing IngestionOptions.StrictRecipe, or delete the state rows " +
                        "for this collection and re-run.",
                };
            }

            if (!driftLogged())
            {
                markDriftLogged();
                IngestionLog.RecipeChanged(_logger, stored.RecipeHash.ToHex(), documentRecipe.Hash);
            }
        }

        // ---- Extractor selection.
        if (extractor is null)
        {
            var failure = _extractors.NotFound(item);
            IngestionLog.DocumentUnsupported(_logger, item.DocumentId, item.MediaType);
            await WriteStateAsync(
                source.Id, item, runId, hash, documentRecipe, extractorId: null, chunkCount: 0,
                IngestionDocumentStatus.Unsupported, failure.Message, cancellationToken).ConfigureAwait(false);

            return new DocumentRun(
                new IngestionDocumentResult(
                    item.DocumentId, IngestionDocumentOutcome.Unsupported, null, 0, 0, 0, 0, 0,
                    _time.GetElapsedTime(started), failure),
                Abandoned: false,
                SuspendReason: null,
                Thrown: null);
        }

        IngestionLog.ExtractorSelected(_logger, item.DocumentId, extractor.Id, extractor.Version);

        // ---- Extraction: OpenAsync call TWO of two.
        ExtractedDocument document;
        var context = new ExtractionContext(
            source.Id, collection, opts.MaxDocumentBytes, opts.Extraction, _logger);
        try
        {
            document = await extractor.ExtractAsync(item, context, cancellationToken).ConfigureAwait(false);
        }
        catch (EdgeExtractionException ex) when (ex.Code is EdgeErrorCode.DocumentEncrypted
            or EdgeErrorCode.DocumentMalformed
            or EdgeErrorCode.DocumentEncodingUndecodable
            or EdgeErrorCode.DocumentPageBudgetExceeded)
        {
            // The four TYPED extraction faults pass through unchanged: an extractor that knows what
            // went wrong says so.
            return await RecordFailureAsync(
                item, source.Id, runId, stored, Failure(ex), started, cancellationToken, ex, hash, documentRecipe)
                .ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // 6102 - the catch-all that stops a NullReferenceException in a third-party parser from
            // becoming an unhandled run fault. An extractor that does not know gets one honest code
            // rather than a leaked exception type.
            var wrapped = new EdgeExtractionException(
                EdgeErrorCode.ExtractionFailed,
                string.Format(
                    CultureInfo.InvariantCulture,
                    "Extractor '{0}' failed on document '{1}'.",
                    extractor.Id,
                    item.DocumentId),
                ex)
            {
                ExtractorName = extractor.Id,
                SourceId = source.Id,
                DocumentId = item.DocumentId,
                RunId = runId,
                Remediation =
                    "The extractor threw an exception it does not diagnose. The original fault is the " +
                    "InnerException.",
            };

            return await RecordFailureAsync(
                item, source.Id, runId, stored, Failure(wrapped), started, cancellationToken, wrapped, hash, documentRecipe)
                .ConfigureAwait(false);
        }

        if (!document.HasTextLayer || document.Text.Length == 0)
        {
            IngestionLog.DocumentNoTextLayer(_logger, item.DocumentId, extractor.Id);
            var failure = new IngestionFailure(
                EdgeErrorCode.DocumentHasNoTextLayer,
                string.Format(
                    CultureInfo.InvariantCulture,
                    "Document '{0}' carries no extractable text layer.",
                    item.DocumentId),
                extractor.Id,
                "Run OCR over the document first, or exclude it from the source's search pattern.");

            await WriteStateAsync(
                source.Id, item, runId, hash, documentRecipe, extractor.Id, chunkCount: 0,
                IngestionDocumentStatus.NoTextLayer, failure.Message, cancellationToken).ConfigureAwait(false);

            return new DocumentRun(
                new IngestionDocumentResult(
                    item.DocumentId, IngestionDocumentOutcome.NoTextLayer, extractor.Id, 0, 0, 0, 0, 0,
                    _time.GetElapsedTime(started), failure),
                Abandoned: false,
                SuspendReason: null,
                Thrown: null);
        }

        // ---- Chunk, hash the chunks, assign duplicate ordinals and keys.
        var chunkOptions = _registration.Chunking!;
        var chunker = ChunkerFactory.Resolve(opts.ChunkerId, document.MediaType, _logger);
        var drafts = chunker.Chunk(document, chunkOptions, _tokenizer).ToList();
        var identified = ChunkDiff.Identify(source.Id, item.DocumentId, drafts);

        // ---- Diff (spec 9.4 steps 4 and 5).
        var storedRows = await ChunkDiff.ReadStoredAsync(
            _database, _dataTable, source.Id, item.DocumentId, cancellationToken).ConfigureAwait(false);
        var diff = ChunkDiff.Compare(identified, storedRows);

        var facts = new DocumentFacts(
            source.Id, item.DocumentId, extractor.Id, document.MediaType, _time.GetUtcNow());

        var added = 0;
        long tokens = 0;

        // ---- a. windowed embed + upsert, with the three evaluations before EACH window.
        for (var offset = 0; offset < diff.Added.Count;)
        {
            var gate = await AwaitGateAsync(
                budget, configuredBatch, counters, startTimestamp, documentBoundary: false, cancellationToken)
                .ConfigureAwait(false);
            if (gate.Stop)
            {
                // Suspending here abandons the document WITHOUT its state row, so it stays dirty
                // and the next run re-enters at spec 9.4 step 4 and adds only the remainder.
                return DocumentRun.Abandon(item.DocumentId, gate.Reason);
            }

            var take = Math.Min(gate.BatchSize, diff.Added.Count - offset);
            var window = diff.Added.Skip(offset).Take(take).ToList();

            Report(IngestionStage.Embedding);
            var result = await _writer.WriteWindowAsync(window, facts, cancellationToken).ConfigureAwait(false);

            counters.EmbedCalls += result.EmbedCalls;
            counters.TokensEmbedded += result.TokensEmbedded;
            counters.TokensFromGenerator = result.UsageFromGenerator;

            // Spec 9.5 / spec 12's tokensEmbeddedSource. The fact is established here, per window,
            // and the diagnostics contributor is synchronous, so it has to be carried on the
            // registration rather than recomputed.
            _registration.TokensEmbeddedSource = result.UsageFromGenerator ? "usage" : "counted";
            tokens += result.TokensEmbedded;
            added += window.Count;
            counters.ChunksAdded += window.Count;
            offset += take;
        }

        // ---- b. delete, AFTER every addition landed.
        if (diff.Removed.Count != 0)
        {
            Report(IngestionStage.Writing);
            await _writer.DeleteAsync(
                [.. diff.Removed.Select(r => r.Key)], opts.DeleteBatchSize, facts, cancellationToken)
                .ConfigureAwait(false);
            counters.ChunksRemoved += diff.Removed.Count;
        }

        // ---- c. repair.
        var repaired = 0;
        if (opts.RepairOrdinals && diff.Repaired.Count != 0)
        {
            Report(IngestionStage.Repairing);
            await _writer.RepairAsync(diff.Repaired, facts, cancellationToken).ConfigureAwait(false);
            repaired = diff.Repaired.Count;
            counters.ChunksRepaired += repaired;
        }

        counters.ChunksUnchanged += diff.Unchanged.Count;

        // ---- d. the state row, LAST.
        await WriteStateAsync(
            source.Id, item, runId, hash, documentRecipe, extractor.Id, identified.Count,
            IngestionDocumentStatus.Indexed, error: null, cancellationToken).ConfigureAwait(false);

        IngestionLog.DocumentIndexed(_logger, item.DocumentId, added, diff.Removed.Count, repaired);

        return new DocumentRun(
            new IngestionDocumentResult(
                item.DocumentId,
                IngestionDocumentOutcome.Indexed,
                extractor.Id,
                added,
                diff.Removed.Count,
                diff.Unchanged.Count,
                repaired,
                tokens,
                _time.GetElapsedTime(started),
                Failure: null),
            Abandoned: false,
            SuspendReason: null,
            Thrown: null);

        void Report(IngestionStage stage) => Current = Current is { } current
            ? current with { Stage = stage, CurrentDocumentId = item.DocumentId }
            : null;
    }

    private async Task<DocumentRun> RecordFailureAsync(
        DocumentSourceItem item,
        string sourceId,
        string runId,
        IngestionDocumentState? stored,
        IngestionFailure failure,
        long started,
        CancellationToken cancellationToken,
        Exception? thrown = null,
        ContentHash? contentHash = null,
        IngestionRecipe? recipe = null)
    {
        IngestionLog.DocumentFailed(_logger, item.DocumentId, failure.Code, failure.Message, thrown);

        // A failure that got PAST the hash pass stores the hash it computed, so the next run skips
        // the document on an unchanged hash instead of retrying a broken file forever - and a
        // recipe bump retries it automatically, which is the correct trigger.
        //
        // 6052 on the degraded (counted-read) path passes neither, so the stored hash is carried
        // forward untouched: the hash of a truncated read would be a lie, and storing it would
        // make a later shrink of the file look unchanged.
        var row = new IngestionDocumentState(
            sourceId,
            item.DocumentId,
            contentHash ?? stored?.ContentHash ?? ContentHash.Zero,
            recipe?.HashValue ?? stored?.RecipeHash ?? ContentHash.Zero,
            item.SizeBytes,
            item.LastModifiedUtc,
            stored?.ExtractorId ?? failure.ExtractorId,
            stored?.ChunkCount ?? 0,
            IngestionDocumentStatus.Failed,
            failure.Message,
            runId,
            _time.GetUtcNow());

        await CheckpointAsync(row, cancellationToken).ConfigureAwait(false);

        return new DocumentRun(
            new IngestionDocumentResult(
                item.DocumentId, IngestionDocumentOutcome.Failed, failure.ExtractorId, 0, 0, 0, 0, 0,
                _time.GetElapsedTime(started), failure),
            Abandoned: false,
            SuspendReason: null,
            thrown);
    }

    private Task WriteStateAsync(
        string sourceId,
        DocumentSourceItem item,
        string runId,
        ContentHash contentHash,
        IngestionRecipe recipe,
        string? extractorId,
        int chunkCount,
        string status,
        string? error,
        CancellationToken cancellationToken) =>
        CheckpointAsync(
            new IngestionDocumentState(
                sourceId,
                item.DocumentId,
                contentHash,
                recipe.HashValue,
                item.SizeBytes,
                item.LastModifiedUtc,
                extractorId,
                chunkCount,
                status,
                error,
                runId,
                _time.GetUtcNow()),
            cancellationToken);

    private async Task CheckpointAsync(IngestionDocumentState row, CancellationToken cancellationToken)
    {
        try
        {
            await _state.UpsertDocumentAsync(
                _registration.CollectionName, row, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            throw new EdgeIngestionException(
                EdgeErrorCode.IngestionCheckpointWriteFailed,
                string.Format(
                    CultureInfo.InvariantCulture,
                    "The ingestion state row for document '{0}' could not be written.",
                    row.DocumentId),
                ex)
            {
                SourceId = row.SourceId,
                DocumentId = row.DocumentId,
                RunId = row.LastRunId,
                Remediation =
                    "An unwritten checkpoint means the next run would redo work it believes is done, so the run " +
                    "aborts. The transaction rolled back, so the document stays at its previous state.",
            };
        }

        IngestionLog.StateCommitted(_logger, row.DocumentId, row.Status);
        _control.SignalCheckpoint();
    }

    /// <summary>
    /// <see cref="Evaluate"/> plus the waiting spec 10.2 attaches to it, which is the ONLY form the
    /// runner uses at a boundary.
    /// <para>
    /// A <see cref="ThrottlePauseBehavior.Wait"/> pause "delays and re-evaluates": the delay is
    /// followed by a fresh evaluation and the loop does not release until an evaluation stops
    /// pausing — or until the stop, cancellation or budget checks at the top of
    /// <see cref="Evaluate"/> end the run, which is what keeps a permanently-pausing throttle from
    /// wedging a run that carries a budget or a token. Delaying once and then proceeding would
    /// process the very document the throttle is still refusing, which is the whole of what
    /// <see cref="ThrottlePauseBehavior.Suspend"/> and <see cref="ThrottlePauseBehavior.Wait"/>
    /// disagree about.
    /// </para>
    /// <para>
    /// A plain (non-pause) delay is different and is honoured exactly once: the throttle asked the
    /// run to slow down, not to hold.
    /// </para>
    /// </summary>
    private async ValueTask<GateDecision> AwaitGateAsync(
        IngestionBudget budget,
        int configuredBatch,
        RunCounters counters,
        long startTimestamp,
        bool documentBoundary,
        CancellationToken cancellationToken)
    {
        var gate = Evaluate(
            budget, configuredBatch, counters, startTimestamp, documentBoundary, cancellationToken);

        while (!gate.Stop && gate.Paused)
        {
            await Task.Delay(gate.Delay, _time, cancellationToken).ConfigureAwait(false);
            gate = Evaluate(
                budget, configuredBatch, counters, startTimestamp, documentBoundary, cancellationToken);
        }

        if (!gate.Stop && gate.Delay > TimeSpan.Zero)
        {
            await Task.Delay(gate.Delay, _time, cancellationToken).ConfigureAwait(false);
        }

        return gate;
    }

    /// <summary>
    /// Spec 10.2's three evaluations, in order: stop or cancel, then budget, then throttle.
    /// Cancellation is checked HERE, before any transaction is opened, and never inside one.
    /// </summary>
    private GateDecision Evaluate(
        IngestionBudget budget,
        int configuredBatch,
        RunCounters counters,
        long startTimestamp,
        bool documentBoundary,
        CancellationToken cancellationToken)
    {
        if (_control.Stop is { Reason: { } stopReason } stop)
        {
            // Spec 10.2 step 1: a stop that came FROM LIFECYCLE is Suspended; Cancelled is the
            // caller-cancelled case. The distinction is carried by a flag rather than a
            // "lifecycle:" prefix, because the observer's memory:critical stop wears spec 10.2's
            // own reason string and has no prefix to match on.
            return new GateDecision(
                true,
                stop.FromLifecycle ? IngestionRunOutcome.Suspended : IngestionRunOutcome.Cancelled,
                stopReason,
                configuredBatch,
                TimeSpan.Zero);
        }

        if (cancellationToken.IsCancellationRequested)
        {
            return new GateDecision(
                true, IngestionRunOutcome.Cancelled, SuspendCallerStop, configuredBatch, TimeSpan.Zero);
        }

        var elapsed = _time.GetElapsedTime(startTimestamp);
        if (budget.MaxDuration is { } maxDuration && elapsed >= maxDuration)
        {
            return Suspend("budget:duration");
        }

        // MaxDocuments is a DOCUMENT-boundary ceiling only. Re-applying it before each write
        // window would abandon the very document that just made the count reach the ceiling,
        // leaving it half-written and un-checkpointed for no reason.
        if (documentBoundary && budget.MaxDocuments is { } maxDocuments && counters.DocumentsSeen >= maxDocuments)
        {
            return Suspend("budget:documents");
        }

        if (budget.MaxChunks is { } maxChunks && counters.ChunksAdded >= maxChunks)
        {
            return Suspend("budget:chunks");
        }

        if (budget.MaxTokens is { } maxTokens && counters.TokensEmbedded >= maxTokens)
        {
            return Suspend("budget:tokens");
        }

        var current = _control.ApplyShrink(configuredBatch);
        var decision = _throttle.Evaluate(new IngestionThrottleContext(
            configuredBatch, current, counters.DocumentsSeen, counters.ChunksAdded, elapsed));

        if (decision.Reason is { } signalled)
        {
            _registration.LastThrottleReason = signalled;
        }

        if (decision.Pause)
        {
            var reason = decision.Reason ?? "throttle:pause";
            _registration.LastThrottleReason = reason;
            IngestionLog.ThrottlePaused(_logger, reason);
            if (_registration.Options.PauseBehavior is ThrottlePauseBehavior.Suspend)
            {
                return Suspend(reason);
            }

            return new GateDecision(
                false,
                IngestionRunOutcome.Completed,
                reason,
                Math.Max(1, Math.Min(decision.BatchSize, current)),
                decision.Delay > TimeSpan.Zero ? decision.Delay : TimeSpan.FromMilliseconds(250),
                Paused: true);
        }

        var batch = Math.Max(1, Math.Min(decision.BatchSize <= 0 ? current : decision.BatchSize, current));
        if (batch != configuredBatch && decision.Reason is { } adjusted)
        {
            IngestionLog.ThrottleAdjusted(_logger, configuredBatch, batch, adjusted);
        }

        return new GateDecision(false, IngestionRunOutcome.Completed, decision.Reason, batch, decision.Delay);

        GateDecision Suspend(string reason) =>
            new(true, IngestionRunOutcome.Suspended, reason, configuredBatch, TimeSpan.Zero);
    }

    private Task FinishAsync(
        string runId,
        IngestionRunOutcome outcome,
        string? suspendReason,
        RunCounters counters,
        long startTimestamp,
        string? error,
        CancellationToken cancellationToken)
    {
        var finishedUtc = _time.GetUtcNow();

        // Spec 12's lastRun* keys. Recorded BEFORE the write, so a run row that fails to finish
        // still leaves diagnostics able to say what the run did - the alternative reports nothing
        // about precisely the run someone is trying to diagnose.
        _registration.LastRun = new IngestionLastRun(
            runId,
            outcome,
            suspendReason,
            finishedUtc,
            _time.GetElapsedTime(startTimestamp),
            counters.DocumentsIndexed,
            counters.DocumentsSkipped,
            counters.DocumentsFailed,
            counters.ChunksAdded,
            counters.ChunksRemoved,
            counters.EmbedCalls,
            counters.TokensEmbedded);

        return _state.FinishRunAsync(
            _registration.CollectionName,
            runId,
            outcome,
            suspendReason,
            counters.DocumentsIndexed,
            counters.ChunksAdded,
            counters.ChunksRemoved,
            error,
            finishedUtc,
            _registration.Options.RunHistoryLimit,
            cancellationToken);
    }

    private static IngestionFailure TooLarge(DocumentSourceItem item, long? declared, long ceiling) =>
        new(
            EdgeErrorCode.IngestionDocumentTooLarge,
            string.Format(
                CultureInfo.InvariantCulture,
                declared is null
                    ? "Document '{0}' exceeds IngestionOptions.MaxDocumentBytes ({2} bytes); the counted read stopped there."
                    : "Document '{0}' declares {1} bytes, over IngestionOptions.MaxDocumentBytes ({2} bytes).",
                item.DocumentId,
                declared ?? 0,
                ceiling),
            ExtractorId: null,
            "Raise IngestionOptions.MaxDocumentBytes, or exclude the file. On iOS a 200 MB PDF is a jetsam, and a " +
            "jetsam loses the run as well as the document.");

    private static IngestionFailure Failure(Exception exception) => exception switch
    {
        EdgeExtractionException extraction => new IngestionFailure(
            extraction.Code, extraction.Message, extraction.ExtractorName, extraction.Remediation),
        EdgeIngestionException ingestion => new IngestionFailure(
            ingestion.Code, ingestion.Message, ExtractorId: null, ingestion.Remediation),
        EdgeException edge => new IngestionFailure(edge.Code, edge.Message),
        _ => new IngestionFailure(EdgeErrorCode.ExtractionFailed, exception.Message),
    };

    /// <param name="Stop">The run ends here, with <paramref name="Outcome"/>.</param>
    /// <param name="Outcome">The outcome a stop carries.</param>
    /// <param name="Reason">The suspend or throttle reason string.</param>
    /// <param name="BatchSize">The batch the next write window may use.</param>
    /// <param name="Delay">How long to wait before doing anything.</param>
    /// <param name="Paused">
    /// The throttle PAUSED and <see cref="ThrottlePauseBehavior.Wait"/> is in force. It is a
    /// distinct state from a plain <paramref name="Delay"/>: a delay is honoured once and the work
    /// proceeds, a pause is re-evaluated after the delay and the work does not proceed until an
    /// evaluation stops pausing (spec 10.2).
    /// </param>
    private readonly record struct GateDecision(
        bool Stop, IngestionRunOutcome Outcome, string? Reason, int BatchSize, TimeSpan Delay, bool Paused = false);

    private sealed record DocumentRun(
        IngestionDocumentResult Result, bool Abandoned, string? SuspendReason, Exception? Thrown)
    {
        public static DocumentRun Skipped(string documentId, string? extractorId, TimeSpan duration) =>
            new(
                new IngestionDocumentResult(
                    documentId, IngestionDocumentOutcome.Skipped, extractorId, 0, 0, 0, 0, 0, duration, null),
                Abandoned: false,
                SuspendReason: null,
                Thrown: null);

        public static DocumentRun Failed(string documentId, IngestionFailure failure, TimeSpan duration) =>
            new(
                new IngestionDocumentResult(
                    documentId, IngestionDocumentOutcome.Failed, failure.ExtractorId, 0, 0, 0, 0, 0, duration, failure),
                Abandoned: false,
                SuspendReason: null,
                Thrown: null);

        public static DocumentRun Abandon(string documentId, string? reason) =>
            new(
                new IngestionDocumentResult(
                    documentId, IngestionDocumentOutcome.Skipped, null, 0, 0, 0, 0, 0, TimeSpan.Zero, null),
                Abandoned: true,
                reason,
                Thrown: null);
    }

    private sealed class RunCounters
    {
        public int DocumentsSeen { get; set; }

        public int DocumentsSkipped { get; set; }

        public int DocumentsIndexed { get; set; }

        public int DocumentsRemoved { get; set; }

        public int DocumentsFailed { get; set; }

        public int ChunksAdded { get; set; }

        public int ChunksRemoved { get; set; }

        public int ChunksUnchanged { get; set; }

        public int ChunksRepaired { get; set; }

        public long TokensEmbedded { get; set; }

        public int EmbedCalls { get; set; }

        public bool TokensFromGenerator { get; set; }

        public void Apply(IngestionDocumentResult result)
        {
            switch (result.Outcome)
            {
                case IngestionDocumentOutcome.Indexed:
                    DocumentsIndexed++;
                    break;
                case IngestionDocumentOutcome.Skipped:
                    DocumentsSkipped++;
                    break;
                case IngestionDocumentOutcome.Failed:
                    DocumentsFailed++;
                    break;
                case IngestionDocumentOutcome.Removed:
                    DocumentsRemoved++;
                    break;
                default:
                    break;
            }
        }
    }
}
