using System.Runtime.CompilerServices;

// The three IEdgeChatDeviceProfileProvider legs and the ChatModelShape cross-check are internal on
// purpose: a consumer configures the first through AddOnnxChat and never sees the second at all,
// which raises ChatModelShapeMismatch (7008) from inside the load path. Spec 16.1's assertions are
// about exactly those internals - the net10.0 profile leg is the one every hosted runner executes -
// so the test assembly reaches them the same way sub-project 2's does.
[assembly: InternalsVisibleTo("Qavren.Edge.Chat.Tests")]
