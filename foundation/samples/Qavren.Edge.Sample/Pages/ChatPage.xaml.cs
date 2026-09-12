using System.Text;
using Microsoft.Extensions.AI;
using Qavren.Edge.Chat;
using Qavren.Edge.Onnx;

namespace Qavren.Edge.Sample.Pages;

/// <summary>
/// SP4 spec 18's Chat page. The consent sheet first - model name, size, licence, free disk, all
/// from <see cref="IChatModelProvisioner.Plan"/>, and a download button wired to
/// <see cref="IProgress{T}"/> of <see cref="ModelProvisioningProgress"/> - then a streaming
/// conversation that prints tokens/sec, time-to-first-token, the resolved context and the stop
/// reason for EVERY turn, including the ugly ones: a <see cref="EdgeChatStopReason.Suspended"/>
/// turn after backgrounding the app, and a <see cref="EdgeErrorCode.ChatInsufficientMemory"/>
/// refusal with its full explanation rendered rather than swallowed.
/// </summary>
/// <remarks>
/// The registered <see cref="IChatClient"/> is the RAG pipeline (<c>UseRag()</c> over the Search
/// page's notes), which is what the Ask page wants. This page talks to the model alone, so it
/// unwraps the leaf with <c>GetService&lt;EdgeChatClient&gt;()</c> - MEAI's pipeline contract is
/// exactly that <c>GetService</c> reaches through every delegating layer to the leaf.
/// </remarks>
public partial class ChatPage : ContentPage
{
    private readonly IChatModelProvisioner _provisioner;
    private readonly IChatClient _chat;
    private readonly List<ChatMessage> _history = [];

    private Uri? _licenseUri;
    private string? _conversationId;
    private bool _turnInFlight;

    // The turn's CancellationTokenSource is a local in OnSend (owned and disposed there); the page
    // keeps only the cancel delegate, so it owns no disposable of its own.
    private Action? _cancelTurn;

    public ChatPage(IChatModelProvisioner provisioner, IChatClient chat)
    {
        InitializeComponent();
        _provisioner = provisioner;

        // The leaf, not the RAG wrapper: a general conversation must not short-circuit on "no
        // notes matched" (RagOptions.ShortCircuitOnNoContext), which is the right behaviour for
        // the Ask page and the wrong one here.
        _chat = chat.GetService<EdgeChatClient>() ?? chat;

        TightDeviceSwitch.IsToggled =
            Preferences.Default.Get(MauiProgram.SimulateTightDevicePreferenceKey, false);

        RefreshPlan();
    }

    private void RefreshPlan()
    {
        var plan = _provisioner.Plan();

        ModelLabel.Text = $"{plan.DisplayName} ({plan.PresetId})";
        SizeLabel.Text = plan.Provisioned
            ? $"On disk - {plan.TotalBytes:N0} bytes, nothing to transfer."
            : $"{plan.BytesToTransfer:N0} of {plan.TotalBytes:N0} bytes to download"
              + (plan.BytesAlreadyPresent > 0 ? $" ({plan.BytesAlreadyPresent:N0} already present, resumable)" : "")
              + (plan.HuggingFaceRepo is { } repo ? $" from {repo}" : "")
              + ".";
        LicenseLabel.Text = plan.LicenseUri is { } uri ? $"{plan.SpdxLicense} - {uri}" : plan.SpdxLicense;
        _licenseUri = plan.LicenseUri;
        DiskLabel.Text = plan.FreeDiskBytes is { } free
            ? $"{free:N0} bytes free - {(plan.FitsOnDisk ? "fits" : "DOES NOT FIT")}"
            : "unknown (the platform would not say; the transfer is allowed, not refused)";
        DirectoryLabel.Text = plan.Directory;

        DownloadButton.IsEnabled = !plan.Provisioned;
        RemoveButton.IsEnabled = plan.Provisioned;
        ProvisionStatusLabel.Text = plan.Provisioned ? "Provisioned." : "Not provisioned - press Download.";
    }

    private async void OnLicenseTapped(object? sender, TappedEventArgs e)
    {
        if (_licenseUri is { } uri)
        {
            await Launcher.Default.OpenAsync(uri);
        }
    }

