namespace Leviathan.ViewModels.Preview;

/// <summary>What a stretch of source text is, for colouring.</summary>
public enum TokenKind
{
    /// <summary>Anything not picked out below; the pane draws it in the normal colour.</summary>
    Plain,

    /// <summary>A keyword, or a tag name in markup.</summary>
    Keyword,

    /// <summary>A string or attribute value.</summary>
    String,

    /// <summary>A comment.</summary>
    Comment,

    /// <summary>A number.</summary>
    Number,
}

/// <summary>A coloured stretch of one line. Stretches between tokens are plain.</summary>
public readonly record struct SyntaxToken(int Start, int Length, TokenKind Kind);

/// <summary>The families of syntax the highlighter knows; one scanner serves each family, so a new extension is one table line.</summary>
public enum SyntaxLanguage
{
    /// <summary>No colouring.</summary>
    None,

    /// <summary>C, C#, Java, JavaScript, TypeScript, Go, Rust, CSS…: <c>//</c> and <c>/* */</c> comments.</summary>
    CLike,

    /// <summary>Python, Ruby, shell, PowerShell, YAML, TOML…: <c>#</c> comments.</summary>
    Hash,

    /// <summary>SQL: <c>--</c> and <c>/* */</c> comments, case-insensitive keywords.</summary>
    Sql,

    /// <summary>XML, XAML, project files: tags, attribute values and <c>&lt;!-- --&gt;</c> comments.</summary>
    Xml,

    /// <summary>JSON: strings, numbers, true, false, null.</summary>
    Json,
}

/// <summary>
/// A small line scanner that colours source for the preview (spec §9.1: "syntax highlighting"). It is not a parser: it finds comments,
/// strings, numbers and keywords well enough to read a file at a glance, never fails on odd input, and costs one pass over the text, so a
/// 2 MB file is coloured in milliseconds off the UI thread. A string never runs past its line, and a block comment carries over lines.
/// </summary>
public static class SyntaxHighlighter
{
    private static readonly HashSet<string> CLikeKeywords = new(StringComparer.Ordinal)
    {
        "abstract", "as", "async", "await", "bool", "break", "case", "catch", "char", "class", "const", "continue", "default", "delegate", "do", "double", "else", "enum", "event",
        "extends", "false", "final", "finally", "float", "for", "foreach", "func", "function", "get", "go", "if", "implements", "import", "in", "int", "interface", "internal", "is",
        "let", "long", "namespace", "new", "null", "nullptr", "override", "package", "private", "protected", "public", "readonly", "record", "ref", "return", "sealed", "set", "short",
        "static", "string", "struct", "super", "switch", "this", "throw", "true", "try", "type", "typeof", "undefined", "using", "var", "virtual", "void", "volatile", "while", "yield",
        "fn", "impl", "mod", "mut", "pub", "self", "trait", "use", "match", "from", "export", "extern", "unsafe", "where", "with", "required", "init", "when", "out", "params",
    };

    private static readonly HashSet<string> HashKeywords = new(StringComparer.Ordinal)
    {
        "and", "as", "assert", "async", "await", "break", "class", "continue", "def", "del", "do", "done", "elif", "else", "end", "esac", "except", "False", "fi", "finally", "for",
        "foreach", "from", "function", "global", "if", "import", "in", "is", "lambda", "None", "not", "or", "param", "pass", "raise", "return", "self", "then", "True", "try", "until",
        "while", "with", "yield", "true", "false", "null", "begin", "module", "require", "let", "local", "export", "switch", "catch", "throw", "trap", "exit",
    };

    private static readonly HashSet<string> SqlKeywords = new(StringComparer.OrdinalIgnoreCase)
    {
        "select", "from", "where", "and", "or", "not", "insert", "into", "values", "update", "set", "delete", "create", "table", "alter", "drop", "index", "view", "join", "inner", "left",
        "right", "outer", "on", "group", "by", "order", "having", "limit", "offset", "as", "distinct", "union", "all", "null", "is", "in", "like", "between", "exists", "case", "when",
        "then", "else", "end", "primary", "key", "foreign", "references", "default", "begin", "commit", "rollback", "transaction", "with", "asc", "desc", "count", "sum", "avg", "min", "max",
    };

