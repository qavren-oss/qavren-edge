using System.Collections;
using System.Globalization;
using System.Linq.Expressions;
using System.Text;
using Microsoft.Extensions.VectorData.ProviderServices;
using Microsoft.Extensions.VectorData.ProviderServices.Filter;

namespace Qavren.Edge.VectorData.Internal;

/// <summary>One bound parameter of a translated filter.</summary>
/// <param name="Name">The placeholder, including its leading <c>@</c>.</param>
/// <param name="Value">The value to bind.</param>
public sealed record EdgeFilterParameter(string Name, object? Value);

/// <summary>A translated filter: the SQL predicate and the parameters it references.</summary>
/// <param name="Sql">The predicate, written against the data table's columns.</param>
/// <param name="Parameters">The parameters, in the order they were created.</param>
public sealed record EdgeFilterTranslation(string Sql, IReadOnlyList<EdgeFilterParameter> Parameters);

/// <summary>
/// Translates an MEVD <c>Filter</c> expression into SQL over the <b>data</b> table, which the
/// caller pushes into vec0 as <c>rowid IN (SELECT "_rowid" FROM &lt;data table&gt; WHERE ...)</c>.
/// <para>
/// It never degrades to a client-side post-filter. With a genuine pre-filter available, degrading
/// would change <i>which</i> k rows come back, not merely how many - so an unsupported construct
/// throws <see cref="NotSupportedException"/> naming the node, which is also what the MEVD
/// conformance suite expects.
/// </para>
/// <para>
/// <c>StartsWith</c>, <c>EndsWith</c> and <c>Contains</c> become <c>LIKE ... ESCAPE '\'</c> in both
/// their overload shapes. A <see cref="StringComparison"/> argument is accepted and does not change
/// the SQL: SQLite's <c>LIKE</c> folds ASCII case and nothing else, whatever the caller names.
/// </para>
/// </summary>
public sealed class EdgeFilterTranslator : FilterTranslatorBase
{
    private const string LikeEscapeClause = " ESCAPE '\\'";

    private readonly StringBuilder _sql = new();
    private readonly List<EdgeFilterParameter> _parameters = [];

    /// <summary>Translates one filter lambda.</summary>
    /// <param name="filter">The MEVD filter expression.</param>
    /// <param name="model">The collection model the filter's properties bind to.</param>
    /// <returns>The predicate and its parameters.</returns>
    public EdgeFilterTranslation Translate(LambdaExpression filter, CollectionModel model)
    {
        ArgumentNullException.ThrowIfNull(filter);
        ArgumentNullException.ThrowIfNull(model);

        _sql.Clear();
        _parameters.Clear();

        var preprocessed = PreprocessFilter(
            filter,
            model,
            new FilterPreprocessingOptions { SupportsParameterization = true });

        TranslatePredicate(preprocessed);
        return new EdgeFilterTranslation(_sql.ToString(), _parameters);
    }

    private static string Escape(string name) => "\"" + name + "\"";

    private static string EscapeLikePattern(string value) => value
        .Replace("\\", "\\\\", StringComparison.Ordinal)
        .Replace("%", "\\%", StringComparison.Ordinal)
        .Replace("_", "\\_", StringComparison.Ordinal);

    private static NotSupportedException Unsupported(Expression node, string? detail = null) =>
        new($"This provider cannot translate the expression '{node}' ({node.NodeType}) into SQLite SQL." +
            (detail is null ? string.Empty : " " + detail));

    private static Expression Unwrap(Expression expression) => expression switch
    {
        UnaryExpression { NodeType: ExpressionType.Convert or ExpressionType.ConvertChecked } unary =>
            Unwrap(unary.Operand),
        _ => expression,
    };

    private static bool IsNull(Expression expression) => Unwrap(expression) switch
    {
        ConstantExpression { Value: null } => true,
        QueryParameterExpression { Value: null } => true,
        _ => false,
    };

