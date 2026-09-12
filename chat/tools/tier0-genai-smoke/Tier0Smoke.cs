using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using Microsoft.Extensions.AI;
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntimeGenAI;
using Qavren.Edge.Chat.Tests.Fixtures;
#if ANDROID
using Android.OS;
#endif

namespace Qavren.Edge.Tier0Smoke;

/// <summary>
/// Spec 16.0 / spec 19 item 1 - the five-target link-and-generate smoke. THROWAWAY: deleted in
/// the closing PR once tier 0 has answered its three go/no-go questions. One shared body, linked
/// (never copied) into both the console leg (win-x64, linux-x64) and the MAUI device head
/// (android, ios, maccatalyst, windows), so the two legs cannot drift into proving different
/// things. References nothing from Qavren.Edge - only the ORT GenAI package this task pins and
/// the committed tier-2 fixture from Task 1.3.
/// </summary>
public static class Tier0Smoke
{
    /// <summary>
    /// Loads the tiny fixture model, generates exactly one token, prints the go/no-go facts as
    /// one <c>key=value</c> line each, and returns 0. Any throw is caught, printed as
    /// <c>TIER0 FAIL: &lt;type&gt;: &lt;message&gt;</c>, and turned into exit code 1 - this is a
    /// go/no-go, not something that should ever print a stack trace to a CI job's summary.
    /// </summary>
    public static async Task<int> RunAsync(TextWriter output)
    {
        string? modelDirectory = null;
        OgaHandle? handle = null;
        Config? config = null;
        Model? model = null;
        Tokenizer? tokenizer = null;
        GeneratorParams? generatorParams = null;
        Generator? generator = null;
        TokenizerStream? tokenizerStream = null;
        try
        {
            // 1. Materialise the same base64 the tier-2 fixture uses, into a scratch directory -
            // Model(string) and Config(string) both need a real directory on disk, not a stream.
            modelDirectory = TinyChatModel.Materialise(
                Path.Combine(Path.GetTempPath(), "qedge-tier0-" + Guid.NewGuid().ToString("N")));

            // 2. The GenAI native environment, and telemetry off before anything else runs.
            handle = new OgaHandle();
            Utils.DisableTelemetryEvents();

            // 3. Config -> Model -> Tokenizer, timed: this is the load half of "link-and-generate".
            var loadStopwatch = Stopwatch.StartNew();
            config = new Config(modelDirectory);
            model = new Model(config);
            tokenizer = new Tokenizer(model);
            loadStopwatch.Stop();

            // 4. A THREE-token prompt, one generated token, decoded through a TokenizerStream.
            // "Hi!" is exactly three tokens under the fixture's tokenizer and is not a guess: the
            // fixture's tokenizer.json is a ByteLevel BPE with "merges": [] and its
            // tokenizer_config.json sets no add_bos_token, so every byte is its own token -
            // 'H'=72, 'i'=105, '!'=33. Measured on win-x64 before this comment was written.
            var promptSequences = tokenizer.Encode("Hi!");
            generatorParams = new GeneratorParams(model);
            generatorParams.SetSearchOption("max_length", 8.0);
            generator = new Generator(model, generatorParams);
            generator.AppendTokenSequences(promptSequences);
            generator.GenerateNextToken();
            var firstTokenId = generator.GetNextTokens()[0];
            tokenizerStream = tokenizer.CreateStream();
            var firstTokenText = tokenizerStream.Decode(firstTokenId);

            // 5. The go/no-go facts, one key=value per line.
            var genAiAssembly = typeof(OgaHandle).Assembly;
            var meaiAbstractionsAssembly = typeof(IChatClient).Assembly;

            await output.WriteLineAsync($"rid={RuntimeInformation.RuntimeIdentifier}").ConfigureAwait(false);
#if ANDROID
            var abi = Build.SupportedAbis is { Count: > 0 } supportedAbis ? supportedAbis[0] : "unknown";
            await output.WriteLineAsync($"abi={abi}").ConfigureAwait(false);
#endif
            await output.WriteLineAsync($"managedAssemblyMvid={genAiAssembly.ManifestModule.ModuleVersionId}").ConfigureAwait(false);
            await output.WriteLineAsync($"meaiAbstractionsVersion={meaiAbstractionsAssembly.GetName().Version}").ConfigureAwait(false);
            // Spec 16.0's go/no-go (c): the Managed assembly compiled against MEAI Abstractions
            // 9.8.0 binding against this repo's 10.10.0, asserted on every target rather than on
            // Windows alone - a TypeLoadException is a per-runtime fact.
            await output.WriteLineAsync(
                $"ichatClientAssignable={typeof(IChatClient).IsAssignableFrom(typeof(OnnxRuntimeGenAIChatClient))}").ConfigureAwait(false);
            await output.WriteLineAsync($"ortEnvIsCreatedAfterGenAi={OrtEnv.IsCreated}").ConfigureAwait(false);
            await output.WriteLineAsync(
                $"modelLoadMs={loadStopwatch.Elapsed.TotalMilliseconds.ToString("F1", CultureInfo.InvariantCulture)}").ConfigureAwait(false);
            await output.WriteLineAsync($"firstTokenId={firstTokenId}").ConfigureAwait(false);
            await output.WriteLineAsync($"firstTokenText={firstTokenText}").ConfigureAwait(false);

            return 0;
        }
        catch (Exception ex)
        {
            await output.WriteLineAsync($"TIER0 FAIL: {ex.GetType()}: {ex.Message}").ConfigureAwait(false);
            return 1;
        }
        finally
        {
            // 6. Dispose everything in reverse order.
            tokenizerStream?.Dispose();
            generator?.Dispose();
            generatorParams?.Dispose();
            tokenizer?.Dispose();
            model?.Dispose();
            config?.Dispose();
            handle?.Dispose();
            if (modelDirectory is not null)
            {
                try
                {
                    Directory.Delete(modelDirectory, recursive: true);
                }
                catch (IOException)
                {
                    // Best-effort scratch cleanup; a locked file here is not a tier-0 finding.
                }
                catch (UnauthorizedAccessException)
                {
                    // Same as above.
                }
            }
        }
    }
}
