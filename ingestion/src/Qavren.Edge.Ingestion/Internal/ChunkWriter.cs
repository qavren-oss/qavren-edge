using System.Globalization;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using Qavren.Edge.Sqlite;
using MEVD = Microsoft.Extensions.VectorData;

namespace Qavren.Edge.Ingestion.Internal;

/// <summary>The per-document facts every written row carries.</summary>
/// <param name="SourceId">The source.</param>
/// <param name="DocumentId">The document.</param>
/// <param name="ExtractorId">The extractor that produced it.</param>
/// <param name="MediaType">Its media type.</param>
/// <param name="CreatedUtc">The run's clock, so one run stamps one value.</param>
internal sealed record DocumentFacts(
    string SourceId, string DocumentId, string ExtractorId, string MediaType, DateTimeOffset CreatedUtc);

/// <summary>What one write window cost.</summary>
/// <param name="EmbedCalls">
/// Spec 9.5 step a1 calls, <b>counted</b>. One per window on the happy path; on the 6206 retry path
/// it is one plus however many halved calls the retry actually issued, which is
/// <c>ceil(n / max(1, n / 2))</c> and is 3 for a window of 3.
/// </param>
/// <param name="TokensEmbedded">The window's token cost.</param>
/// <param name="UsageFromGenerator">
/// True when the count came from <c>GeneratedEmbeddings.Usage.InputTokenCount</c>, false when it
/// came from SP3's own <c>CountTokens</c> sum. Surfaced as the <c>tokensEmbeddedSource</c>
/// diagnostics key, so nobody has to guess which number they are reading.
/// </param>
internal sealed record WriteWindowResult(int EmbedCalls, long TokensEmbedded, bool UsageFromGenerator);

