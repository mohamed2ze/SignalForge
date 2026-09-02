using System.Globalization;
using System.Text;
using System.Text.Json.Nodes;

namespace SignalForge.Application.ExpressionEvaluation;

/// <summary>
/// Result of evaluating a condition expression.
/// On error the value defaults to <see langword="false"/> and <see cref="Error"/> carries the message.
/// </summary>
public sealed record ConditionEvaluationResult(bool Value, string? Error);

/// <summary>
/// Sandboxed evaluator for workflow condition expressions.
///
/// Grammar (small and deliberately safe):
///   expr        := orExpr
///   orExpr      := andExpr ( 'or' andExpr )*
///   andExpr     := notExpr ( 'and' notExpr )*
///   notExpr     := 'not' notExpr | comparison
///   comparison  := operand ( ('==' | '!=' | '>' | '<' | '>=' | '<=') operand )?
///   operand     := '$' ( '.' ident | '[' number ']' )* | 'true' | 'false' | 'null'
///                  | number | string | '(' expr ')'
///
/// No function calls, no arithmetic, no environment access. A JSON-path reference
/// resolves against the supplied context root ("$"); missing paths evaluate to null.
/// </summary>
public static class ConditionExpressionEvaluator
{
    /// <summary>
    /// Maximum length of a condition expression accepted by the evaluator. Guards the tokenizer
    /// against unbounded input (e.g. a maliciously long condition persisted by an admin).
    /// </summary>
    public const int MaxExpressionLength = 2048;

    /// <summary>
    /// Maximum nesting depth (parentheses and 'not' chains) accepted by the parser. Prevents a
    /// pathological expression like a thousand nested parentheses from exhausting the stack; the
    /// parser fails fast with an error instead (Decision #26).
    /// </summary>
    public const int MaxParseDepth = 64;

    /// <summary>
    /// Evaluates a condition expression against a context JSON node.
    /// Never throws — errors are captured and return <see langword="false"/>.
    /// </summary>
    /// <param name="expression">The condition expression text.</param>
    /// <param name="context">The evaluation context root ("$"). May be null.</param>
    public static ConditionEvaluationResult Evaluate(string expression, JsonNode? context)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(expression))
                throw new ConditionExpressionException("Condition expression is empty.");

            if (expression.Length > MaxExpressionLength)
                throw new ConditionExpressionException(
                    $"Condition expression exceeds maximum length of {MaxExpressionLength} characters.");

            IReadOnlyList<Token> tokens = Tokenizer.Tokenize(expression);
            Node ast = new Parser(tokens, MaxParseDepth).Parse();
            return new ConditionEvaluationResult(Evaluator.ToBool(Evaluator.Evaluate(ast, context)), null);
        }
        catch (Exception ex)
        {
            return new ConditionEvaluationResult(false, ex.Message);
        }
    }
}

public sealed class ConditionExpressionException : Exception
{
    public ConditionExpressionException(string message) : base(message) { }
}

#region AST

internal abstract record Node;

internal sealed record LiteralNode(JsonNode? Value) : Node;

internal sealed record PathNode(IReadOnlyList<PathSegment> Segments) : Node;

internal sealed record PathSegment(int? Index, string? Property);

internal sealed record UnaryNode(Node Operand) : Node;

internal sealed record BinaryNode(string Operator, Node Left, Node Right) : Node;

#endregion

#region Tokenizer

internal enum TokenType
{
    Root,        // $
    Dot,         // .
    LBracket,    // [
    RBracket,    // ]
    LParen,      // (
    RParen,      // )
    Number,
    String,
    Identifier,
    True,
    False,
    Null,
    Equals,      // ==
    NotEquals,   // !=
    Gt,          // >
    Lt,          // <
    Gte,         // >=
    Lte,         // <=
    And,         // and
    Or,          // or
    Not,         // not
    End,
}

internal readonly record struct Token(TokenType Type, string Value, int Position);