    private static readonly HashSet<string> JsonKeywords = new(StringComparer.Ordinal) { "true", "false", "null" };

    /// <summary>The syntax a file's extension (with its dot) gets.</summary>
    public static SyntaxLanguage LanguageFor(string extension) => extension.ToLowerInvariant() switch
    {
        ".cs" or ".csx" or ".java" or ".kt" or ".go" or ".rs" or ".swift" or ".dart" or ".c" or ".cc" or ".cpp" or ".h" or ".hpp" or ".js" or ".mjs" or ".cjs" or ".ts" or ".tsx" or ".jsx"
            or ".css" or ".scss" or ".less" or ".php" or ".fs" => SyntaxLanguage.CLike,
        ".py" or ".rb" or ".sh" or ".bash" or ".zsh" or ".ps1" or ".psm1" or ".pl" or ".yaml" or ".yml" or ".toml" or ".ini" or ".cfg" or ".conf" or ".env" or ".gitignore" or ".gitattributes"
            or ".editorconfig" or ".lua" => SyntaxLanguage.Hash,
        ".sql" => SyntaxLanguage.Sql,
        ".xml" or ".xaml" or ".csproj" or ".vbproj" or ".fsproj" or ".props" or ".targets" or ".resx" or ".config" or ".slnx" or ".html" or ".htm" or ".xhtml" => SyntaxLanguage.Xml,
        ".json" or ".jsonc" => SyntaxLanguage.Json,
        _ => SyntaxLanguage.None,
    };

    /// <summary>The coloured stretches of each line; the result has one entry per line, empty for a line with nothing to colour.</summary>
    public static SyntaxToken[][] Highlight(IReadOnlyList<string> lines, SyntaxLanguage language)
    {
        ArgumentNullException.ThrowIfNull(lines);
        var result = new SyntaxToken[lines.Count][];
        var inBlock = false;
        for (var i = 0; i < lines.Count; i++)
        {
            result[i] = language == SyntaxLanguage.None ? [] : ScanLine(lines[i], language, ref inBlock);
        }

        return result;
    }

    private static SyntaxToken[] ScanLine(string line, SyntaxLanguage language, ref bool inBlock)
    {
        var tokens = new List<SyntaxToken>();
        var i = 0;
        var inTag = false;
        while (i < line.Length)
        {
            if (inBlock)
            {
                var close = language == SyntaxLanguage.Xml ? "-->" : "*/";
                var end = line.IndexOf(close, i, StringComparison.Ordinal);
                var stop = end < 0 ? line.Length : end + close.Length;
                tokens.Add(new SyntaxToken(i, stop - i, TokenKind.Comment));
                inBlock = end < 0;
                i = stop;
                continue;
            }

            var c = line[i];
            if (language == SyntaxLanguage.Xml)
            {
                i = ScanXml(line, i, tokens, ref inTag, ref inBlock);
                continue;
            }

            if (StartsComment(line, i, language, out var block))
            {
                if (block)
                {
                    inBlock = true;
                    tokens.Add(new SyntaxToken(i, 2, TokenKind.Comment));
                    i += 2;
                }
                else
                {
                    tokens.Add(new SyntaxToken(i, line.Length - i, TokenKind.Comment));
                    i = line.Length;
                }

                continue;
            }

            if (c is '"' or '\'' || (c == '`' && language == SyntaxLanguage.CLike))
            {
                var end = StringEnd(line, i);
                tokens.Add(new SyntaxToken(i, end - i, TokenKind.String));
                i = end;
                continue;
            }

            if (char.IsDigit(c) && (i == 0 || !IsWordChar(line[i - 1])))
            {
                var end = i;
                while (end < line.Length && (char.IsLetterOrDigit(line[end]) || line[end] is '.' or '_'))
                {
                    end++;
                }

                tokens.Add(new SyntaxToken(i, end - i, TokenKind.Number));
                i = end;
                continue;
            }

            if (char.IsLetter(c) || c == '_')
            {
                var end = i;
                while (end < line.Length && IsWordChar(line[end]))
                {
                    end++;
                }

                if (KeywordsFor(language).Contains(line.Substring(i, end - i)))
                {
                    tokens.Add(new SyntaxToken(i, end - i, TokenKind.Keyword));
                }

                i = end;
                continue;
            }

            i++;
        }

        // A comment opened and closed on one line is found in two steps (its opener, then the rest); it is one stretch to the reader.
        for (var t = tokens.Count - 1; t > 0; t--)
        {
            if (tokens[t].Kind == TokenKind.Comment && tokens[t - 1].Kind == TokenKind.Comment && tokens[t - 1].Start + tokens[t - 1].Length == tokens[t].Start)
            {
                tokens[t - 1] = new SyntaxToken(tokens[t - 1].Start, tokens[t - 1].Length + tokens[t].Length, TokenKind.Comment);
                tokens.RemoveAt(t);
            }
        }

        return [.. tokens];
    }

