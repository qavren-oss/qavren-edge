namespace Qavren.Edge.Sqlite.Vec;

/// <summary>String builders for sqlite-vec's scalar functions. No magic, no hidden state.</summary>
public static class VecFunctions
{
    /// <summary>Returns the version string, e.g. <c>v0.1.9</c> — note the leading <c>v</c>.</summary>
    public const string Version = "vec_version()";

    /// <summary>Returns build/debug information about the sqlite-vec extension.</summary>
    public const string Debug = "vec_debug()";

    /// <summary>Returns the number of elements in a vector expression.</summary>
    public static string Length(string expression) => $"vec_length({expression})";

    /// <summary>Returns a vector expression's element type as text (<c>float32</c>, <c>int8</c> or <c>bit</c>).</summary>
    public static string Type(string expression) => $"vec_type({expression})";

    /// <summary>Returns the vector expression normalized to unit length.</summary>
    public static string Normalize(string expression) => $"vec_normalize({expression})";

    /// <summary>Returns the vector expression rendered as a JSON array.</summary>
    public static string ToJson(string expression) => $"vec_to_json({expression})";

    /// <summary>Returns the cosine distance between two vector expressions.</summary>
    public static string DistanceCosine(string a, string b) => $"vec_distance_cosine({a}, {b})";

    /// <summary>Returns the Euclidean (L2) distance between two vector expressions.</summary>
    public static string DistanceL2(string a, string b) => $"vec_distance_l2({a}, {b})";

    /// <summary>Returns the Manhattan (L1) distance between two vector expressions.</summary>
    public static string DistanceL1(string a, string b) => $"vec_distance_l1({a}, {b})";

    /// <summary>Returns the Hamming distance between two bit-vector expressions.</summary>
    public static string DistanceHamming(string a, string b) => $"vec_distance_hamming({a}, {b})";
}