internal static class Tokenizer
{
    public static IReadOnlyList<Token> Tokenize(string input)
    {
        var tokens = new List<Token>();
        int i = 0;

        while (i < input.Length)
        {
            char c = input[i];

            if (char.IsWhiteSpace(c))
            {
                i++;
                continue;
            }

            switch (c)
            {
                case '$':
                    tokens.Add(new Token(TokenType.Root, "$", i));
                    i++;
                    continue;
                case '.':
                    tokens.Add(new Token(TokenType.Dot, ".", i));
                    i++;
                    continue;
                case '[':
                    tokens.Add(new Token(TokenType.LBracket, "[", i));
                    i++;
                    continue;
                case ']':
                    tokens.Add(new Token(TokenType.RBracket, "]", i));
                    i++;
                    continue;
                case '(':
                    tokens.Add(new Token(TokenType.LParen, "(", i));
                    i++;
                    continue;
                case ')':
                    tokens.Add(new Token(TokenType.RParen, ")", i));
                    i++;
                    continue;
            }

            // Comparison operators
            if (c == '=' || c == '!' || c == '>' || c == '<')
            {
                int start = i;
                bool hasSecond = i + 1 < input.Length && input[i + 1] == '=';
                if (hasSecond)
                {
                    string op = input.Substring(start, 2);
                    TokenType type = op switch
                    {
                        "==" => TokenType.Equals,
                        "!=" => TokenType.NotEquals,
                        ">=" => TokenType.Gte,
                        "<=" => TokenType.Lte,
                        _ => throw new ConditionExpressionException($"Unexpected operator '{op}' at position {start}")
                    };
                    tokens.Add(new Token(type, op, start));
                    i += 2;
                    continue;
                }

                TokenType single = c switch
                {
                    '>' => TokenType.Gt,
                    '<' => TokenType.Lt,
                    _ => throw new ConditionExpressionException($"Unexpected character '{c}' at position {start}")
                };
                tokens.Add(new Token(single, c.ToString(), start));
                i++;
                continue;
            }

            // Quoted string: '...' or "...", with backslash escapes.
            if (c == '\'' || c == '"')
            {
                char quote = c;
                int start = i;
                i++;
                var sb = new StringBuilder();
                bool closed = false;
                while (i < input.Length)
                {
                    char sc = input[i];
                    if (sc == '\\' && i + 1 < input.Length)
                    {
                        char next = input[i + 1];
                        sb.Append(next switch
                        {
                            'n' => '\n',
                            't' => '\t',
                            'r' => '\r',
                            _ => next // \', \", \\
                        });
                        i += 2;
                        continue;
                    }

                    if (sc == quote)
                    {
                        i++;
                        closed = true;
                        break;
                    }

                    sb.Append(sc);
                    i++;
                }

                if (!closed)
                    throw new ConditionExpressionException($"Unterminated string literal at position {start}");

                tokens.Add(new Token(TokenType.String, sb.ToString(), start));
                continue;
            }

            // Number: optional leading '-', digits, optional fractional part (only if '.' is followed by a digit,
            // so paths like $.output.1.status still tokenize the dot separately).
            if (char.IsDigit(c) || (c == '-' && i + 1 < input.Length && char.IsDigit(input[i + 1])))
            {
                int start = i;
                if (c == '-') i++;
                while (i < input.Length && char.IsDigit(input[i])) i++;
                if (i < input.Length && input[i] == '.' && i + 1 < input.Length && char.IsDigit(input[i + 1]))
                {
                    i++;
                    while (i < input.Length && char.IsDigit(input[i])) i++;
                }

                tokens.Add(new Token(TokenType.Number, input[start..i], start));
                continue;
            }

            // Identifier / keyword.
            if (char.IsLetter(c) || c == '_')
            {
                int start = i;
                while (i < input.Length && (char.IsLetterOrDigit(input[i]) || input[i] == '_')) i++;
                string word = input[start..i];

                TokenType type = word.ToLowerInvariant() switch
                {
                    "and" => TokenType.And,
                    "or" => TokenType.Or,
                    "not" => TokenType.Not,
                    "true" => TokenType.True,
                    "false" => TokenType.False,
                    "null" => TokenType.Null,
                    _ => TokenType.Identifier
                };

                tokens.Add(new Token(type, word, start));
                continue;
            }

            throw new ConditionExpressionException($"Unexpected character '{c}' at position {i}");
        }

        tokens.Add(new Token(TokenType.End, string.Empty, input.Length));
        return tokens;
    }
}

#endregion

#region Parser

internal sealed class Parser
{
    private readonly IReadOnlyList<Token> _tokens;
    private readonly int _maxDepth;
    private int _position;
    private int _depth;

    public Parser(IReadOnlyList<Token> tokens, int maxDepth)
    {
        _tokens = tokens;
        _maxDepth = maxDepth;
    }

    public Node Parse()
    {
        Node node = ParseOr();
        Expect(TokenType.End, "trailing tokens");
        return node;
    }