    private async void OnDownload(object? sender, EventArgs e)
    {
        DownloadButton.IsEnabled = false;
        ProvisionProgress.IsVisible = true;
        ProvisionProgress.Progress = 0;
        ProvisionStatusLabel.Text = "Starting...";

        var progress = new Progress<ModelProvisioningProgress>(p =>
        {
            var resumed = p.Resumed ? " (resumed)" : "";
            if (p.BytesTotal is { } total && total > 0)
            {
                ProvisionProgress.Progress = (double)p.BytesCompleted / total;
                ProvisionStatusLabel.Text = $"{p.RelativePath}{resumed}: {p.BytesCompleted:N0} / {total:N0} bytes";
            }
            else
            {
                ProvisionStatusLabel.Text = $"{p.RelativePath}{resumed}: {p.BytesCompleted:N0} bytes";
            }
        });

        try
        {
            var provisioned = await _provisioner.ProvisionAsync(progress);
            ProvisionStatusLabel.Text =
                $"Provisioned from {provisioned.Source} in {provisioned.Duration.TotalSeconds:F1} s.";
        }
        catch (EdgeException ex)
        {
            // ChatTransferNotPermitted off Wi-Fi, ChatInsufficientDisk, a digest mismatch: the
            // library's message says which, and the remediation says what to do about it.
            ProvisionStatusLabel.Text = Describe(ex);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            ProvisionStatusLabel.Text = $"Failed: {ex.GetType().Name}: {ex.Message}";
        }
        finally
        {
            ProvisionProgress.IsVisible = false;
            RefreshPlan();
        }
    }

    private async void OnRemove(object? sender, EventArgs e)
    {
        RemoveButton.IsEnabled = false;
        try
        {
            await _provisioner.RemoveAsync();
            _history.Clear();
            _conversationId = null;
            Transcript.Clear();
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            ProvisionStatusLabel.Text = $"Remove failed: {ex.Message}";
        }
        finally
        {
            RefreshPlan();
        }
    }

    private void OnTightDeviceToggled(object? sender, ToggledEventArgs e)
    {
        Preferences.Default.Set(MauiProgram.SimulateTightDevicePreferenceKey, e.Value);
        ProvisionStatusLabel.Text = e.Value
            ? "Tight device armed: restart the app, then Send. The gate reads its options once, at registration."
            : "Tight device disarmed: restart the app to load with the real memory reading.";
    }

    private void OnCancel(object? sender, EventArgs e) => _cancelTurn?.Invoke();

    private async void OnSend(object? sender, EventArgs e)
    {
        if (_turnInFlight)
        {
            return; // one turn at a time in the UI; the client itself queues (MaxQueuedTurns).
        }

        var prompt = PromptEntry.Text?.Trim();
        if (string.IsNullOrEmpty(prompt))
        {
            return;
        }

        PromptEntry.Text = "";
        SendButton.IsEnabled = false;
        CancelButton.IsEnabled = true;
        _turnInFlight = true;
        using var turnCts = new CancellationTokenSource();
        _cancelTurn = turnCts.Cancel;

        AddBubble("You", prompt, stats: null);
        var (answerLabel, statsLabel) = AddBubble("Model", "", stats: "generating...");

        _history.Add(new ChatMessage(ChatRole.User, prompt));

        var text = new StringBuilder();
        ChatTurnStatus? status = null;

        try
        {
            // Send the whole history every time and hand back the previous turn's conversation
            // id: that pair is what lets EdgeChatClient reuse its cached generator and append
            // only the new prompt tokens (PromptTokensAppended below says how many).
            var options = new ChatOptions { ConversationId = _conversationId };

            await foreach (var update in _chat.GetStreamingResponseAsync(_history, options, turnCts.Token))
            {
                text.Append(update.Text);
                answerLabel.Text = text.ToString();

                // The status rides on the final update; reading every update and keeping the
                // last non-null costs nothing and survives the library moving it.
                status = update.GetTurnStatus() ?? status;
                _conversationId = update.ConversationId ?? _conversationId;
            }

            statsLabel.Text = status is null
                ? "finished (no turn status was published)"
                : FormatStatus(status);

            if (text.Length > 0)
            {
                _history.Add(new ChatMessage(ChatRole.Assistant, text.ToString()));
            }
        }
        catch (OperationCanceledException)
        {
            statsLabel.Text = status is null ? "stop: Cancelled (before the first token)" : FormatStatus(status);
        }
        catch (EdgeException ex)
        {
            // The refusal, in full. For ChatInsufficientMemory the Message IS the gate's
            // explanation - every term it read and which one bound - and Remediation names the
            // lever (a smaller preset, MaxContextTokens, the iOS entitlements).
            _history.RemoveAt(_history.Count - 1);
            answerLabel.Text = Describe(ex);
            statsLabel.Text = $"refused: {ex.Code} ({(int)ex.Code})";
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            _history.RemoveAt(_history.Count - 1);
            answerLabel.Text = $"{ex.GetType().Name}: {ex.Message}";
            statsLabel.Text = "failed";
        }
        finally
        {
            _cancelTurn = null;
            _turnInFlight = false;
            SendButton.IsEnabled = true;
            CancelButton.IsEnabled = false;
            await TranscriptScroll.ScrollToAsync(0, double.MaxValue, animated: true);
        }
    }

