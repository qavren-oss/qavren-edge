using Qavren.Edge.Onnx;

namespace Qavren.Edge.Embeddings.Onnx.Internal;

/// <summary>
/// Provisions the vocabulary, builds each preset's tokenizer once behind a
/// <c>SemaphoreSlim(1)</c>, and caches it for the process. Concurrent first callers share one build.
/// </summary>
/// <remarks>
/// <b>The cache is keyed by <see cref="EmbeddingPreset.Id"/>, not a single slot.</b> One provider
/// serves every registration in the process, and the keyed <c>AddOnnxEmbeddings(name, ...)</c>
/// overload exists precisely so two presets can coexist - a bge document generator beside a nomic
/// one, say. A single-slot cache would hand whichever preset embedded first to every later caller,
/// applying that preset's <c>MaxSequenceLength</c> and <c>LowerCase</c> to the other's text: no
/// exception, plausible and wrong vectors, which is the failure class every <c>required</c> field
/// on <see cref="EmbeddingPreset"/> exists to prevent.
/// </remarks>
internal sealed class EdgeTokenizerProvider : IEdgeTokenizerProvider, IDisposable
{
    private readonly IOnnxModelStore _store;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly Dictionary<string, IEdgeTokenizer> _byPresetId = new(StringComparer.Ordinal);
    private IEdgeTokenizer? _current;

    /// <summary>Creates the provider.</summary>
    /// <param name="store">The model store that puts the vocabulary on disk.</param>
    public EdgeTokenizerProvider(IOnnxModelStore store)
    {
        ArgumentNullException.ThrowIfNull(store);
        _store = store;
    }

    /// <summary>
    /// The most recently built tokenizer, or null before the first <see cref="GetAsync"/>. With one
    /// registration - which is every app that calls <c>AddOnnxEmbeddings()</c> once - this is that
    /// registration's tokenizer. Diagnostics prefer <see cref="Find"/>, which names the preset.
    /// </summary>
    public IEdgeTokenizer? Current => Volatile.Read(ref _current);

    /// <summary>The cached tokenizer for one preset, or null when it has not been built yet.</summary>
    /// <param name="presetId">The preset id to look up.</param>
    /// <returns>That preset's tokenizer, or null.</returns>
    /// <remarks>
    /// What the diagnostics block reads, so a report under a keyed registration names the vocabulary
    /// of the preset that registration actually runs rather than whichever one embedded first.
    /// </remarks>
    public IEdgeTokenizer? Find(string presetId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(presetId);

        lock (_byPresetId)
        {
            return _byPresetId.TryGetValue(presetId, out var tokenizer) ? tokenizer : null;
        }
    }

    /// <inheritdoc />
    public async ValueTask<IEdgeTokenizer> GetAsync(
        EmbeddingPreset preset,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(preset);

        if (Find(preset.Id) is { } cached)
        {
            return cached;
        }

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (Find(preset.Id) is { } raced)
            {
                return raced;
            }

            if (preset.TokenizerKind == EdgeTokenizerKind.UnigramTokenizerJson)
            {
                throw new EdgeEmbeddingException(
                    EdgeErrorCode.TokenizerKindUnsupported,
                    preset.Id,
                    "Unigram tokenizer.json assets need Microsoft.ML.Tokenizers 3.x, which this " +
                    "release does not pin. See ADR 0006 (defer multilingual to Tokenizers 3.x).");
            }

            var provisioned = await _store
                .EnsureAsync(preset.Manifest, progress: null, cancellationToken)
                .ConfigureAwait(false);

            if (!provisioned.Files.TryGetValue(preset.TokenizerFile, out var vocabPath))
            {
                throw new EdgeEmbeddingException(
                    EdgeErrorCode.TokenizerAssetMissing,
                    preset.Id,
                    $"The manifest for '{preset.Manifest.ModelId}' provisioned no file at " +
                    $"'{preset.TokenizerFile}'. Add it to the manifest with " +
                    $"{nameof(OnnxModelFileRole)}.{nameof(OnnxModelFileRole.Vocabulary)}.");
            }

            if (!File.Exists(vocabPath))
            {
                throw new EdgeEmbeddingException(
                    EdgeErrorCode.TokenizerAssetMissing,
                    preset.Id,
                    $"The vocabulary for '{preset.Manifest.ModelId}' is not on disk at '{vocabPath}'.");
            }

            var tokenizer = await EdgeTokenizer
                .CreateWordPieceAsync(
                    vocabPath,
                    new WordPieceTokenizerOptions
                    {
                        LowerCase = preset.LowerCase,
                        MaxSequenceLength = preset.MaxSequenceLength,
                    },
                    cancellationToken)
                .ConfigureAwait(false);

            lock (_byPresetId)
            {
                _byPresetId[preset.Id] = tokenizer;
            }

            Volatile.Write(ref _current, tokenizer);
            return tokenizer;
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <inheritdoc />
    public void Dispose()
    {
        Volatile.Write(ref _current, null);

        IEdgeTokenizer[] built;
        lock (_byPresetId)
        {
            built = [.. _byPresetId.Values];
            _byPresetId.Clear();
        }

        foreach (var tokenizer in built)
        {
            tokenizer.Dispose();
        }

        _gate.Dispose();
    }
}
