using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Qavren.Edge.Chat.Internal;
using Qavren.Edge.Diagnostics;
using Qavren.Edge.Hosting;
using Qavren.Edge.Lifecycle;
using Qavren.Edge.Onnx;

namespace Qavren.Edge.Chat;

/// <summary>Registers on-device chat on the Qavren.Edge builder.</summary>
public static class ChatOnnxEdgeBuilderExtensions
{
    /// <summary>
    /// Calls <c>AddOnnx()</c> (so sub-project 2's order-200 <c>OrtEnv</c> task, model paths,
    /// resource monitor and model store are present and correctly ordered), registers the device
    /// profile, the model host, the provisioner, the order-400 startup task, the lifecycle observer
    /// and the diagnostics contributor, then delegates to <c>services.AddChatClient</c>.
    /// <para>
    /// Registration goes through MEAI's own <c>AddChatClient</c> and not a hand-rolled
    /// <c>IChatClient</c> descriptor, because that is the descriptor Agent Framework and Semantic
    /// Kernel resolve, and because its factory is <c>ChatClientBuilder.Build</c> - so the pipeline
    /// is constructed lazily from the app's provider and the order of builder calls does not
    /// matter. <c>AddChatClient</c> and <c>AddKeyedChatClient</c> live in the
    /// <c>Microsoft.Extensions.DependencyInjection</c> namespace, not <c>Microsoft.Extensions.AI</c>
    /// (plan adjustment 10).
    /// </para>
    /// Returns <see cref="EdgeBuilder"/> rather than <c>ChatClientBuilder</c>, matching
    /// <c>AddOnnxEmbeddings</c> and the rest of the suite's chaining idiom; the MEAI pipeline is the
    /// optional <paramref name="pipeline"/> callback. <b>Remember MEAI's ordering contract</b>:
    /// <c>ChatClientBuilder.Build</c> applies factories in REVERSE, so the FIRST <c>Use()</c> call
    /// is the OUTERMOST client.
    /// </summary>
    /// <param name="builder">The Qavren.Edge builder.</param>
    /// <param name="preset">Required. There is no default - see <see cref="ChatPresets"/>.</param>
    /// <param name="configure">Configures this registration's <see cref="EdgeChatOptions"/>.</param>
    /// <param name="pipeline">The MEAI pipeline over the client.</param>
    /// <returns>The same builder.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="builder"/> or <paramref name="preset"/> is null.</exception>
    public static EdgeBuilder AddOnnxChat(
        this EdgeBuilder builder,
        ChatPreset preset,
        Action<EdgeChatOptions>? configure = null,
        Action<ChatClientBuilder>? pipeline = null)
        => AddOnnxChatCore(builder, name: null, preset, configure, pipeline);

    /// <summary>
    /// Keyed registration, mirroring sub-project 1's named databases and sub-project 2's named
    /// generators. Uses <c>AddKeyedChatClient</c>.
    /// </summary>
    /// <param name="builder">The Qavren.Edge builder.</param>
    /// <param name="name">The service key.</param>
    /// <param name="preset">Required. There is no default.</param>
    /// <param name="configure">Configures this registration's <see cref="EdgeChatOptions"/>.</param>
    /// <param name="pipeline">The MEAI pipeline over the client.</param>
    /// <returns>The same builder.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="builder"/> or <paramref name="preset"/> is null.</exception>
    /// <exception cref="ArgumentException"><paramref name="name"/> is null or whitespace.</exception>
    public static EdgeBuilder AddOnnxChat(
        this EdgeBuilder builder,
        string name,
        ChatPreset preset,
        Action<EdgeChatOptions>? configure = null,
        Action<ChatClientBuilder>? pipeline = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        return AddOnnxChatCore(builder, name, preset, configure, pipeline);
    }

