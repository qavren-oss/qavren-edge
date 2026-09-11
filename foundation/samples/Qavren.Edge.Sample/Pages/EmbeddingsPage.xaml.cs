using System.Diagnostics;
using Microsoft.Extensions.AI;
using Qavren.Edge.Embeddings.Onnx;
using Qavren.Edge.Onnx;

namespace Qavren.Edge.Sample.Pages;

/// <summary>
/// Spec 18: pick a preset, show determinate provisioning progress, embed a string, show
/// dimensions, elapsed time, the execution provider accepted and the ones skipped with their
/// reasons - the point being that <see cref="ExecutionProviderAttempt.Failure"/> becomes visible to
/// a human instead of living only in a log line.
/// </summary>
public partial class EmbeddingsPage : ContentPage
{
    private readonly IServiceProvider _services;
    private readonly IOnnxModelStore _modelStore;
    private readonly IOnnxSessionHost _sessionHost;

    public EmbeddingsPage(IServiceProvider services, IOnnxModelStore modelStore, IOnnxSessionHost sessionHost)
    {
        InitializeComponent();
        _services = services;
        _modelStore = modelStore;
        _sessionHost = sessionHost;

        PresetPicker.ItemsSource = EmbeddingPresets.All.Select(p => p.Id).ToArray();
        PresetPicker.SelectedIndex = 0;
    }

    /// <summary>
    /// Only <see cref="EmbeddingPresets.MiniLmL6V2Int8"/> is registered UNKEYED - that is the one
    /// <c>AddVectorStore</c> resolves for the Search page's notes (spec 4.3). The other three are
    /// registered under their own <see cref="EmbeddingPreset.Id"/> purely so this page can demo
    /// picking between presets without touching what the Search page embeds with.
    /// </summary>
    private IEmbeddingGenerator<string, Embedding<float>> ResolveGenerator(EmbeddingPreset preset) =>
        ReferenceEquals(preset, EmbeddingPresets.MiniLmL6V2Int8)
            ? _services.GetRequiredService<IEmbeddingGenerator<string, Embedding<float>>>()
            : _services.GetRequiredKeyedService<IEmbeddingGenerator<string, Embedding<float>>>(preset.Id);

    private async void OnEmbed(object? sender, EventArgs e)
    {
        if (PresetPicker.SelectedItem is not string presetId)
        {
            StatusLabel.Text = "Pick a preset first.";
            return;
        }

        var preset = EmbeddingPresets.ById(presetId);
        var text = string.IsNullOrWhiteSpace(InputEntry.Text) ? "a roof leak after the storm" : InputEntry.Text;

        ProvisionProgress.IsVisible = true;
        ProvisionProgress.Progress = 0;
        StatusLabel.Text = "Provisioning...";
        DimensionsLabel.Text = "-";
        ElapsedLabel.Text = "-";
        AcceptedLabel.Text = "-";
        SkippedList.ItemsSource = null;

        // A determinate progress bar needs BytesTotal, which only the model store's own EnsureAsync
        // reports (spec 18). IOnnxSessionHost.AcquireAsync - what GenerateAsync calls internally -
        // takes no IProgress<T>, so provisioning is done explicitly first; once provisioned it is a
        // cheap idempotent no-op the second time GenerateAsync triggers it.
        var progress = new Progress<ModelProvisioningProgress>(p =>
        {
            if (p.BytesTotal is { } total && total > 0)
            {
                ProvisionProgress.Progress = (double)p.BytesCompleted / total;
                StatusLabel.Text = $"Provisioning {p.RelativePath}: {p.BytesCompleted:N0} / {total:N0} bytes";
            }
            else
            {
                StatusLabel.Text = $"Provisioning {p.RelativePath}: {p.BytesCompleted:N0} bytes";
            }
        });

        try
        {
            await _modelStore.EnsureAsync(preset.Manifest, progress);

            var generator = ResolveGenerator(preset);
            var stopwatch = Stopwatch.StartNew();
            var result = await generator.GenerateAsync([text]);
            stopwatch.Stop();

            DimensionsLabel.Text = result[0].Vector.Length.ToString();
            ElapsedLabel.Text = $"{stopwatch.Elapsed.TotalMilliseconds:F1} ms";

            var info = _sessionHost.Describe(preset.Manifest.ModelId);
            if (info is not null)
            {
                AcceptedLabel.Text = info.ExecutionProviders.Accepted.ToString();
                SkippedList.ItemsSource = info.ExecutionProviders.Attempts
                    .Where(a => a.Provider != info.ExecutionProviders.Accepted)
                    .Select(a => a.Accepted
                        ? $"{a.Provider}: accepted but not chosen"
                        : $"{a.Provider}: {a.Failure ?? "rejected, no failure message"}")
                    .ToArray();

                if (SkippedList.ItemsSource is not IReadOnlyCollection<string> { Count: > 0 })
                {
                    SkippedList.ItemsSource = new[] { "(none - only one provider was tried on this platform)" };
                }
            }

            StatusLabel.Text = "Done.";
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            StatusLabel.Text = $"Failed: {ex.Message}";
        }
        finally
        {
            ProvisionProgress.IsVisible = false;
        }
    }
}