    private static string Quote(string value) =>
        "'" + value.Replace("'", "''", StringComparison.Ordinal) + "'";

    private void TranslatePredicate(Expression node)
    {
        switch (node)
        {
            case BinaryExpression { NodeType: ExpressionType.AndAlso } and1:
                TranslateLogical(and1.Left, and1.Right, "AND");
                return;

            case BinaryExpression { NodeType: ExpressionType.OrElse } or1:
                TranslateLogical(or1.Left, or1.Right, "OR");
                return;

            case UnaryExpression { NodeType: ExpressionType.Not } not:
                // SQL's NOT is three-valued and C#'s is not. Over a row whose String column is
                // NULL, "String" = 'foo' is unknown, so a bare NOT (...) is unknown too and the row
                // is dropped - where C# evaluates !(r.String == "foo") to true and keeps it.
                // COALESCE collapses the unknown to false before the negation, which is exactly the
                // C# semantics MEVD's filter contract is written in.
                _sql.Append("NOT COALESCE(");
                TranslatePredicate(not.Operand);
                _sql.Append(", 0)");
                return;

            case BinaryExpression binary when TryComparisonOperator(binary.NodeType, out var op):
                TranslateComparison(binary, op);
                return;
        }

        // A bare boolean column in predicate position: r.Bool on the typed path, and
        // (bool)r["Bool"] - a Convert over the dictionary indexer - on the dynamic one. The dynamic
        // shape has to be recognised here, ahead of the MethodCallExpression arm below, which would
        // otherwise hand Dictionary.get_Item to the method translator and refuse it.
        if (node.Type == typeof(bool) && BindsToColumn(node))
        {
            TranslateValue(node);
            _sql.Append(" = 1");
            return;
        }

        switch (node)
        {
            case MethodCallExpression call:
                TranslateMethodCall(call);
                return;

            case UnaryExpression { NodeType: ExpressionType.Convert or ExpressionType.ConvertChecked } convert:
                TranslatePredicate(convert.Operand);
                return;

            default:
                // A bare boolean parameter or constant in predicate position.
                if (node.Type == typeof(bool))
                {
                    TranslateValue(node);
                    _sql.Append(" = 1");
                    return;
                }

                throw Unsupported(node);
        }
    }

    private void TranslateLogical(Expression left, Expression right, string op)
    {
        _sql.Append('(');
        TranslatePredicate(left);
        _sql.Append(' ').Append(op).Append(' ');
        TranslatePredicate(right);
        _sql.Append(')');
    }

    private void TranslateComparison(BinaryExpression binary, string op)
    {
        if (binary.NodeType is ExpressionType.Equal or ExpressionType.NotEqual)
        {
            var nullOnRight = IsNull(binary.Right);
            if (nullOnRight || IsNull(binary.Left))
            {
                TranslateValue(nullOnRight ? binary.Left : binary.Right);
                _sql.Append(binary.NodeType == ExpressionType.Equal ? " IS NULL" : " IS NOT NULL");
                return;
            }

            // Over a nullable column, <> is three-valued where C#'s != is not: NULL <> 'foo' is
            // unknown and drops the row, but r.String != "foo" over a null String is true and keeps
            // it. SQLite's IS NOT is the two-valued form of the same comparison - identical for
            // non-NULL operands - so it is used exactly where a NULL can actually turn up. A
            // non-nullable column keeps plain <>, which reads better and stays index-friendly.
            if (binary.NodeType == ExpressionType.NotEqual
                && (BindsToNullableColumn(binary.Left) || BindsToNullableColumn(binary.Right)))
            {
                TranslateValue(binary.Left);
                _sql.Append(" IS NOT ");
                TranslateValue(binary.Right);
                return;
            }
        }

        TranslateValue(binary.Left);
        _sql.Append(' ').Append(op).Append(' ');
        TranslateValue(binary.Right);
    }