    /// <summary>
    /// Order 410. Faults startup with <see cref="EdgeErrorCode.ChatModelNotProvisioned"/> when the
    /// model is absent. It never downloads: blocking <c>IEdgeHost.Started</c> on a 1.241 GB transfer
    /// also blocks every <c>IEdgeDatabase.OpenConnectionAsync</c>.
    /// </summary>
    /// <param name="builder">The Qavren.Edge builder.</param>
    /// <param name="name">The registration's service key, or null for the unkeyed one.</param>
    /// <returns>The same builder.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="builder"/> is null.</exception>
    public static EdgeBuilder RequireChatModelAtStartup(this EdgeBuilder builder, string? name = null)
    {
        ArgumentNullException.ThrowIfNull(builder);

        if (ChatRegistrationMarker.TryAdd(builder.Services, "require", name))
        {
            builder.Services.AddSingleton<IEdgeStartupTask>(
                sp => new ChatProvisioningStartupTask(sp, name));
        }

        return builder;
    }

    /// <summary>
    /// Order 420. Loads the model through <c>IChatModelHost.PreloadAsync</c>, probes the chat
    /// template, and probes guidance when the policy is not
    /// <see cref="EdgeGuidancePolicy.Disabled"/>. Off by default: it forces provisioning. It runs no
    /// generation - one token on a 1B model is 30 ms of decode on top of a 1.6 s load, and nothing
    /// about the decode loop is proven by doing it twice.
    /// <para>
    /// Idempotent per registration, made so by scanning the <see cref="IServiceCollection"/> for a
    /// marker record - <b>not</b> <c>TryAddEnumerable</c>, which on an enumerable service like
    /// <see cref="IEdgeStartupTask"/> would suppress every <i>other</i> package's task rather than a
    /// duplicate of this one. Sub-project 2's <c>WarmUpSessionAtStartup</c> registers with a bare
    /// <c>AddSingleton</c> and gets duplicate order-220 tasks when called twice; sub-project 4 does
    /// not repeat that.
    /// </para>
    /// </summary>
    /// <param name="builder">The Qavren.Edge builder.</param>
    /// <param name="name">The registration's service key, or null for the unkeyed one.</param>
    /// <returns>The same builder.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="builder"/> is null.</exception>
    public static EdgeBuilder WarmUpChatAtStartup(this EdgeBuilder builder, string? name = null)
    {
        ArgumentNullException.ThrowIfNull(builder);

        if (ChatRegistrationMarker.TryAdd(builder.Services, "warmup", name))
        {
            builder.Services.AddSingleton<IEdgeStartupTask>(sp => new ChatWarmUpStartupTask(sp, name));
        }

        return builder;
    }

    private static EdgeBuilder AddOnnxChatCore(
        EdgeBuilder builder,
        string? name,
        ChatPreset preset,
        Action<EdgeChatOptions>? configure,
        Action<ChatClientBuilder>? pipeline)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(preset);

        // Idempotent, and it is what puts the order-200 OrtEnv task in front of our order-400 one.
        builder.AddOnnx();

        AddProcessWideServices(builder.Services);

        if (!ChatRegistrationMarker.TryAdd(builder.Services, "chat", name))
        {
            // Calling AddOnnxChat twice for one key registers one of everything, exactly as
            // AddOnnx does.
            return builder;
        }

        var options = new EdgeChatOptions { Preset = preset };
        configure?.Invoke(options);

        // The callback may READ Preset; replacing it is unsupported, so it is put back.
        options.Preset = preset;

        var registration = new ChatRegistration(name, preset, options);
        var services = builder.Services;

        if (name is null)
        {
            services.AddSingleton(sp => CreateProvisioner(sp, registration));
            services.AddSingleton<IChatModelProvisioner>(sp => sp.GetRequiredService<ChatModelProvisioner>());
            services.AddSingleton(sp => CreateHost(sp, registration));
            services.AddSingleton<IChatModelHost>(sp => sp.GetRequiredService<ChatModelHost>());
            services.AddSingleton<IChatLifecycleTarget>(sp => sp.GetRequiredService<ChatModelHost>());
        }
        else
        {
            services.AddKeyedSingleton(name, (sp, _) => CreateProvisioner(sp, registration));
            services.AddKeyedSingleton<IChatModelProvisioner>(
                name, (sp, key) => sp.GetRequiredKeyedService<ChatModelProvisioner>(key));
            services.AddKeyedSingleton(name, (sp, _) => CreateHost(sp, registration));
            services.AddKeyedSingleton<IChatModelHost>(
                name, (sp, key) => sp.GetRequiredKeyedService<ChatModelHost>(key));
            services.AddKeyedSingleton<IChatLifecycleTarget>(
                name, (sp, key) => sp.GetRequiredKeyedService<ChatModelHost>(key));
        }