    private static string FormatStatus(ChatTurnStatus s)
    {
        var line =
            $"stop: {s.StopReason} | {s.TokensPerSecond:F1} tok/s | first token {s.TimeToFirstToken.TotalMilliseconds:F0} ms | " +
            $"{s.Duration.TotalSeconds:F1} s | context {s.ContextTokens} | prompt {s.PromptTokens} (+{s.PromptTokensAppended} appended) | " +
            $"generated {s.GeneratedTokens}";

        if (s.MessagesDropped > 0)
        {
            line += $" | {s.MessagesDropped} history message(s) dropped";
        }

        if (s.ThermalThrottled)
        {
            line += $" | thermal {s.Thermal} (throttled)";
        }

        return line;
    }

    /// <summary>Everything the exception carries, so nothing the library explained is lost.</summary>
    private static string Describe(EdgeException ex)
    {
        var sb = new StringBuilder();
        sb.Append(ex.Code).Append(" (").Append((int)ex.Code).Append("): ").Append(ex.Message);

        if (ex is EdgeChatException chat)
        {
            void Line(string name, object? value)
            {
                if (value is not null)
                {
                    sb.Append('\n').Append(name).Append(": ").Append(value is long l ? $"{l:N0}" : value);
                }
            }

            Line("required bytes", chat.RequiredBytes);
            Line("available bytes", chat.AvailableBytes);
            Line("total memory bytes", chat.TotalMemoryBytes);
            Line("budget kind", chat.BudgetKind);
            Line("requested context", chat.RequestedContextTokens);
            Line("fitting context", chat.FittingContextTokens);
            Line("prompt tokens", chat.PromptTokens);
            Line("thermal", chat.Thermal);
            Line("runtime identifier", chat.RuntimeIdentifier);

            if (chat.Remediation is { } remediation)
            {
                sb.Append("\n\nRemediation: ").Append(remediation);
            }
        }

        return sb.ToString();
    }

    private (Label Text, Label Stats) AddBubble(string who, string text, string? stats)
    {
        var textLabel = new Label { Text = text, LineBreakMode = LineBreakMode.WordWrap };
        var statsLabel = new Label
        {
            Text = stats ?? "",
            FontSize = 11,
            IsVisible = stats is not null,
            LineBreakMode = LineBreakMode.WordWrap,
        };

        Transcript.Add(new Border
        {
            Padding = new Thickness(10),
            StrokeThickness = 1,
            Stroke = string.Equals(who, "You", StringComparison.Ordinal)
                ? Application.Current?.RequestedTheme == AppTheme.Dark ? Color.FromArgb("#444") : Color.FromArgb("#ccc")
                : Color.FromArgb("#512BD4"),
            Content = new VerticalStackLayout
            {
                Spacing = 4,
                Children =
                {
                    new Label { Text = who, FontAttributes = FontAttributes.Bold, FontSize = 12 },
                    textLabel,
                    statsLabel,
                },
            },
        });

        return (textLabel, statsLabel);
    }
}
