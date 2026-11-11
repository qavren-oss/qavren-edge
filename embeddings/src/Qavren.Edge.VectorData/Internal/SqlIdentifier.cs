namespace Qavren.Edge.VectorData.Internal;

/// <summary>
/// The one rule a name has to meet before this provider writes it into SQL text.
/// </summary>
/// <remarks>
/// SQLite takes no parameters for identifiers, so a collection name and every table and column
/// name derived from it are written between double quotes into <c>CREATE</c>, <c>DROP</c>,
/// <c>INSERT</c> and <c>SELECT</c> text. A name that contains a double quote closes its own
/// quotation: <c>x"; DROP TABLE other; --</c> handed to <c>EnsureCollectionDeletedAsync</c> is two
/// statements, and Microsoft.Data.Sqlite runs both. An app that names collections after anything a
/// user typed is exposed. Such a name is refused here, where it enters, rather than escaped at each
/// of the places it is written - no collection can exist under one today, because the same quote
/// that makes it dangerous makes every statement about it a syntax error.
/// <para>
/// Control characters go too: a NUL ends the statement text as far as SQLite's parser is concerned.
/// A single quote is fine in an identifier and is escaped where FTS5 takes the name as a literal.
/// </para>
/// </remarks>
internal static class SqlIdentifier
{
    /// <summary>Throws unless <paramref name="name"/> can be written between double quotes as it is.</summary>
    /// <param name="name">The table or column name.</param>
    /// <param name="what">What the name is, for the message: "collection name", "column name".</param>
    /// <param name="paramName">The caller's parameter the name came from.</param>
    /// <returns><paramref name="name"/>, unchanged.</returns>
    /// <exception cref="ArgumentException">The name is empty, or contains a double quote or a control character.</exception>
    public static string Require(string name, string what, string paramName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name, paramName);

        foreach (var c in name)
        {
            if (c == '"' || char.IsControl(c))
            {
                throw new ArgumentException(
                    $"The {what} cannot contain a double quote or a control character: it is written into SQL " +
                    "between double quotes, and SQLite takes no parameters for identifiers. Map whatever the " +
                    "name came from to letters, digits, spaces and punctuation other than '\"'.",
                    paramName);
            }
        }

        return name;
    }
}
