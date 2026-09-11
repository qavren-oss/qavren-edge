using System.Globalization;
using System.Reflection;
using Xunit;

namespace Qavren.Edge.Rag.Tests;

/// <summary>
/// Spec 16.1's "Projector shape" bullet, first clause: <b>one</b> <c>Func&lt;TRecord, RagSource&gt;</c>
/// signature reaches both <c>VectorStoreRetriever</c>'s constructor and
/// <c>AddVectorStoreRetriever</c>.
/// <para>
/// It is a reflection assertion because that is the only thing a compile-time test cannot express.
/// What it catches is a two-argument projector overload - <c>Func&lt;TRecord, int, RagSource&gt;</c>,
/// or a <c>Func&lt;TRecord, RetrievalRequest, RagSource&gt;</c> "for convenience" - creeping in and
/// quietly giving the package two projector shapes. "There is exactly one projector shape in this
/// package" is spec 7's own words, and a reader has no other way to check it.
/// </para>
/// </summary>
public class ProjectorShapeTests
{
    private static readonly Assembly RagAssembly = typeof(RagSource).Assembly;

    [Fact]
    public void VectorStoreRetrieversConstructorTakesExactlyOneFuncAndItIsFuncOfRecordToRagSource()
    {
        var open = typeof(VectorStoreRetriever<,>);
        var record = open.GetGenericArguments()[1];

        var funcParameters = open
            .GetConstructors(BindingFlags.Public | BindingFlags.Instance)
            .SelectMany(c => c.GetParameters())
            .Where(p => p.ParameterType.IsGenericType &&
                        p.ParameterType.Name.StartsWith("Func`", StringComparison.Ordinal))
            .ToList();

        var projector = Assert.Single(funcParameters);
        var arguments = projector.ParameterType.GetGenericArguments();

        Assert.Equal(typeof(Func<,>), projector.ParameterType.GetGenericTypeDefinition());
        Assert.Equal(2, arguments.Length);
        Assert.Equal(record, arguments[0]);
        Assert.Equal(typeof(RagSource), arguments[1]);
    }

    [Fact]
    public void ThereIsExactlyOneAddVectorStoreRetrieverAndItsProjectorIsTheSameShape()
    {
        var methods = typeof(EdgeRagBuilderExtensions)
            .GetMethods(BindingFlags.Public | BindingFlags.Static)
            .Where(m => m.Name == "AddVectorStoreRetriever")
            .ToList();

        var method = Assert.Single(methods);
        var record = method.GetGenericArguments()[1];
        var project = method.GetParameters().Single(p => p.Name == "project");
        var arguments = project.ParameterType.GetGenericArguments();

        Assert.Equal(typeof(Func<,>), project.ParameterType.GetGenericTypeDefinition());
        Assert.Equal(2, arguments.Length);
        Assert.Equal(record, arguments[0]);
        Assert.Equal(typeof(RagSource), arguments[1]);
    }

    [Fact]
    public void NoPublicMemberAnywhereInTheAssemblyDeclaresAProjectorOfAnyOtherArity()
    {
        var offenders = new List<string>();

        foreach (var type in RagAssembly.GetExportedTypes())
        {
            var members = type
                .GetConstructors(BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static)
                .Cast<MethodBase>()
                .Concat(type.GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static));

            foreach (var member in members)
            {
                foreach (var parameter in member.GetParameters())
                {
                    if (!ReturnsARagSource(parameter.ParameterType))
                    {
                        continue;
                    }

                    var arity = parameter.ParameterType.GetGenericArguments().Length;
                    if (arity != 2)
                    {
                        offenders.Add(string.Format(
                            CultureInfo.InvariantCulture,
                            "{0}.{1}({2}) takes a projector of arity {3}",
                            type.FullName,
                            member.Name,
                            parameter.Name,
                            arity));
                    }
                }
            }
        }

        Assert.True(
            offenders.Count == 0,
            "Qavren.Edge.Rag must have exactly one projector shape, Func<TRecord, RagSource>. Offenders: " +
            string.Join("; ", offenders));
    }

    private static bool ReturnsARagSource(Type type)
    {
        if (!type.IsGenericType || !type.Name.StartsWith("Func`", StringComparison.Ordinal))
        {
            return false;
        }

        var arguments = type.GetGenericArguments();
        return arguments[^1] == typeof(RagSource);
    }
}
