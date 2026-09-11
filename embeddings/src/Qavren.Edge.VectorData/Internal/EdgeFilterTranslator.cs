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

    private static string FormatLiteral(object? value) => value switch
    {
        null => "NULL",
        string s => "'" + s.Replace("'", "''", StringComparison.Ordinal) + "'",
        bool b => b ? "1" : "0",
        Guid g => "'" + g.ToString("D", CultureInfo.InvariantCulture).ToUpperInvariant() + "'",
        DateTime d => "'" + d.ToString("O", CultureInfo.InvariantCulture) + "'",
        DateTimeOffset d => "'" + d.ToString("O", CultureInfo.InvariantCulture) + "'",
        DateOnly d => "'" + d.ToString("O", CultureInfo.InvariantCulture) + "'",
        TimeOnly t => "'" + t.ToString("O", CultureInfo.InvariantCulture) + "'",
        byte[] bytes => "X'" + Convert.ToHexString(bytes) + "'",
        IFormattable f => f.ToString(null, CultureInfo.InvariantCulture),
        _ => throw new NotSupportedException(
            $"This provider cannot inline a constant of type '{value.GetType()}' into SQLite SQL."),
    };

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
                _sql.Append("NOT (");
                TranslatePredicate(not.Operand);
                _sql.Append(')');
                return;

            case BinaryExpression binary when TryComparisonOperator(binary.NodeType, out var op):
                TranslateComparison(binary, op);
                return;

            case MethodCallExpression call:
                TranslateMethodCall(call);
                return;

            case UnaryExpression { NodeType: ExpressionType.Convert or ExpressionType.ConvertChecked } convert:
                TranslatePredicate(convert.Operand);
                return;

            default:
                // A bare boolean property or parameter in predicate position.
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
                _sql.Append(FormatLiteral(prefix + EscapeLikePattern(constant) + suffix));
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
                _sql.Append(FormatLiteral(constant.Value));
                return;

            case MemberExpression member:
                throw Unsupported(member, $"Member '{member.Member.Name}' maps to no column on this collection.");

            default:
                throw Unsupported(unwrapped);
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