    private void TranslateMethodCall(MethodCallExpression call)
    {
        if (TryMatchContains(call, out var source, out var item))
        {
            TranslateIn(call, source, item);
            return;
        }

        // Both overload shapes: StartsWith(value) and StartsWith(value, StringComparison). The
        // comparison is accepted and not honoured literally - SQLite's LIKE folds ASCII case and
        // nothing else, whatever the caller asks for - which is the same bargain every SQL
        // provider makes, and is documented on the type.
        if (call.Object is not null
            && call.Arguments.Count is 1 or 2
            && (call.Arguments.Count == 1 || call.Arguments[1].Type == typeof(StringComparison))
            && call.Method.DeclaringType == typeof(string))
        {
            var pattern = call.Method.Name switch
            {
                nameof(string.StartsWith) => (Prefix: string.Empty, Suffix: "%"),
                nameof(string.EndsWith) => (Prefix: "%", Suffix: string.Empty),
                nameof(string.Contains) => (Prefix: "%", Suffix: "%"),
                _ => default,
            };

            if (pattern != default)
            {
                TranslateLike(call, call.Object, call.Arguments[0], pattern.Prefix, pattern.Suffix);
                return;
            }
        }

        throw Unsupported(call, $"Method '{call.Method.DeclaringType?.Name}.{call.Method.Name}' has no SQLite translation.");
    }

    private void TranslateIn(MethodCallExpression call, Expression source, Expression item)
    {
        TranslateValue(item);
        _sql.Append(" IN (");

        switch (Unwrap(source))
        {
            case NewArrayExpression { NodeType: ExpressionType.NewArrayInit } array:
                for (var i = 0; i < array.Expressions.Count; i++)
                {
                    if (i > 0)
                    {
                        _sql.Append(", ");
                    }

                    TranslateValue(array.Expressions[i]);
                }

                break;

            case QueryParameterExpression { Value: IEnumerable values } when values is not string:
                AppendEnumerable(values);
                break;

            case ConstantExpression { Value: IEnumerable constants } when constants is not string:
                AppendEnumerable(constants);
                break;

            default:
                throw Unsupported(call, "Contains needs an inline array or a captured enumerable.");
        }

        _sql.Append(')');
    }

    private void AppendEnumerable(IEnumerable values)
    {
        var first = true;
        foreach (var value in values)
        {
            if (!first)
            {
                _sql.Append(", ");
            }

            first = false;
            _sql.Append(AddParameter(value));
        }

        if (first)
        {
            // An empty IN () is a syntax error in SQLite; NULL never matches, which is the same
            // result set with valid SQL.
            _sql.Append("NULL");
        }
    }

    private void TranslateLike(
        MethodCallExpression call,
        Expression instance,
        Expression argument,
        string prefix,
        string suffix)
    {
        TranslateValue(instance);
        _sql.Append(" LIKE ");

        switch (Unwrap(argument))
        {
            case ConstantExpression { Value: string constant }:
                _sql.Append(Quote(prefix + EscapeLikePattern(constant) + suffix));
                break;

            case QueryParameterExpression { Value: string captured }:
                _sql.Append(AddParameter(prefix + EscapeLikePattern(captured) + suffix));
                break;

            default:
                throw Unsupported(call, "The pattern of a LIKE translation must be a string constant or a captured string.");
        }

        _sql.Append(LikeEscapeClause);
    }