        // One observer and one contributor per registration, each closing over its own options.
        // Not TryAddEnumerable: a second keyed registration is a second observer, not a duplicate.
        services.AddSingleton<IEdgeLifecycleObserver>(sp => new ChatLifecycleObserver(
            sp,
            sp.GetRequiredService<ChatEnvironmentState>(),
            sp.GetRequiredService<IOptions<EdgeGenAiOptions>>(),
            registration,
            Logger<ChatLifecycleObserver>(sp)));

        services.AddSingleton<IEdgeDiagnosticsContributor>(sp => new ChatDiagnosticsContributor(
            sp,
            registration,
            sp.GetRequiredService<ChatEnvironmentState>(),
            sp.GetRequiredService<IEdgeResourceMonitor>()));

        var chatBuilder = name is null
            ? services.AddChatClient(sp => CreateChatClient(sp, registration))
            : services.AddKeyedChatClient(name, sp => CreateChatClient(sp, registration));

        pipeline?.Invoke(chatBuilder);

        return builder;
    }

    private static void AddProcessWideServices(IServiceCollection services)
    {
        services.AddOptions<EdgeGenAiOptions>();
        services.TryAddSingleton<ChatEnvironmentState>();

#if ANDROID
        services.TryAddSingleton<IEdgeChatDeviceProfileProvider, AndroidChatDeviceProfileProvider>();
#elif IOS || MACCATALYST
        services.TryAddSingleton<IEdgeChatDeviceProfileProvider, AppleChatDeviceProfileProvider>();
#else
        services.TryAddSingleton<IEdgeChatDeviceProfileProvider, PortableChatDeviceProfileProvider>();
#endif

        // A marker rather than TryAddEnumerable, because the descriptor carries a factory and
        // TryAddEnumerable cannot dedupe one of those.
        if (ChatRegistrationMarker.TryAdd(services, "environment", null))
        {
            services.AddSingleton<IEdgeStartupTask>(sp => new ChatEnvironmentStartupTask(
                sp.GetRequiredService<IEdgeChatDeviceProfileProvider>(),
                sp.GetRequiredService<ChatEnvironmentState>(),
                sp.GetRequiredService<IOptions<EdgeGenAiOptions>>(),
                Logger<ChatEnvironmentStartupTask>(sp)));
        }
    }

    private static ChatModelProvisioner CreateProvisioner(IServiceProvider sp, ChatRegistration registration) =>
        new(
            registration.Preset,
            registration.Options,
            sp.GetRequiredService<IOnnxModelStore>(),
            sp.GetRequiredService<IEdgeModelPaths>(),
            Logger<ChatModelProvisioner>(sp));

    private static ChatModelHost CreateHost(IServiceProvider sp, ChatRegistration registration) =>
        new(
            registration,
            sp.GetRequiredService<IEdgeHost>(),
            registration.Name is null
                ? sp.GetRequiredService<ChatModelProvisioner>()
                : sp.GetRequiredKeyedService<ChatModelProvisioner>(registration.Name),
            sp.GetRequiredService<IEdgeResourceMonitor>(),
            sp.GetRequiredService<ChatEnvironmentState>(),
            Logger<ChatModelHost>(sp));

    /// <summary>
    /// The leaf <c>IChatClient</c> for one registration.
    /// </summary>
    /// <remarks>
    /// <b>Wave 4 registers a client that resolves its model lazily and refuses to decode.</b> The
    /// decode loop, the conversation cache and the statistics are Task 5.1's, and this factory is
    /// the one line that changes when <c>EdgeChatClient</c> lands. What it already provides is the
    /// half spec section 8.1 cares about: resolving <c>IChatClient</c> constructs <b>nothing</b>
    /// native, so <c>OrtEnv.IsCreated</c> is still false after composition, and the first call
    /// raises <see cref="EdgeErrorCode.ChatEnvironmentNotStarted"/> when the order-400 task has not
    /// run.
    /// </remarks>
    private static PendingEdgeChatClient CreateChatClient(IServiceProvider sp, ChatRegistration registration) =>
        new PendingEdgeChatClient(
            registration,
            registration.Name is null
                ? sp.GetRequiredService<ChatModelHost>()
                : sp.GetRequiredKeyedService<ChatModelHost>(registration.Name));

    /// <summary>
    /// Resolves a logger without requiring logging to be registered at all. A bare
    /// <see cref="ServiceCollection"/> is a container this suite's decisions say every package must
    /// run on, and <c>GetRequiredService&lt;ILogger&lt;T&gt;&gt;()</c> would not.
    /// </summary>
    private static ILogger<T> Logger<T>(IServiceProvider sp) =>
        sp.GetService<ILoggerFactory>()?.CreateLogger<T>() ?? NullLogger<T>.Instance;
}