    // Markup: <!-- comments -->, <tag names, and "quoted" or 'quoted' values inside a tag; text between tags stays plain.
    private static int ScanXml(string line, int i, List<SyntaxToken> tokens, ref bool inTag, ref bool inBlock)
    {
        var c = line[i];
        if (!inTag)
        {
            if (string.CompareOrdinal(line, i, "<!--", 0, 4) == 0)
            {
                inBlock = true;
                tokens.Add(new SyntaxToken(i, 4, TokenKind.Comment));
                return i + 4;
            }

            if (c == '<' && i + 1 < line.Length && (char.IsLetter(line[i + 1]) || line[i + 1] is '/' or '?' or '!'))
            {
                var end = i + 1;
                while (end < line.Length && (char.IsLetterOrDigit(line[end]) || line[end] is '/' or '?' or '!' or ':' or '-' or '_' or '.'))
                {
                    end++;
                }

                tokens.Add(new SyntaxToken(i, end - i, TokenKind.Keyword));
                inTag = true;
                return end;
            }

            return i + 1;
        }

        if (c == '>')
        {
            inTag = false;
            return i + 1;
        }

        if (c is '"' or '\'')
        {
            var end = StringEnd(line, i);
            tokens.Add(new SyntaxToken(i, end - i, TokenKind.String));
            return end;
        }

        return i + 1;
    }

    private static bool StartsComment(string line, int i, SyntaxLanguage language, out bool block)
    {
        block = false;
        switch (language)
        {
            case SyntaxLanguage.CLike:
                if (string.CompareOrdinal(line, i, "//", 0, 2) == 0)
                {
                    return true;
                }

                block = string.CompareOrdinal(line, i, "/*", 0, 2) == 0;
                return block;
            case SyntaxLanguage.Hash:
                return line[i] == '#' || (line[i] == ';' && i == 0);
            case SyntaxLanguage.Sql:
                if (string.CompareOrdinal(line, i, "--", 0, 2) == 0)
                {
                    return true;
                }

                block = string.CompareOrdinal(line, i, "/*", 0, 2) == 0;
                return block;
            default:
                return false;
        }
    }

    // A string ends at its closing quote, or at the end of the line when it has none; a backslash escapes the next character.
    private static int StringEnd(string line, int start)
    {
        var quote = line[start];
        for (var i = start + 1; i < line.Length; i++)
        {
            if (line[i] == '\\')
            {
                i++;
            }
            else if (line[i] == quote)
            {
                return i + 1;
            }
        }

        return line.Length;
    }

    private static bool IsWordChar(char c) => char.IsLetterOrDigit(c) || c == '_';

    private static HashSet<string> KeywordsFor(SyntaxLanguage language) => language switch
    {
        SyntaxLanguage.CLike => CLikeKeywords,
        SyntaxLanguage.Hash => HashKeywords,
        SyntaxLanguage.Sql => SqlKeywords,
        SyntaxLanguage.Json => JsonKeywords,
        _ => [],
    };
}
