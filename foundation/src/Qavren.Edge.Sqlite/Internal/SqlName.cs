using System.Buffers;
using System.Text;

namespace Qavren.Edge.Sqlite.Internal;

/// <summary>
/// How the builders in this package write a table or column name into SQL text.
/// </summary>
/// <remarks>
/// SQLite takes no parameters for identifiers, so every name these builders are handed ends up as
/// text. Two rules follow, and they pull in opposite directions.
/// <para>
/// <b>A name that already worked is written exactly as it was.</b> The text of a trigger and of a
/// <c>CREATE VIRTUAL TABLE</c> is stored in the database file, and a caller may have been handing
/// these builders more than a plain word all along: a name they quoted themselves
/// (<c>"my col"</c>, <c>[my col]</c>), an FTS5 column option (<c>body UNINDEXED</c>), an FTS5 table
/// option (<c>detail=column</c>). All of that still goes through untouched.
/// </para>
/// <para>
/// <b>A name that could not be written is quoted.</b> A keyword SQLite will not read as a name
/// where one is expected (<c>new.From</c>), and a name that is not one word (<c>created-at</c>),
/// were a syntax error before. They are written between double quotes, with any double quote
/// inside doubled.
/// </para>
/// </remarks>
internal static class SqlName
{
    /// <summary>
    /// The keywords SQLite 3.53 refuses where the grammar wants a name: in a column list and after
    /// <c>new.</c> / <c>old.</c>. Not the whole keyword list - most keywords fall back to being an
    /// identifier (<c>KEY</c>, <c>MATCH</c>, <c>REPLACE</c>, <c>ROW</c>, ...) and were always
    /// written bare, which is why this is a list and not a call to <c>sqlite3_keyword_check</c>.
    /// <c>IdentifierQuotingTests</c> runs every keyword the loaded library reports through the
    /// builders, so an upgrade that moves a word across this line fails there.
    /// </summary>
    private static readonly HashSet<string> s_notAName = new(StringComparer.OrdinalIgnoreCase)
    {
        "ADD", "ALL", "ALTER", "AND", "AS", "AUTOINCREMENT", "BETWEEN", "CASE", "CHECK", "COLLATE",
        "COMMIT", "CONSTRAINT", "CREATE", "DEFAULT", "DEFERRABLE", "DELETE", "DISTINCT", "DROP",
        "ELSE", "ESCAPE", "EXCEPT", "EXISTS", "FOREIGN", "FROM", "GROUP", "HAVING", "IN", "INDEX",
        "INSERT", "INTERSECT", "INTO", "IS", "ISNULL", "JOIN", "LIMIT", "NOT", "NOTHING", "NOTNULL",
        "NULL", "ON", "OR", "ORDER", "PRIMARY", "REFERENCES", "RETURNING", "SELECT", "SET", "TABLE",
        "THEN", "TO", "TRANSACTION", "UNION", "UNIQUE", "UPDATE", "USING", "VALUES", "WHEN", "WHERE",
    };

    /// <summary>
    /// Five more that are a name after a dot but something else at the start of an expression:
    /// <c>CAST</c> and <c>RAISE</c> open a call, and the three <c>CURRENT_</c> words are values.
    /// </summary>
    private static readonly HashSet<string> s_notAnOperand = new(StringComparer.OrdinalIgnoreCase)
    {
        "CAST", "CURRENT_DATE", "CURRENT_TIME", "CURRENT_TIMESTAMP", "RAISE",
    };

    /// <summary>What SQLite's tokenizer and FTS5's argument parser both skip between words.</summary>
    private static readonly char[] s_whitespace = [' ', '\t', '\r', '\n', '\f'];

    /// <summary>What an FTS5 option, a quoted name and a list of entries are spelled with.</summary>
    private static readonly SearchValues<char> s_fts5Syntax = SearchValues.Create("=,()[]\"'`");

    /// <summary>The name between double quotes, any double quote inside it doubled.</summary>
    /// <param name="name">The table or column name.</param>
    /// <returns>The quoted name.</returns>
    public static string Quote(string name) =>
        "\"" + name.Replace("\"", "\"\"", StringComparison.Ordinal) + "\"";

    /// <summary>
    /// A column name where SQLite's grammar wants a name: an <c>INSERT</c> column list, and after
    /// <c>new.</c> or <c>old.</c> in a trigger body.
    /// </summary>
    /// <param name="column">The column name, or one the caller already quoted.</param>
    /// <returns>The text to write.</returns>
    public static string AsName(string column)
    {
        if (IsQuoted(column))
        {
            return column;
        }

        var word = column.AsSpan().Trim(s_whitespace);
        return IsWord(word) && !IsKeyword(word, s_notAName) ? column : Quote(column);
    }