    private Node ParseOr()
    {
        _depth++;
        try
        {
            if (_depth > _maxDepth)
                throw new ConditionExpressionException(
                    $"Condition expression exceeds maximum nesting depth of {_maxDepth}.");

            Node left = ParseAnd();
            while (Current.Type == TokenType.Or)
            {
                Advance();
                Node right = ParseAnd();
                left = new BinaryNode("or", left, right);
            }

            return left;
        }
        finally
        {
            _depth--;
        }
    }

    private Node ParseAnd()
    {
        Node left = ParseNot();
        while (Current.Type == TokenType.And)
        {
            Advance();
            Node right = ParseNot();
            left = new BinaryNode("and", left, right);
        }

        return left;
    }

    private Node ParseNot()
    {
        _depth++;
        try
        {
            if (_depth > _maxDepth)
                throw new ConditionExpressionException(
                    $"Condition expression exceeds maximum nesting depth of {_maxDepth}.");

            if (Current.Type == TokenType.Not)
            {
                Advance();
                return new UnaryNode(ParseNot());
            }

            return ParseComparison();
        }
        finally
        {
            _depth--;
        }
    }

    private Node ParseComparison()
    {
        Node left = ParseOperand();

        if (Current.Type is TokenType.Equals or TokenType.NotEquals or TokenType.Gt or TokenType.Lt or TokenType.Gte or TokenType.Lte)
        {
            string op = Current.Value;
            Advance();
            Node right = ParseOperand();
            return new BinaryNode(op, left, right);
        }

        return left;
    }

    private Node ParseOperand()
    {
        Token token = Current;

        switch (token.Type)
        {
            case TokenType.LParen:
                Advance();
                Node inner = ParseOr();
                Expect(TokenType.RParen, "expected ')'");
                return inner;

            case TokenType.Root:
                {
                    Advance();
                    var segments = new List<PathSegment>();
                    while (true)
                    {
                        if (Current.Type == TokenType.Dot)
                        {
                            Advance();
                            Token seg = Current;
                            if (seg.Type != TokenType.Identifier && seg.Type != TokenType.Number)
                                throw new ConditionExpressionException($"Expected property name after '.' at position {seg.Position}");
                            segments.Add(new PathSegment(null, seg.Value));
                            Advance();
                            continue;
                        }

                        if (Current.Type == TokenType.LBracket)
                        {
                            Advance();
                            Token indexToken = Current;
                            if (indexToken.Type != TokenType.Number)
                                throw new ConditionExpressionException($"Expected array index after '[' at position {indexToken.Position}");
                            if (!int.TryParse(indexToken.Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out int index) || index < 0)
                                throw new ConditionExpressionException($"Invalid array index '{indexToken.Value}' at position {indexToken.Position}");
                            segments.Add(new PathSegment(index, null));
                            Advance();
                            Expect(TokenType.RBracket, "expected ']'");
                            continue;
                        }

                        break;
                    }

                    return new PathNode(segments);
                }

            case TokenType.String:
            case TokenType.Number:
            case TokenType.True:
            case TokenType.False:
            case TokenType.Null:
                {
                    Advance();
                    return new LiteralNode(ToJsonNode(token));
                }

            default:
                throw new ConditionExpressionException($"Unexpected token '{token.Value}' at position {token.Position}");
        }
    }

    private static JsonNode? ToJsonNode(Token token)
    {
        return token.Type switch
        {
            TokenType.String => JsonValue.Create(token.Value),
            TokenType.True => JsonValue.Create(true),
            TokenType.False => JsonValue.Create(false),
            TokenType.Null => null,
            TokenType.Number => ParseNumber(token.Value),
            _ => throw new ConditionExpressionException($"Unexpected literal '{token.Value}' at position {token.Position}")
        };
    }

    private static JsonNode? ParseNumber(string text)
    {
        if (decimal.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out decimal d))
            return JsonValue.Create(d);
        if (long.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out long l))
            return JsonValue.Create(l);
        return JsonValue.Create(text);
    }

    private Token Current => _tokens[_position];

    private void Advance() => _position++;

    private void Expect(TokenType type, string message)
    {
        if (Current.Type != type)
            throw new ConditionExpressionException($"{message} at position {Current.Position} (found '{Current.Value}')");
        Advance();
    }
}

#endregion

#region Evaluator

internal static class Evaluator
{
    public static JsonNode? Evaluate(Node node, JsonNode? context)
    {
        return node switch
        {
            LiteralNode literal => literal.Value,
            PathNode path => ResolvePath(context, path.Segments),
            UnaryNode unary => JsonValue.Create(!ToBool(Evaluate(unary.Operand, context))),
            BinaryNode binary => EvaluateBinary(binary, context),
            _ => throw new ConditionExpressionException("Unknown expression node.")
        };
    }

