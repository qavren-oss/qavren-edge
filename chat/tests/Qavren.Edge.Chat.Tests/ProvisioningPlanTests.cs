using Qavren.Edge.Chat.Internal;
using Qavren.Edge.Chat.Tests.Fakes;
using Xunit;

namespace Qavren.Edge.Chat.Tests;

/// <summary>
/// Spec sections 6.8, 8.3 and 12.2: the consent plan, and the two refusals that have to land
/// <b>before the first byte</b>.
/// </summary>
public sealed class ProvisioningPlanTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(), "qedge-chat-tests", Guid.NewGuid().ToString("N"));

    private static ChatPreset Preset => ChatPresets.Llama32_1BInstructInt4;

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_root))
            {
                Directory.Delete(_root, recursive: true);
            }
        }
        catch (IOException)
        {
            // A temp directory a virus scanner still has open is not a test failure.
        }
    }

    private (ChatModelProvisioner Provisioner, FakeModelStore Store, EdgeChatOptions Options) Build(
        long? freeDisk,
        bool provisioned = false)
    {
        var options = new EdgeChatOptions { Preset = Preset };
        var paths = new FakeModelPaths(_root);
        var store = new FakeModelStore(ChatModelProvisioner.DirectoryFor(paths.Models, Preset.Manifest))
        {
            Provisioned = provisioned,
        };

        var provisioner = new ChatModelProvisioner(
            Preset, options, store, paths, new RecordingLogger(), _ => freeDisk);

        return (provisioner, store, options);
    }

    [Fact]
    public void PlanReportsTheBundleTheLicenceAndTheRevisionWithNoNetworkIo()
    {
        var (provisioner, store, _) = Build(freeDisk: 100L * 1024 * 1024 * 1024);

        var plan = provisioner.Plan();

        Assert.Equal(Preset.Id, plan.PresetId);
        Assert.Equal(Preset.DisplayName, plan.DisplayName);
        Assert.False(plan.Provisioned);

        // The BUNDLE total - every file, not just the weights.
        Assert.Equal(1_241_451_982L, plan.TotalBytes);
        Assert.Equal(0, plan.BytesAlreadyPresent);
        Assert.Equal(plan.TotalBytes, plan.BytesToTransfer);
        Assert.True(plan.FitsOnDisk);

        // The SPDX expression, verbatim: LicenseRef- is a legal SPDX expression and a bare
        // LLAMA-3.2-Community is not.
        Assert.Equal("LicenseRef-LLAMA-3.2-Community", plan.SpdxLicense);
        Assert.NotNull(plan.LicenseUri);
        Assert.Equal("Arm/llama-3-2-1b-instruct-onnx-genai-int4-kquantlast-emb-int8-vivo-x300", plan.HuggingFaceRepo);
        Assert.Equal("333c515af8b9355011c0b295c4356c9c24b463ff", plan.HuggingFaceRevision);

        // The content-addressed directory: <Models>/<modelId>/<first 16 of the graph digest>.
        Assert.EndsWith(Path.Combine(Preset.Manifest.ModelId, "742f2292f396bbc6"), plan.Directory, StringComparison.Ordinal);

        // A stat, and nothing more.
        Assert.Equal(0, store.Ensures);
    }

    [Fact]
    public void PlanCountsWhatAResumedTransferWouldNotHaveToFetchAgain()
    {
        var (provisioner, _, _) = Build(freeDisk: 100L * 1024 * 1024 * 1024);
        var directory = provisioner.Plan().Directory;

        Directory.CreateDirectory(directory);
        File.WriteAllText(Path.Combine(directory, "tokenizer_config.json"), new string('x', 353));

        var plan = provisioner.Plan();

        Assert.Equal(353, plan.BytesAlreadyPresent);
        Assert.Equal(plan.TotalBytes - 353, plan.BytesToTransfer);
    }

    [Fact]
    public async Task ShortDiskIs7052BeforeTheFirstByte()
    {
        // SP2's HTTP source checks per FILE against a 32 MiB margin, which passes six times for a
        // bundle that will not fit and then fails 900 MB in. This one asks the whole question first.
        var (provisioner, store, options) = Build(freeDisk: 1_000_000_000L);

        Assert.Equal(512L * 1024 * 1024, options.Provisioning.FreeDiskMarginBytes);
        Assert.False(provisioner.Plan().FitsOnDisk);

        var exception = await Assert.ThrowsAsync<EdgeChatException>(
            () => provisioner.ProvisionAsync(cancellationToken: TestContext.Current.CancellationToken).AsTask())
            .ConfigureAwait(true);

        Assert.Equal(EdgeErrorCode.ChatInsufficientDiskSpace, exception.Code);
        Assert.Equal(0, store.Ensures);
        Assert.NotNull(exception.Remediation);
    }

    [Fact]
    public async Task IsTransferPermittedReturningFalseIs7053BeforeAConnectionIsOpened()
    {
        var (provisioner, store, options) = Build(freeDisk: 100L * 1024 * 1024 * 1024);

        // Null - the default - permits.
        Assert.Null(options.Provisioning.IsTransferPermitted);
        options.Provisioning.IsTransferPermitted = () => false;

        var exception = await Assert.ThrowsAsync<EdgeChatException>(
            () => provisioner.ProvisionAsync(cancellationToken: TestContext.Current.CancellationToken).AsTask())
            .ConfigureAwait(true);

        Assert.Equal(EdgeErrorCode.ChatDownloadNotPermitted, exception.Code);
        Assert.Equal(0, store.Ensures);
        Assert.Contains("unmetered", exception.Remediation!, StringComparison.Ordinal);
    }

    [Fact]
    public async Task APermittedTransferWithRoomOnDiskReachesTheStore()
    {
        var (provisioner, store, options) = Build(freeDisk: 100L * 1024 * 1024 * 1024);
        options.Provisioning.IsTransferPermitted = () => true;

        var provisioned = await provisioner
            .ProvisionAsync(cancellationToken: TestContext.Current.CancellationToken)
            .ConfigureAwait(true);

        Assert.Equal(1, store.Ensures);
        Assert.Equal(Preset.Manifest.ModelId, provisioned.ModelId);
    }

    [Fact]
    public async Task AnAlreadyProvisionedModelSkipsBothChecks()
    {
        // Neither check may fire when no bytes are going to move: a policy hook that refuses on
        // mobile data must not stop an app using a model it already has.
        var (provisioner, store, options) = Build(freeDisk: 1, provisioned: true);
        options.Provisioning.IsTransferPermitted = () => false;

        await provisioner.ProvisionAsync(cancellationToken: TestContext.Current.CancellationToken)
            .ConfigureAwait(true);

        Assert.Equal(1, store.Ensures);
    }

    [Fact]
    public void AnUnknownFreeDiskReadingIsNeverARefusal()
    {
        // The same rule the memory gate applies to an unreadable AvailableMemoryBytes.
        var (provisioner, _, _) = Build(freeDisk: null);
        var plan = provisioner.Plan();

        Assert.Null(plan.FreeDiskBytes);
        Assert.True(plan.FitsOnDisk);
    }
}
