using System.Text.Json;
using System.Text.RegularExpressions;
using Xunit;

namespace Qavren.Edge.Ingestion.Tests.Tier3;

/// <summary>
/// A plain tier-1 test with no network and no skip: the real-world manifest parses and every entry
/// is well-formed, so a malformed manifest fails on the PR that introduced it rather than at 03:00
/// in a nightly job (plan adjustment 26).
/// </summary>
public sealed partial class RealWorldCorpusManifestTests
{
    [GeneratedRegex("^[0-9a-f]{64}$")]
    private static partial Regex Sha256Shape { get; }

    [Fact]
    public void The_manifest_parses_and_carries_a_documents_array()
    {
        using var json = JsonDocument.Parse(RealWorldCorpus.Text());

        Assert.Equal(JsonValueKind.Object, json.RootElement.ValueKind);
        Assert.True(json.RootElement.TryGetProperty("documents", out var documents), "no 'documents' property");
        Assert.Equal(JsonValueKind.Array, documents.ValueKind);
        Assert.True(json.RootElement.TryGetProperty("schema", out _), "no 'schema' property");
    }

    [Fact]
    public void Every_entry_has_all_five_fields()
    {
        foreach (var entry in RealWorldCorpus.Load())
        {
            Assert.False(string.IsNullOrWhiteSpace(entry.Id), "an entry has no 'id'");
            Assert.False(string.IsNullOrWhiteSpace(entry.Producer), $"'{entry.Id}' has no 'producer'");
            Assert.False(string.IsNullOrWhiteSpace(entry.Url), $"'{entry.Id}' has no 'url'");
            Assert.False(string.IsNullOrWhiteSpace(entry.Sha256), $"'{entry.Id}' has no 'sha256'");
            Assert.False(string.IsNullOrWhiteSpace(entry.Phrase), $"'{entry.Id}' has no 'phrase'");
        }
    }

    [Fact]
    public void Every_sha256_is_sixty_four_lower_case_hex()
    {
        foreach (var entry in RealWorldCorpus.Load())
        {
            Assert.True(
                entry.Sha256 is not null && Sha256Shape.IsMatch(entry.Sha256),
                $"'{entry.Id}': sha256 '{entry.Sha256}' is not 64 lower-case hex characters");
        }
    }

    [Fact]
    public void Every_producer_is_word_libreoffice_or_acrobat()
    {
        foreach (var entry in RealWorldCorpus.Load())
        {
            Assert.True(
                entry.Producer is not null && RealWorldCorpus.Producers.Contains(entry.Producer, StringComparer.Ordinal),
                $"'{entry.Id}': producer '{entry.Producer}' is not one of {string.Join(" / ", RealWorldCorpus.Producers)}");
        }
    }

    [Fact]
    public void Every_id_is_unique_and_every_url_is_absolute()
    {
        var entries = RealWorldCorpus.Load();

        var ids = entries.Select(e => e.Id).ToList();
        Assert.Equal(ids.Count, ids.Distinct(StringComparer.Ordinal).Count());

        foreach (var entry in entries)
        {
            Assert.True(
                Uri.TryCreate(entry.Url, UriKind.Absolute, out var uri) && uri.Scheme is "https" or "http",
                $"'{entry.Id}': url '{entry.Url}' is not an absolute http(s) URL");
        }
    }
}
