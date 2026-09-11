namespace Qavren.Edge.Sqlite.Vec;

/// <summary>String builders for sqlite-vec's scalar functions. No magic, no hidden state.</summary>
public static class VecFunctions
{
    /// <summary>Returns the version string, e.g. <c>v0.1.9</c> — note the leading <c>v</c>.</summary>
    public const string Version = "vec_version()";

    public const string Debug = "vec_debug()";

    public static string Length(string expression) => $"vec_length({expression})";

    public static string Type(string expression) => $"vec_type({expression})";

    public static string Normalize(string expression) => $"vec_normalize({expression})";

    public static string ToJson(string expression) => $"vec_to_json({expression})";

    public static string DistanceCosine(string a, string b) => $"vec_distance_cosine({a}, {b})";

    public static string DistanceL2(string a, string b) => $"vec_distance_l2({a}, {b})";

    public static string DistanceL1(string a, string b) => $"vec_distance_l1({a}, {b})";

    public static string DistanceHamming(string a, string b) => $"vec_distance_hamming({a}, {b})";
}