/// <summary>
/// The idempotency marker <c>AddOnnxChat</c>, <c>RequireChatModelAtStartup</c> and
/// <c>WarmUpChatAtStartup</c> scan for.
/// </summary>
/// <param name="Kind">Which registration this marks.</param>
/// <param name="Name">The service key it belongs to, or null for the unkeyed registration.</param>
/// <remarks>
/// A scan rather than <c>TryAddEnumerable</c>, and the difference is not stylistic: on an
/// enumerable service like <see cref="IEdgeStartupTask"/>, <c>TryAddEnumerable</c> dedupes by
/// implementation <i>type</i>, so it would suppress every <b>other</b> package's task of the same
/// type rather than a duplicate of this one - and it refuses a factory descriptor outright, which
/// is what every registration here is.
/// </remarks>
internal sealed record ChatRegistrationMarker(string Kind, string? Name)
{
    /// <summary>Adds the marker unless it is already there.</summary>
    /// <param name="services">The collection being composed.</param>
    /// <param name="kind">Which registration is being marked.</param>
    /// <param name="name">The service key, or null.</param>
    /// <returns><see langword="true"/> when this call is the first one.</returns>
    public static bool TryAdd(IServiceCollection services, string kind, string? name)
    {
        ArgumentNullException.ThrowIfNull(services);

        var marker = new ChatRegistrationMarker(kind, name);

        foreach (var descriptor in services)
        {
            if (descriptor.ServiceType == typeof(ChatRegistrationMarker)
                && descriptor.ImplementationInstance is ChatRegistrationMarker existing
                && existing == marker)
            {
                return false;
            }
        }

        services.AddSingleton(marker);
        return true;
    }
}