/// <summary>
/// Spec 9.5's four-step write protocol, in order: embed + upsert per window, then delete, then
/// repair, then the state row.
/// <para>
/// <b>The transaction invariant.</b> No method here calls an SP2 collection method from inside an
/// SP3 <c>ExecuteInTransactionAsync</c> callback. <c>ExecuteInTransactionAsync</c> opens a NEW
/// connection per call and SP2's <c>UpsertAsync</c> and <c>DeleteAsync</c> each wrap themselves in
/// one, so nesting is two connections contending for the WAL write lock — <c>SQLITE_BUSY</c>, not
/// atomicity.
/// </para>
/// </summary>
internal sealed class ChunkWriter(
    IEdgeDatabase database,
    MEVD.VectorStoreCollection<object, Dictionary<string, object?>> collection,
    IEmbeddingGenerator<string, Embedding<float>> generator,
    IChunkTokenizer tokenizer,
    string dataTable,
    ILogger logger)
{
    /// <summary>
    /// Spec 9.5 step a. <b>a1 is SP3's call, not SP2's</b>: SP3's definition declares the vector
    /// property as <c>ReadOnlyMemory&lt;float&gt;</c> rather than a <c>string</c> source, so
    /// <c>CollectionModel.EmbeddingGenerationRequired</c> is false and <c>ResolveVectorsAsync</c>
    /// takes its pre-computed path. Owning the call is what makes <c>EmbedCalls</c> countable, what
    /// makes <c>TokensEmbedded</c> meterable, and what separates 6206 from 6207 by call site rather
    /// than by guesswork.
    /// </summary>
    public async Task<WriteWindowResult> WriteWindowAsync(
        IReadOnlyList<IdentifiedChunk> window, DocumentFacts facts, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(window);
        ArgumentNullException.ThrowIfNull(facts);

        var texts = new string[window.Count];
        for (var i = 0; i < window.Count; i++)
        {
            texts[i] = window[i].Draft.EmbedText;
        }

        var embedCalls = 1;
        GeneratedEmbeddings<Embedding<float>> embeddings;
        try
        {
            // a1 — ONE call, strictly OUTSIDE any transaction.
            embeddings = await generator.GenerateAsync(texts, options: null, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception first) when (first is not OperationCanceledException)
        {
            // 6206: the embed threw. Halve the batch, log 918, retry the EMBED once. A second
            // failure suspends the run - it is never retried a third time and never silently
            // dropped.
            var half = Math.Max(1, window.Count / 2);
            IngestionLog.EmbedBatchShrunk(logger, window.Count, half, "embed:failed");

            try
            {
                // The retry is COUNTED, not assumed to be one call: a window of five halved to
                // three is ceil(5/3) = 2 calls, a window of three halved to one is 3, and
                // IngestionBudget.MaxTokens meters the same pass. Hard-coding 2 would under-report
                // EmbedCalls on every window whose size is not a power of two.
                var retry = await GenerateInHalvesAsync(texts, half, cancellationToken).ConfigureAwait(false);
                embedCalls += retry.Calls;
                embeddings = retry.Embeddings;
            }
            catch (Exception second) when (second is not OperationCanceledException)
            {
                throw new EdgeIngestionException(
                    EdgeErrorCode.IngestionEmbeddingFailed,
                    string.Format(
                        CultureInfo.InvariantCulture,
                        "Embedding {0} chunks of document '{1}' failed twice, the second time at a batch of {2}.",
                        window.Count,
                        facts.DocumentId,
                        half),
                    second)
                {
                    SourceId = facts.SourceId,
                    DocumentId = facts.DocumentId,
                    Remediation =
                        "The embedding generator is refusing work. Check the model is loaded and the device is not " +
                        "out of memory, then re-run - committed documents skip on the hash gate.",
                };
            }
        }

        if (embeddings.Count != window.Count)
        {
            throw new EdgeIngestionException(
                EdgeErrorCode.IngestionEmbeddingFailed,
                string.Format(
                    CultureInfo.InvariantCulture,
                    "The embedding generator returned {0} vectors for {1} inputs.",
                    embeddings.Count,
                    window.Count))
            {
                SourceId = facts.SourceId,
                DocumentId = facts.DocumentId,
                Remediation = "The generator must return one embedding per input, in order.",
            };
        }

        var usage = embeddings.Usage?.InputTokenCount;
        long tokens;
        bool usageFromGenerator;
        if (usage is { } published)
        {
            tokens = published;
            usageFromGenerator = true;
        }
        else
        {
            tokens = 0;
            for (var i = 0; i < window.Count; i++)
            {
                tokens += tokenizer.CountTokens(texts[i].AsSpan());
            }

            usageFromGenerator = false;
        }

        var records = new List<Dictionary<string, object?>>(window.Count);
        for (var i = 0; i < window.Count; i++)
        {
            records.Add(ToRecord(window[i], facts, embeddings[i].Vector));
        }

        try
        {
            // a2 — ONE call. It opens the only transaction in the pair.
            await collection.UpsertAsync(records, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            throw new EdgeIngestionException(
                EdgeErrorCode.IngestionWriteFailed,
                string.Format(
                    CultureInfo.InvariantCulture,
                    "Upserting {0} chunks of document '{1}' failed.",
                    window.Count,
                    facts.DocumentId),
                ex)
            {
                SourceId = facts.SourceId,
                DocumentId = facts.DocumentId,
                Remediation =
                    "The write, not the embed, failed - so it is not retried. Inspect the inner " +
                    "EdgeVectorStoreException for the SQLite fault.",
            };
        }

        return new WriteWindowResult(embedCalls, tokens, usageFromGenerator);
    }

    /// <summary>
    /// Spec 9.5 step b. Deletions run AFTER every addition has landed, so a crash mid-document never
    /// deletes content that has not yet been replaced.
    /// </summary>
    public async Task DeleteAsync(
        IReadOnlyList<string> keys, int batchSize, DocumentFacts facts, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(keys);
        ArgumentNullException.ThrowIfNull(facts);

        for (var offset = 0; offset < keys.Count; offset += batchSize)
        {
            var window = keys.Skip(offset).Take(batchSize).Cast<object>().ToList();
            try
            {
                await collection.DeleteAsync(window, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                throw new EdgeIngestionException(
                    EdgeErrorCode.IngestionWriteFailed,
                    string.Format(
                        CultureInfo.InvariantCulture,
                        "Deleting {0} chunks of document '{1}' failed.",
                        window.Count,
                        facts.DocumentId),
                    ex)
                {
                    SourceId = facts.SourceId,
                    DocumentId = facts.DocumentId,
                };
            }
        }
    }

    /// <summary>
    /// Spec 9.5 step c. SP3's own transaction, batched <c>UPDATE</c>s. It never recomputes a vector
    /// and never touches the <c>vec0</c> row, because <c>vec0</c> is keyed on <c>_rowid</c> and an
    /// <c>UPDATE</c> does not move it.
    /// <para>
    /// It DOES fire SP1's <c>"&lt;fts&gt;_au"</c> trigger, which is <c>AFTER UPDATE ON &lt;data&gt;</c>
    /// unqualified and performs an FTS5 delete-plus-insert per row — so inserting one paragraph at
    /// the top of a 500-chunk document is one embedding and 499 FTS5 delete/insert pairs. That is
    /// the honest number, and <c>IngestionOptions.RepairOrdinals</c> exists to turn it off.
    /// </para>
    /// </summary>
    public async Task RepairAsync(
        IReadOnlyList<IdentifiedChunk> repaired, DocumentFacts facts, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(repaired);
        ArgumentNullException.ThrowIfNull(facts);

        if (repaired.Count == 0)
        {
            return;
        }

        var sql = $"""
                   UPDATE "{dataTable}"
                   SET "{IngestionColumns.Ordinal}" = $o,
                       "{IngestionColumns.CharStart}" = $s,
                       "{IngestionColumns.CharEnd}" = $e
                   WHERE "{IngestionColumns.Key}" = $k
                   """;

        await database.ExecuteInTransactionAsync(
            async (connection, transaction, ct) =>
            {
                foreach (var chunk in repaired)
                {
                    var command = connection.CreateCommand();
                    await using (command.ConfigureAwait(false))
                    {
                        command.Transaction = transaction;
                        command.CommandText = sql;
                        command.Parameters.Add(new SqliteParameter("$o", chunk.Draft.Ordinal));
                        command.Parameters.Add(new SqliteParameter("$s", chunk.Draft.CharStart));
                        command.Parameters.Add(new SqliteParameter("$e", chunk.Draft.CharEnd));
                        command.Parameters.Add(new SqliteParameter("$k", chunk.Key));
                        await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
                    }
                }

                return true;
            },
            cancellationToken).ConfigureAwait(false);

        IngestionLog.OrdinalsRepaired(logger, facts.DocumentId, repaired.Count);
    }

    /// <summary>Deletes every chunk of one document, without reading its keys first.</summary>
    public Task<int> DeleteDocumentChunksAsync(
        string sourceId, string documentId, CancellationToken cancellationToken) =>
        database.ExecuteInTransactionAsync(
            async (connection, transaction, ct) =>
            {
                var command = connection.CreateCommand();
                await using (command.ConfigureAwait(false))
                {
                    command.Transaction = transaction;
                    command.CommandText =
                        $"""
                         DELETE FROM "{dataTable}"
                         WHERE "{IngestionColumns.SourceId}" = $s AND "{IngestionColumns.DocumentId}" = $d
                         """;
                    command.Parameters.Add(new SqliteParameter("$s", sourceId));
                    command.Parameters.Add(new SqliteParameter("$d", documentId));
                    return await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
                }
            },
            cancellationToken);

    private static Dictionary<string, object?> ToRecord(
        IdentifiedChunk chunk, DocumentFacts facts, ReadOnlyMemory<float> embedding)
    {
        var draft = chunk.Draft;
        var ingested = new IngestedChunk(
            chunk.Key,
            facts.SourceId,
            facts.DocumentId,
            draft.Ordinal,
            draft.Text,
            draft.Breadcrumb,
            draft.CharStart,
            draft.CharEnd,
            draft.TokenCount,
            draft.Page,
            draft.Kind.ToString(),
            facts.ExtractorId,
            facts.MediaType,
            chunk.ContentHash,
            facts.CreatedUtc);

        return new Dictionary<string, object?>(ingested.ToRecord(embedding), StringComparer.Ordinal);
    }

    private async Task<(GeneratedEmbeddings<Embedding<float>> Embeddings, int Calls)> GenerateInHalvesAsync(
        string[] texts, int half, CancellationToken cancellationToken)
    {
        var combined = new GeneratedEmbeddings<Embedding<float>>();
        long? inputTokens = null;
        var sawUsage = false;
        var calls = 0;

        for (var offset = 0; offset < texts.Length; offset += half)
        {
            var slice = texts.Skip(offset).Take(half).ToList();
            var part = await generator.GenerateAsync(slice, options: null, cancellationToken).ConfigureAwait(false);
            calls++;
            foreach (var embedding in part)
            {
                combined.Add(embedding);
            }

            if (part.Usage?.InputTokenCount is { } tokens)
            {
                sawUsage = true;
                inputTokens = (inputTokens ?? 0) + tokens;
            }
        }

        if (sawUsage)
        {
            combined.Usage = new UsageDetails { InputTokenCount = inputTokens };
        }

        return (combined, calls);
    }
}