    private void TranslateValue(Expression node)
    {
        var unwrapped = Unwrap(node);

        // MEVD's TryBindProperty throws rather than returning false when the member belongs to the
        // record but maps to no property in the model. That is precisely the "a property mapping to
        // no column" case, and it owes the caller a NotSupportedException naming the property.
        bool bound;
        PropertyModel? property;
        try
        {
            bound = TryBindProperty(unwrapped, out property);
        }
        catch (InvalidOperationException ex)
        {
            // A DYNAMIC filter naming a property the collection does not have is the caller's
            // mistake, and MEVD's own InvalidOperationException - which quotes the bad name - is
            // both what the abstraction documents and what the conformance suite asserts, so it is
            // left to travel. A TYPED member that maps to no column is a provider-side gap in the
            // model, and that one owes the caller a NotSupportedException naming the member.
            if (unwrapped is MethodCallExpression)
            {
                throw;
            }

            throw new NotSupportedException(
                $"This provider cannot translate the expression '{unwrapped}': {ex.Message}", ex);
        }

        if (bound && property is not null)
        {
            _sql.Append(Escape(property.StorageName));
            return;
        }

        switch (unwrapped)
        {
            case QueryParameterExpression parameter:
                _sql.Append(AddParameter(parameter.Value));
                return;

            case ConstantExpression constant:
                AppendConstant(constant.Value);
                return;

            case MemberExpression member:
                throw Unsupported(member, $"Member '{member.Member.Name}' maps to no column on this collection.");

            default:
                throw Unsupported(unwrapped);
        }
    }

    /// <summary>
    /// Renders an inline constant. Text, booleans and numbers are written straight into the SQL -
    /// their SQLite spelling is unambiguous and an inlined literal keeps the emitted predicate
    /// readable and loggable. Everything else is BOUND instead, because its stored spelling belongs
    /// to Microsoft.Data.Sqlite rather than to this translator: a <see cref="DateTime"/> is stored
    /// as <c>2020-01-01 12:30:45</c>, not as the round-trip <c>O</c> form, and a literal that
    /// guessed differently would silently match nothing. Binding is the only way a filter's
    /// spelling is guaranteed to be the writer's spelling, for every type and every version.
    /// </summary>
    /// <param name="value">The constant's value.</param>
    private void AppendConstant(object? value)
    {
        switch (value)
        {
            case null:
                _sql.Append("NULL");
                return;

            case string text:
                _sql.Append(Quote(text));
                return;

            case bool flag:
                _sql.Append(flag ? '1' : '0');
                return;

            case int or long or short or sbyte or byte or uint or ulong or ushort or float or double:
                _sql.Append(((IFormattable)value).ToString(null, CultureInfo.InvariantCulture));
                return;

            default:
                _sql.Append(AddParameter(value));
                return;
        }
    }

    /// <summary>Whether the expression binds to a column of this collection.</summary>
    /// <param name="node">The candidate expression.</param>
    /// <returns><see langword="true"/> when it names a modelled property.</returns>
    private bool BindsToColumn(Expression node) => TryBindColumn(node) is not null;

    /// <summary>Whether the expression binds to a column whose value can be SQL NULL.</summary>
    /// <param name="node">The candidate expression.</param>
    /// <returns><see langword="true"/> when it names a modelled property of a nullable type.</returns>
    private bool BindsToNullableColumn(Expression node) =>
        TryBindColumn(node) is { } property
        && (!property.Type.IsValueType || Nullable.GetUnderlyingType(property.Type) is not null);

    private PropertyModel? TryBindColumn(Expression node)
    {
        try
        {
            return TryBindProperty(Unwrap(node), out var property) ? property : null;
        }
        catch (InvalidOperationException)
        {
            // Not a column. The caller is only asking a question here; whoever goes on to translate
            // the node reports the failure.
            return null;
        }
    }

    private string AddParameter(object? value)
    {
        var name = "@p" + (_parameters.Count + 1).ToString(CultureInfo.InvariantCulture);
        _parameters.Add(new EdgeFilterParameter(name, value));
        return name;
    }

    private static bool TryComparisonOperator(ExpressionType nodeType, out string op)
    {
        op = nodeType switch
        {
            ExpressionType.Equal => "=",
            ExpressionType.NotEqual => "<>",
            ExpressionType.LessThan => "<",
            ExpressionType.LessThanOrEqual => "<=",
            ExpressionType.GreaterThan => ">",
            ExpressionType.GreaterThanOrEqual => ">=",
            _ => string.Empty,
        };

        return op.Length > 0;
    }
}