/// <summary>
/// The leaf <c>IChatClient</c> wave 4 registers: it resolves its model lazily and refuses to
/// decode.
/// </summary>
/// <remarks>
/// <b>This type is transitional and Task 5.1 deletes it.</b> The decode loop, the conversation
/// cache, the stop-sequence matcher and the statistics are that task's, and
/// <c>ChatOnnxEdgeBuilderExtensions.CreateChatClient</c> is the one line that changes when
/// <c>EdgeChatClient</c> lands.
/// <para>
/// What it already provides is the half spec section 8.1 cares about and the half section 15.3's
/// 7001 row describes: resolving <c>IChatClient</c> constructs <b>nothing</b> native, so
/// <c>OrtEnv.IsCreated</c> is still false after composition, and the first call goes through
/// <c>IChatModelHost.AcquireAsync</c> - which is what raises
/// <see cref="EdgeErrorCode.ChatEnvironmentNotStarted"/> when the order-400 task has not run.
/// </para>
/// </remarks>
/// <param name="registration">The registration this client speaks for.</param>
/// <param name="host">Its model host.</param>
internal sealed class PendingEdgeChatClient(ChatRegistration registration, ChatModelHost host) : IChatClient
{
    /// <inheritdoc />
    public async Task<ChatResponse> GetResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        var lease = await host.AcquireAsync(cancellationToken).ConfigureAwait(false);
        lease.Dispose();
        throw NotImplementedYet();
    }

    /// <inheritdoc />
    public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        CancellationToken cancellationToken = default)
        => Core(cancellationToken);

    /// <inheritdoc />
    public object? GetService(Type serviceType, object? serviceKey = null)
    {
        ArgumentNullException.ThrowIfNull(serviceType);

        if (serviceKey is not null)
        {
            return null;
        }

        if (serviceType.IsInstanceOfType(this))
        {
            return this;
        }

        if (serviceType == typeof(ChatClientMetadata))
        {
            return new ChatClientMetadata("onnxruntime-genai", defaultModelId: registration.Preset.Manifest.ModelId);
        }

        if (serviceType == typeof(IChatModelHost))
        {
            return host;
        }

        return serviceType == typeof(ChatModelInfo) ? host.Describe() : null;
    }

    /// <inheritdoc />
    public void Dispose()
    {
        // The model belongs to the host, which DI owns and disposes.
    }

    private async IAsyncEnumerable<ChatResponseUpdate> Core(
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var lease = await host.AcquireAsync(cancellationToken).ConfigureAwait(false);
        lease.Dispose();

        // Refuse() throws rather than returning: a turn is a refusal, never a silent empty stream.
        // Writing it as a call keeps this `yield return` reachable, which is what keeps Core an
        // iterator without an unreachable-code error.
        yield return Refuse();
    }

    private ChatResponseUpdate Refuse() => throw NotImplementedYet();

    private NotSupportedException NotImplementedYet() =>
        new($"The chat client for preset '{registration.Preset.Id}' can load its model but cannot " +
            "yet generate: the decode loop lands with EdgeChatClient. Everything else on this " +
            "registration - provisioning, the memory budget, the probes, diagnostics and the " +
            "lifecycle - is live.");
}

/// <summary>Resolves a registration's host and provisioner, keyed or not.</summary>
internal static class ChatServiceLocator
{
    /// <summary>The host for a registration, or null when nothing is registered under that key.</summary>
    /// <param name="services">The container.</param>
    /// <param name="name">The service key, or null.</param>
    /// <returns>The host, or null.</returns>
    public static ChatModelHost? TryHost(IServiceProvider services, string? name) =>
        name is null
            ? services.GetService<ChatModelHost>()
            : services.GetKeyedService<ChatModelHost>(name);

    /// <summary>The provisioner for a registration, or null.</summary>
    /// <param name="services">The container.</param>
    /// <param name="name">The service key, or null.</param>
    /// <returns>The provisioner, or null.</returns>
    public static ChatModelProvisioner? TryProvisioner(IServiceProvider services, string? name) =>
        name is null
            ? services.GetService<ChatModelProvisioner>()
            : services.GetKeyedService<ChatModelProvisioner>(name);

    /// <summary>The host for a registration.</summary>
    /// <param name="services">The container.</param>
    /// <param name="name">The service key, or null.</param>
    /// <returns>The host.</returns>
    public static ChatModelHost Host(IServiceProvider services, string? name) =>
        name is null
            ? services.GetRequiredService<ChatModelHost>()
            : services.GetRequiredKeyedService<ChatModelHost>(name);

    /// <summary>The provisioner for a registration.</summary>
    /// <param name="services">The container.</param>
    /// <param name="name">The service key, or null.</param>
    /// <returns>The provisioner.</returns>
    public static ChatModelProvisioner Provisioner(IServiceProvider services, string? name) =>
        name is null
            ? services.GetRequiredService<ChatModelProvisioner>()
            : services.GetRequiredKeyedService<ChatModelProvisioner>(name);
}