    /// <summary>
    /// A column name at the start of an expression, as in <c>WHERE &lt;column&gt; MATCH ...</c>.
    /// Only a keyword is touched: anything else here - a qualified name, a name the caller quoted -
    /// is the caller's own SQL and stays as written.
    /// </summary>
    /// <param name="column">The column name.</param>
    /// <returns>The text to write.</returns>
    public static string AsOperand(string column)
    {
        var word = column.AsSpan().Trim(s_whitespace);
        return IsWord(word) && (IsKeyword(word, s_notAName) || IsKeyword(word, s_notAnOperand))
            ? Quote(word.ToString())
            : column;
    }

    /// <summary>
    /// One entry of an FTS5 column list. FTS5 parses that list itself and reads a keyword as a
    /// name, so a keyword stays bare. What is quoted is an entry FTS5 could only have rejected: one
    /// with a character outside its bare words, or several words that are not a column and its
    /// <c>UNINDEXED</c> option.
    /// <para>
    /// An entry holding <c>= , ( ) [ ]</c> or a quote character is never touched. Those are how an
    /// option, a quoted name and a list are spelled, and an entry spelled with them may be exactly
    /// what the caller meant; a NAME that contains one has to arrive already quoted.
    /// </para>
    /// </summary>
    /// <param name="column">The column definition.</param>
    /// <returns>The text to write.</returns>
    public static string AsFts5Column(string column)
    {
        if (column.AsSpan().ContainsAny(s_fts5Syntax))
        {
            return column;
        }

        var words = column.Split(s_whitespace, StringSplitOptions.RemoveEmptyEntries);

        var asWritten = words.Length switch
        {
            1 => IsFts5Word(words[0]),
            2 => IsFts5Word(words[0]) && string.Equals(words[1], "unindexed", StringComparison.OrdinalIgnoreCase),
            _ => false,
        };

        return asWritten ? column : Quote(column);
    }

    /// <summary>
    /// Whether the text is one complete quoted identifier in any of the four spellings SQLite
    /// accepts where a name is expected: <c>"x"</c>, <c>[x]</c>, <c>`x`</c> and <c>'x'</c>.
    /// </summary>
    private static bool IsQuoted(string text)
    {
        if (text.Length < 2)
        {
            return false;
        }

        var open = text[0];
        var close = open == '[' ? ']' : open;
        if (open is not ('"' or '[' or '`' or '\'') || text[^1] != close)
        {
            return false;
        }

        // Inside, the closing character may only appear doubled - and never at all between
        // brackets, which have no escape. Anything else is two tokens, not one name.
        for (var i = 1; i < text.Length - 1; i++)
        {
            if (text[i] != close)
            {
                continue;
            }

            if (open == '[' || i + 1 >= text.Length - 1 || text[i + 1] != close)
            {
                return false;
            }

            i++;
        }

        return true;
    }

    /// <summary>
    /// SQLite's bare identifier: a letter, an underscore or anything outside ASCII, then the same
    /// plus digits and <c>$</c>.
    /// </summary>
    private static bool IsWord(ReadOnlySpan<char> text)
    {
        if (text.IsEmpty || char.IsAsciiDigit(text[0]) || text[0] == '$')
        {
            return false;
        }

        foreach (var c in text)
        {
            if (!(char.IsAsciiLetterOrDigit(c) || c is '_' or '$' || c >= 0x80))
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>
    /// FTS5's bare word: letters, digits, underscore, and anything outside ASCII. The statement
    /// goes through SQLite's tokenizer before FTS5 sees its arguments, though, and that refuses
    /// digits with letters after them (<c>1st</c>) - so a word that starts with a digit has to be
    /// digits throughout.
    /// </summary>
    private static bool IsFts5Word(string text)
    {
        var allDigits = true;
        foreach (var c in text)
        {
            if (!(char.IsAsciiLetterOrDigit(c) || c == '_' || c >= 0x80))
            {
                return false;
            }

            allDigits &= char.IsAsciiDigit(c);
        }

        return text.Length > 0 && (allDigits || !char.IsAsciiDigit(text[0]));
    }

    private static bool IsKeyword(ReadOnlySpan<char> word, HashSet<string> keywords)
    {
        // SQLite folds ASCII only when it looks a keyword up, so a word with anything else in it is
        // never one - whatever a culture-blind comparer makes of a dotless i.
        return Ascii.IsValid(word) && keywords.GetAlternateLookup<ReadOnlySpan<char>>().Contains(word);
    }
}
