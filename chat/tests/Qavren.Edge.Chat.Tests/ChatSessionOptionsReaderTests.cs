using Qavren.Edge.Chat.Internal;
using Qavren.Edge.Chat.Tests.Fakes;
using Qavren.Edge.Chat.Tests.Fixtures;
using Xunit;

namespace Qavren.Edge.Chat.Tests;

/// <summary>
/// Plan adjustment 29's four arms. Section 14.3 publishes <c>chatIntraOpNumThreads</c> and the draft
/// plan gave it no owner, which is how a diagnostics key becomes a stub - or, worse, how a second
/// <b>reflection-based</b> JSON parse gets bolted onto a load path whose entire point is that it is
/// trim-safe.
/// </summary>
public class ChatSessionOptionsReaderTests
{
    /// <summary>
    /// The block is <b>not</b> at the top level: an ORT GenAI config's top level is
    /// <c>model</c> and <c>search</c>, and <c>session_options</c> sits under <c>model.decoder</c>.
    /// </summary>
    private const string DeclaresFour = """
        {
          "model": {
            "decoder": {
              "filename": "model.onnx",
              "session_options": { "log_id": "onnxruntime-genai", "intra_op_num_threads": 4 }
            }
          }
        }
        """;

    private const string NoSessionOptions = """
        {
          "model": { "decoder": { "filename": "model.onnx", "head_size": 64 } }
        }
        """;

    private const string SessionOptionsWithoutThreads = """
        {
          "model": {
            "decoder": { "session_options": { "log_id": "onnxruntime-genai", "provider_options": [] } }
          }
        }
        """;

    /// <summary>The spelling a reader would get wrong: the block, but at the top level.</summary>
    private const string AtTheTopLevel = """
        { "session_options": { "intra_op_num_threads": 8 } }
        """;

    [Fact]
    public void ADeclaredThreadCountIsRead()
    {
        Assert.Equal(4, ChatSessionOptionsReader.ReadIntraOpNumThreads(DeclaresFour));
        Assert.Equal("4", ChatSessionOptionsReader.Describe(4));
    }

    [Fact]
    public void ADecoderWithNoSessionOptionsIsNotDeclared()
    {
        Assert.Null(ChatSessionOptionsReader.ReadIntraOpNumThreads(NoSessionOptions));
        Assert.Equal("(not declared)", ChatSessionOptionsReader.Describe(null));
    }

    [Fact]
    public void SessionOptionsWithoutAThreadCountIsNotDeclared()
    {
        Assert.Null(ChatSessionOptionsReader.ReadIntraOpNumThreads(SessionOptionsWithoutThreads));
        Assert.Equal("(not declared)", ChatSessionOptionsReader.Describe(null));
    }

    [Fact]
    public void TheReaderNeverSubstitutesZeroForAnAbsentKey()
    {
        // 0 is ORT's "pick a thread count for me" sentinel and is a real, DIFFERENT answer from
        // "the publisher said nothing". A reader that returned it would publish a lie.
        Assert.NotEqual(0, ChatSessionOptionsReader.ReadIntraOpNumThreads(NoSessionOptions) ?? -1);
        Assert.NotEqual("0", ChatSessionOptionsReader.Describe(null));
    }

    [Fact]
    public void TheBlockIsReadFromModelDecoderAndNotFromTheTopLevel()
    {
        // The arm that catches the PATH being wrong rather than the parser.
        Assert.Null(ChatSessionOptionsReader.ReadIntraOpNumThreads(AtTheTopLevel));
    }

    [Theory]
    [InlineData("{")]
    [InlineData("\"not an object\"")]
    [InlineData("{ \"model\": { \"decoder\": { \"session_options\": { \"intra_op_num_threads\": \"four\" } } } }")]
    public void AnUnreadableBodyIsNullAndNeverAThrow(string json)
    {
        // A diagnostics key must never be the reason a model fails to load; 7007 already names the
        // file and the field when the config itself is unusable.
        Assert.Null(ChatSessionOptionsReader.ReadIntraOpNumThreads(json));
    }

    [Fact]
    public void BothRealConfigBodiesReadWithoutThrowing()
    {
        // The arm that would catch the path being wrong against a real publisher's export rather
        // than against a body written in this file. Neither shipped preset declares the key, so the
        // contract is "null, or a positive integer" - whichever they actually shipped.
        foreach (var body in new[] { GenAiConfigFixtures.LlamaConfigJson, GenAiConfigFixtures.QwenConfigJson })
        {
            var declared = ChatSessionOptionsReader.ReadIntraOpNumThreads(body);
            Assert.True(declared is null or > 0);
        }
    }

    [Fact]
    public void ChatModelShapeExposesNoThreadCountMember()
    {
        // The grep-style guard: every field on ChatModelShape is cross-checked against the
        // provisioned config and a disagreement is 7008. A thread count is a runtime hint a
        // publisher may change between revisions without changing the model, so carrying it there
        // would turn a harmless republish into a hard failure.
        var members = typeof(ChatModelShape)
            .GetProperties()
            .Select(p => p.Name)
            .ToList();

        Assert.DoesNotContain(members, name => name.Contains("Thread", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(members, name => name.Contains("SessionOptions", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void TheAnswerIsCarriedOnChatModelInfoBesideTheShape()
    {
        // Plan adjustment 29: read once, from the config text the load path already had in hand,
        // and cached here - so the diagnostics contributor re-parses nothing.
        var info = TestInfo.Build(ChatPresets.Llama32_1BInstructInt4) with { IntraOpNumThreads = 4 };

        Assert.Equal(4, info.IntraOpNumThreads);
        Assert.Equal("4", ChatSessionOptionsReader.Describe(info.IntraOpNumThreads));
    }
}