    private static JsonNode? EvaluateBinary(BinaryNode binary, JsonNode? context)
    {
        switch (binary.Operator)
        {
            case "and":
                {
                    bool left = ToBool(Evaluate(binary.Left, context));
                    if (!left) return JsonValue.Create(false);
                    return JsonValue.Create(ToBool(Evaluate(binary.Right, context)));
                }
            case "or":
                {
                    bool left = ToBool(Evaluate(binary.Left, context));
                    if (left) return JsonValue.Create(true);
                    return JsonValue.Create(ToBool(Evaluate(binary.Right, context)));
                }
            case "==":
            case "!=":
                {
                    JsonNode? left = Evaluate(binary.Left, context);
                    JsonNode? right = Evaluate(binary.Right, context);
                    bool equal = ValuesEqual(left, right);
                    return JsonValue.Create(binary.Operator == "==" ? equal : !equal);
                }
            case ">":
            case "<":
            case ">=":
            case "<=":
                {
                    JsonNode? left = Evaluate(binary.Left, context);
                    JsonNode? right = Evaluate(binary.Right, context);
                    int comparison = CompareOrdered(left, right);
                    bool result = binary.Operator switch
                    {
                        ">" => comparison > 0,
                        "<" => comparison < 0,
                        ">=" => comparison >= 0,
                        "<=" => comparison <= 0,
                        _ => false
                    };
                    return JsonValue.Create(result);
                }
            default:
                throw new ConditionExpressionException($"Unknown operator '{binary.Operator}'");
        }
    }

    private static JsonNode? ResolvePath(JsonNode? root, IReadOnlyList<PathSegment> segments)
    {
        JsonNode? current = root;

        foreach (PathSegment segment in segments)
        {
            if (current is null)
                return null;

            if (segment.Index.HasValue)
            {
                if (current is JsonArray array && segment.Index.Value < array.Count)
                {
                    current = array[segment.Index.Value];
                }
                else
                {
                    return null;
                }
            }
            else
            {
                if (current is JsonObject obj)
                {
                    current = obj[segment.Property!];
                }
                else
                {
                    return null;
                }
            }
        }

        return current;
    }

    public static bool ToBool(JsonNode? node)
    {
        if (node is null)
            return false;

        if (node is JsonValue value)
        {
            if (value.TryGetValue<bool>(out bool b))
                return b;
            if (value.TryGetValue<decimal>(out decimal d))
                return d != 0;
            if (value.TryGetValue<string>(out string? s))
            {
                if (bool.TryParse(s, out bool parsedBool))
                    return parsedBool;
                return !string.IsNullOrWhiteSpace(s);
            }
        }

        return true;
    }

    private static bool ValuesEqual(JsonNode? left, JsonNode? right)
    {
        if (left is null || right is null)
            return left is null && right is null;

        if (left is JsonValue lv && right is JsonValue rv)
        {
            if (TryGetDecimal(lv, out decimal ld) && TryGetDecimal(rv, out decimal rd))
                return ld == rd;

            if (lv.TryGetValue<bool>(out bool lb) && rv.TryGetValue<bool>(out bool rb))
                return lb == rb;

            if (lv.TryGetValue<string>(out string? ls) && rv.TryGetValue<string>(out string? rs))
                return string.Equals(ls, rs, StringComparison.Ordinal);

            return false;
        }

        return false;
    }

    private static int CompareOrdered(JsonNode? left, JsonNode? right)
    {
        if (left is JsonValue lv && right is JsonValue rv)
        {
            if (TryGetDecimal(lv, out decimal ld) && TryGetDecimal(rv, out decimal rd))
                return ld.CompareTo(rd);

            if (lv.TryGetValue<string>(out string? ls) && rv.TryGetValue<string>(out string? rs))
                return string.CompareOrdinal(ls, rs);

            throw new ConditionExpressionException("Cannot compare operands of different types.");
        }

        throw new ConditionExpressionException("Cannot compare null or non-primitive values.");
    }

    private static bool TryGetDecimal(JsonValue value, out decimal result)
    {
        // Prefer decimal, then long, then double for robustness across JSON representations.
        if (value.TryGetValue<decimal>(out result))
            return true;

        if (value.TryGetValue<long>(out long l))
        {
            result = l;
            return true;
        }

        if (value.TryGetValue<double>(out double d))
        {
            result = (decimal)d;
            return true;
        }

        result = default;
        return false;
    }
}

#endregion