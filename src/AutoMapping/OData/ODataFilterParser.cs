using System.Globalization;
using System.Linq.Expressions;
using System.Reflection;
using System.Text;

namespace AutoMapping.OData;

/// <summary>
/// Parses an OData v4 <c>$filter</c> / <c>$orderby</c> expression into an expression tree over a DTO type.
/// Only members of that type and a fixed list of functions are reachable, so a query string cannot call arbitrary code.
/// </summary>
internal sealed class ODataFilterParser
{
    private enum T { End, Ident, Number, String, LParen, RParen, Comma, Colon }

    private readonly record struct Token(T Kind, string Text);

    private readonly List<Token> _tokens = new();
    private int _pos;
    private int _depth;
    private readonly ParameterExpression _it;
    private readonly Dictionary<string, ParameterExpression> _ranges = new(StringComparer.Ordinal);   // any/all range variables
    private readonly ODataSettings _settings;

    private ODataFilterParser(string text, Type itemType, ODataSettings settings)
    {
        _settings = settings;
        if (text.Length > settings.MaxExpressionLength)
            throw new ODataException($"The query expression is longer than {settings.MaxExpressionLength} characters.");
        _it = Expression.Parameter(itemType, "it");
        Tokenize(text);
    }

    public static LambdaExpression ParseFilter(string text, Type itemType, ODataSettings settings)
    {
        var parser = new ODataFilterParser(text, itemType, settings);
        var body = parser.ParseOr();
        parser.Expect(T.End);
        if (body.Type != typeof(bool)) throw new ODataException("$filter must be a boolean expression.");
        return Expression.Lambda(body, parser._it);
    }

    /// <summary>"Name desc, Customer/City" -> (selector over the DTO, descending).</summary>
    public static List<(LambdaExpression Selector, bool Descending)> ParseOrderBy(string text, Type itemType, ODataSettings settings)
    {
        var result = new List<(LambdaExpression, bool)>();
        var items = text.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        settings.CheckList("$orderby", text, items.Length);
        foreach (var part in items)
        {
            var pieces = part.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            var descending = false;
            if (pieces.Length == 2 && pieces[1].Equals("desc", StringComparison.OrdinalIgnoreCase)) descending = true;
            else if (pieces.Length == 2 && !pieces[1].Equals("asc", StringComparison.OrdinalIgnoreCase)) throw new ODataException($"Invalid $orderby item '{part}'.");
            else if (pieces.Length > 2 || pieces.Length == 0) throw new ODataException($"Invalid $orderby item '{part}'.");

            var parser = new ODataFilterParser(pieces[0], itemType, settings);
            var body = parser.ParsePath();
            parser.Expect(T.End);
            result.Add((Expression.Lambda(body, parser._it), descending));
        }
        return result;
    }

    // ------------------------------------------------------------------ tokenizer

    private void Tokenize(string s)
    {
        var i = 0;
        while (i < s.Length)
        {
            var c = s[i];
            if (char.IsWhiteSpace(c)) { i++; continue; }
            if (c == '(') { _tokens.Add(new(T.LParen, "(")); i++; }
            else if (c == ')') { _tokens.Add(new(T.RParen, ")")); i++; }
            else if (c == ',') { _tokens.Add(new(T.Comma, ",")); i++; }
            else if (c == ':') { _tokens.Add(new(T.Colon, ":")); i++; }
            else if (c == '\'')
            {
                var sb = new StringBuilder();
                i++;
                while (true)
                {
                    if (i >= s.Length) throw new ODataException("Unterminated string literal.");
                    if (s[i] == '\'')
                    {
                        if (i + 1 < s.Length && s[i + 1] == '\'') { sb.Append('\''); i += 2; continue; }
                        i++;
                        break;
                    }
                    sb.Append(s[i++]);
                }
                _tokens.Add(new(T.String, sb.ToString()));
            }
            else if (char.IsDigit(c) || (c == '-' && i + 1 < s.Length && char.IsDigit(s[i + 1]) && PreviousIsOperator()))
            {
                var start = i++;
                // numbers, but also dates (2024-05-01), datetimes (2024-05-01T10:00:00Z) and guids start with digits
                while (i < s.Length && (char.IsLetterOrDigit(s[i]) || s[i] is '.' or '-' or ':' or '+')) i++;
                _tokens.Add(new(T.Number, s[start..i]));
            }
            else if (char.IsLetter(c) || c == '_')
            {
                var start = i++;
                while (i < s.Length && (char.IsLetterOrDigit(s[i]) || s[i] is '_' or '/' or '.')) i++;
                // OData enum literal: Namespace.EnumType'Member' -> the member name as a string, coerced to the enum later
                if (i < s.Length && s[i] == '\'' && s[start..i].Contains('.')) continue;
                _tokens.Add(new(T.Ident, s[start..i]));
            }
            else throw new ODataException($"Unexpected character '{c}' in the query expression.");
        }
        _tokens.Add(new(T.End, ""));
    }

    private bool PreviousIsOperator() =>
        _tokens.Count == 0 || _tokens[^1].Kind is T.LParen or T.Comma ||
        _tokens[^1].Kind == T.Ident && Operators.Contains(_tokens[^1].Text.ToLowerInvariant());

    private static readonly HashSet<string> Operators = new() { "eq", "ne", "gt", "ge", "lt", "le", "and", "or", "not", "add", "sub", "mul", "div", "mod" };

    // ------------------------------------------------------------------ parser

    private Token Peek => _tokens[_pos];
    private Token Next() => _tokens[_pos++];

    private void Expect(T kind)
    {
        if (Peek.Kind != kind) throw new ODataException($"Unexpected '{Peek.Text}' in the query expression.");
        _pos++;
    }

    private bool PeekWord(string word) => Peek.Kind == T.Ident && Peek.Text.Equals(word, StringComparison.OrdinalIgnoreCase);

    private Expression ParseOr()
    {
        if (++_depth > _settings.MaxExpressionDepth) throw new ODataException("The query expression is nested too deeply.");
        var left = ParseAnd();
        while (PeekWord("or")) { _pos++; left = Expression.OrElse(AsBool(left), AsBool(ParseAnd())); }
        _depth--;
        return left;
    }

    private Expression ParseAnd()
    {
        var left = ParseNot();
        while (PeekWord("and")) { _pos++; left = Expression.AndAlso(AsBool(left), AsBool(ParseNot())); }
        return left;
    }

    private Expression ParseNot()
    {
        if (PeekWord("not")) { _pos++; return Expression.Not(AsBool(ParseNot())); }
        return ParseComparison();
    }

    private static Expression AsBool(Expression e) =>
        e.Type == typeof(bool) ? e : e.Type == typeof(bool?) ? Expression.Equal(e, Expression.Constant(true, typeof(bool?))) : throw new ODataException("A boolean expression was expected.");

    private Expression ParseComparison()
    {
        var left = ParseAdditive();
        while (Peek.Kind == T.Ident && Peek.Text.ToLowerInvariant() is "eq" or "ne" or "gt" or "ge" or "lt" or "le")
        {
            var op = Next().Text.ToLowerInvariant();
            var right = ParseAdditive();
            (left, right) = Unify(left, right);
            left = Compare(op, left, right);
        }
        return left;
    }

    private static Expression Compare(string op, Expression l, Expression r)
    {
        if (op is "eq") return Expression.Equal(l, r);
        if (op is "ne") return Expression.NotEqual(l, r);
        if (l.Type == typeof(string))
        {
            var cmp = Expression.Call(typeof(string), nameof(string.CompareOrdinal), null, l, r);
            return Relational(op, cmp, Expression.Constant(0));
        }
        if (l.Type == typeof(bool) || l.Type == typeof(Guid)) throw new ODataException($"'{op}' cannot compare {l.Type.Name} values.");
        return Relational(op, l, r);
    }

    private static Expression Relational(string op, Expression l, Expression r) => op switch
    {
        "gt" => Expression.GreaterThan(l, r),
        "ge" => Expression.GreaterThanOrEqual(l, r),
        "lt" => Expression.LessThan(l, r),
        _ => Expression.LessThanOrEqual(l, r)
    };

    private Expression ParseAdditive()
    {
        var left = ParseMultiplicative();
        while (PeekWord("add") || PeekWord("sub"))
        {
            var add = Next().Text.Equals("add", StringComparison.OrdinalIgnoreCase);
            var (l, r) = Unify(left, ParseMultiplicative());
            left = add ? Expression.Add(l, r) : Expression.Subtract(l, r);
        }
        return left;
    }

    private Expression ParseMultiplicative()
    {
        var left = ParsePrimary();
        while (PeekWord("mul") || PeekWord("div") || PeekWord("mod"))
        {
            var op = Next().Text.ToLowerInvariant();
            var (l, r) = Unify(left, ParsePrimary());
            // a constant zero divisor would fail at run time (a 500); report it as a bad query instead
            if (op != "mul" && r is ConstantExpression { Value: { } zero } && IsZero(zero)) throw new ODataException("Division by zero.");
            left = op == "mul" ? Expression.Multiply(l, r) : op == "div" ? Expression.Divide(l, r) : Expression.Modulo(l, r);
        }
        return left;
    }

    private static bool IsZero(object value) => value switch
    {
        int i => i == 0, long l => l == 0, decimal m => m == 0, double d => d == 0, float f => f == 0,
        short s => s == 0, byte b => b == 0, sbyte sb => sb == 0, ushort us => us == 0, uint ui => ui == 0, ulong ul => ul == 0,
        _ => false
    };

    private Expression ParsePrimary()
    {
        var token = Peek;
        switch (token.Kind)
        {
            case T.LParen:
                _pos++;
                var inner = ParseOr();
                Expect(T.RParen);
                return inner;
            case T.String:
                _pos++;
                return Expression.Constant(token.Text, typeof(string));
            case T.Number:
                _pos++;
                return Literal(token.Text);
            case T.Ident:
                _pos++;
                var lower = token.Text.ToLowerInvariant();
                if (lower == "true") return Expression.Constant(true);
                if (lower == "false") return Expression.Constant(false);
                if (lower == "null") return Expression.Constant(null, typeof(object));
                if (Peek.Kind == T.LParen && (lower.EndsWith("/any") || lower.EndsWith("/all"))) return ParseQuantifier(token.Text);
                if (Peek.Kind == T.LParen) return ParseFunction(lower);
                _pos--;
                return ParsePath();
            default:
                throw new ODataException($"Unexpected '{token.Text}' in the query expression.");
        }
    }

    /// <summary>Member path such as <c>Customer/Address/City</c> (case-insensitive, public instance members of the DTO only).</summary>
    private Expression ParsePath()
    {
        var token = Next();
        if (token.Kind != T.Ident) throw new ODataException($"A member name was expected, found '{token.Text}'.");
        Expression current = _it;
        var segments = token.Text.Split('/', '.');
        var skip = 0;
        if (_ranges.TryGetValue(segments[0], out var range)) { current = range; skip = 1; }
        foreach (var segment in segments.Skip(skip))
        {
            var member = (MemberInfo?)current.Type.GetProperty(segment, BindingFlags.Public | BindingFlags.Instance | BindingFlags.IgnoreCase) ??
                         current.Type.GetField(segment, BindingFlags.Public | BindingFlags.Instance | BindingFlags.IgnoreCase);
            if (member == null) throw new ODataException($"'{segment}' is not a member of {current.Type.Name}.");
            current = Expression.MakeMemberAccess(current, member);
        }
        return current;
    }

    /// <summary><c>Items/any(i: i/Price gt 5)</c>, <c>Items/all(i: ...)</c> and <c>Items/any()</c>.</summary>
    private Expression ParseQuantifier(string text)
    {
        var cut = text.LastIndexOf('/');
        var all = text[(cut + 1)..].Equals("all", StringComparison.OrdinalIgnoreCase);
        var collection = new ODataFilterParserPath(this).Resolve(text[..cut]);
        if (collection.Type == typeof(string) || !TryElementType(collection.Type, out var element))
            throw new ODataException($"'{text[..cut]}' is not a collection.");

        Expect(T.LParen);
        var method = all ? nameof(Enumerable.All) : nameof(Enumerable.Any);
        if (Peek.Kind == T.RParen)
        {
            if (all) throw new ODataException("all() needs a predicate.");
            _pos++;
            return Expression.Call(typeof(Enumerable), method, new[] { element }, collection);
        }

        var nameToken = Next();
        if (nameToken.Kind != T.Ident || Peek.Kind != T.Colon) throw new ODataException("Expected 'variable:' at the start of any()/all().");
        _pos++;
        var variable = Expression.Parameter(element, nameToken.Text);
        if (_ranges.ContainsKey(nameToken.Text)) throw new ODataException($"The range variable '{nameToken.Text}' is already in use.");
        _ranges[nameToken.Text] = variable;
        Expression body;
        try { body = AsBool(ParseOr()); }
        finally { _ranges.Remove(nameToken.Text); }
        Expect(T.RParen);
        return Expression.Call(typeof(Enumerable), method, new[] { element }, collection, Expression.Lambda(body, variable));
    }

    private static bool TryElementType(Type type, out Type element)
    {
        var enumerable = type.GetInterfaces().Concat(new[] { type })
            .FirstOrDefault(i => i.IsGenericType && i.GetGenericTypeDefinition() == typeof(IEnumerable<>));
        element = enumerable?.GetGenericArguments()[0]!;
        return enumerable != null;
    }

    // re-uses ParsePath for an already-split path string
    private readonly struct ODataFilterParserPath
    {
        private readonly ODataFilterParser _p;
        public ODataFilterParserPath(ODataFilterParser p) => _p = p;
        public Expression Resolve(string path)
        {
            var saved = (_p._tokens.ToList(), _p._pos);
            _p._tokens.Clear();
            _p._tokens.Add(new Token(T.Ident, path));
            _p._tokens.Add(new Token(T.End, ""));
            _p._pos = 0;
            try { return _p.ParsePath(); }
            finally { _p._tokens.Clear(); _p._tokens.AddRange(saved.Item1); _p._pos = saved.Item2; }
        }
    }

    /// <summary>OData <c>$search</c>: space separated terms are ANDed, <c>OR</c> / <c>NOT</c> and "quoted phrases" are honoured;
    /// a term matches when any top-level string property of the DTO contains it.</summary>
    public static LambdaExpression? ParseSearch(string text, Type itemType, ODataSettings settings)
    {
        if (text.Length > settings.MaxExpressionLength) throw new ODataException("The $search text is too long.");
        var it = Expression.Parameter(itemType, "it");
        var strings = itemType.GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Where(p => p.PropertyType == typeof(string) && p.GetIndexParameters().Length == 0).ToList();
        if (strings.Count == 0) throw new ODataException($"{itemType.Name} has no string properties to search.");

        Expression Term(string term)
        {
            Expression any = Expression.Constant(false);
            foreach (var p in strings)
            {
                var member = Expression.Property(it, p);
                var contains = Expression.AndAlso(Expression.NotEqual(member, Expression.Constant(null, typeof(string))),
                    Expression.Call(Expression.Call(member, nameof(string.ToLowerInvariant), null), typeof(string).GetMethod(nameof(string.Contains), new[] { typeof(string) })!,
                        Expression.Constant(term.ToLowerInvariant())));
                any = any is ConstantExpression ? contains : Expression.OrElse(any, contains);
            }
            return any;
        }

        var words = new List<string>();
        var i = 0;
        while (i < text.Length)
        {
            if (char.IsWhiteSpace(text[i])) { i++; continue; }
            if (text[i] == '"')
            {
                var end = text.IndexOf('"', i + 1);
                if (end < 0) throw new ODataException("Unterminated quote in $search.");
                words.Add("\"" + text[(i + 1)..end]);   // leading quote marks a phrase, so it is never read as OR/NOT
                i = end + 1;
            }
            else
            {
                var start = i;
                while (i < text.Length && !char.IsWhiteSpace(text[i])) i++;
                words.Add(text[start..i]);
            }
        }
        if (words.Count == 0) return null;
        if (words.Count > settings.MaxSearchTerms) throw new ODataException($"$search has more than {settings.MaxSearchTerms} words.");

        Expression? result = null;       // AND of OR-groups
        Expression? group = null;
        var negate = false;
        var orNext = false;
        void Flush()
        {
            if (group == null) return;
            result = result == null ? group : Expression.AndAlso(result, group);
            group = null;
        }
        foreach (var raw in words)
        {
            var isPhrase = raw.StartsWith('"');
            var word = isPhrase ? raw[1..] : raw;
            if (!isPhrase && word == "OR") { orNext = true; continue; }
            if (!isPhrase && word == "AND") continue;
            if (!isPhrase && word == "NOT") { negate = true; continue; }
            if (word.Length < settings.MinSearchTermLength) throw new ODataException($"$search words must have at least {settings.MinSearchTermLength} characters.");
            var e = Term(word);
            if (negate) { e = Expression.Not(e); negate = false; }
            if (orNext && group != null) group = Expression.OrElse(group, e);
            else { Flush(); group = e; }
            orNext = false;
        }
        Flush();
        return Expression.Lambda(result!, it);
    }

    // ------------------------------------------------------------------ literals and typing

    private static Expression Literal(string text)
    {
        var t = text.TrimEnd('m', 'M', 'l', 'L', 'd', 'D', 'f', 'F');
        if (long.TryParse(text, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var l))
            return Expression.Constant(l >= int.MinValue && l <= int.MaxValue ? (object)(int)l : l);
        if (Guid.TryParse(text, out var g)) return Expression.Constant(g);
        if (text.Length >= 10 && char.IsDigit(text[0]) && text.Contains('-') &&
            DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var dto))
            return Expression.Constant(dto);
        if (decimal.TryParse(t, NumberStyles.Float, CultureInfo.InvariantCulture, out var m) &&
            text.IndexOfAny(new[] { 'e', 'E', 'd', 'D', 'f', 'F' }) < 0)
            return Expression.Constant(m);
        if (double.TryParse(t, NumberStyles.Float, CultureInfo.InvariantCulture, out var d)) return Expression.Constant(d);
        throw new ODataException($"'{text}' is not a valid literal.");
    }

    /// <summary>Brings two operands to one type: literals adapt to the other side, numerics are widened, nullables lifted.</summary>
    private static (Expression, Expression) Unify(Expression l, Expression r)
    {
        if (IsNullLiteral(l)) l = Expression.Default(r.Type.IsValueType && Nullable.GetUnderlyingType(r.Type) == null ? typeof(Nullable<>).MakeGenericType(r.Type) : r.Type);
        if (IsNullLiteral(r)) r = Expression.Default(l.Type.IsValueType && Nullable.GetUnderlyingType(l.Type) == null ? typeof(Nullable<>).MakeGenericType(l.Type) : l.Type);
        if (l.Type == r.Type) return (l, r);

        if (r is ConstantExpression && l is not ConstantExpression) return (l, Coerce(r, l.Type));
        if (l is ConstantExpression && r is not ConstantExpression) return (Coerce(l, r.Type), r);

        var lt = Nullable.GetUnderlyingType(l.Type) ?? l.Type;
        var rt = Nullable.GetUnderlyingType(r.Type) ?? r.Type;
        var common = lt == rt ? lt : Rank(lt) > 0 && Rank(rt) > 0 ? (Rank(lt) >= Rank(rt) ? lt : rt) : throw new ODataException($"Cannot combine {l.Type.Name} and {r.Type.Name}.");
        if (l.Type.IsValueType && Nullable.GetUnderlyingType(l.Type) != null || r.Type.IsValueType && Nullable.GetUnderlyingType(r.Type) != null)
            common = typeof(Nullable<>).MakeGenericType(common);
        return (Expression.Convert(l, common), Expression.Convert(r, common));
    }

    private static bool IsNullLiteral(Expression e) => e is ConstantExpression { Value: null } && e.Type == typeof(object);

    private static int Rank(Type t) => Type.GetTypeCode(t) switch
    {
        TypeCode.Byte or TypeCode.SByte or TypeCode.Int16 or TypeCode.UInt16 => 1,
        TypeCode.Int32 or TypeCode.UInt32 => 2,
        TypeCode.Int64 or TypeCode.UInt64 => 3,
        TypeCode.Single => 4,
        TypeCode.Double => 5,
        TypeCode.Decimal => 6,
        _ => 0
    };

    private static Expression Coerce(Expression literal, Type target)
    {
        var value = ((ConstantExpression)literal).Value;
        var underlying = Nullable.GetUnderlyingType(target) ?? target;
        try
        {
            object? converted;
            if (value is null) converted = null;
            else if (underlying.IsEnum) converted = value is string name ? Enum.Parse(underlying, name, ignoreCase: true) : Enum.ToObject(underlying, value);
            else if (underlying == typeof(Guid)) converted = value is Guid ? value : Guid.Parse((string)value);
            else if (underlying == typeof(DateTime)) converted = value is DateTimeOffset o ? o.UtcDateTime : DateTime.Parse(value.ToString()!, CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal);
            else if (underlying == typeof(DateTimeOffset)) converted = value is DateTimeOffset ? value : DateTimeOffset.Parse(value.ToString()!, CultureInfo.InvariantCulture);
            else if (underlying == typeof(DateOnly)) converted = value is DateTimeOffset o2 ? DateOnly.FromDateTime(o2.UtcDateTime) : DateOnly.Parse(value.ToString()!, CultureInfo.InvariantCulture);
            else if (underlying == typeof(string)) converted = value.ToString();
            else converted = System.Convert.ChangeType(value, underlying, CultureInfo.InvariantCulture);
            return Expression.Constant(converted, target);
        }
        catch (Exception ex) when (ex is FormatException or InvalidCastException or OverflowException or ArgumentException)
        {
            throw new ODataException($"Cannot use the value '{value}' with a {underlying.Name} member.");
        }
    }

    // ------------------------------------------------------------------ functions (fixed list)

    private Expression ParseFunction(string name)
    {
        Expect(T.LParen);
        var args = new List<Expression>();
        if (Peek.Kind != T.RParen)
        {
            args.Add(ParseOr());
            while (Peek.Kind == T.Comma) { _pos++; args.Add(ParseOr()); }
        }
        Expect(T.RParen);

        Expression S(int i) => Require(args, i, typeof(string));
        switch (name)
        {
            case "contains": return Str(S(0), nameof(string.Contains), S(1));
            case "startswith": return Str(S(0), nameof(string.StartsWith), S(1));
            case "endswith": return Str(S(0), nameof(string.EndsWith), S(1));
            case "tolower": return Expression.Call(S(0), nameof(string.ToLowerInvariant), null);
            case "toupper": return Expression.Call(S(0), nameof(string.ToUpperInvariant), null);
            case "trim": return Expression.Call(S(0), nameof(string.Trim), null);
            case "length": return Expression.Property(S(0), nameof(string.Length));
            case "concat": return Expression.Call(typeof(string), nameof(string.Concat), null, S(0), S(1));
            case "indexof": return Expression.Call(S(0), nameof(string.IndexOf), null, S(1), Expression.Constant(StringComparison.Ordinal));
            case "substring":
                return args.Count == 3
                    ? Expression.Call(S(0), nameof(string.Substring), null, Require(args, 1, typeof(int)), Require(args, 2, typeof(int)))
                    : Expression.Call(S(0), nameof(string.Substring), null, Require(args, 1, typeof(int)));
            case "year": case "month": case "day": case "hour": case "minute": case "second":
                if (args.Count != 1) throw new ODataException($"{name}() takes one argument.");
                var target = args[0];
                var type = Nullable.GetUnderlyingType(target.Type) ?? target.Type;
                if (type != typeof(DateTime) && type != typeof(DateTimeOffset)) throw new ODataException($"{name}() needs a date value.");
                var member = char.ToUpperInvariant(name[0]) + name[1..];
                return target.Type == type
                    ? Expression.Property(target, member)
                    : Expression.Condition(Expression.NotEqual(target, Expression.Constant(null, target.Type)),
                        Expression.Convert(Expression.Property(Expression.Property(target, "Value"), member), typeof(int?)), Expression.Constant(null, typeof(int?)));
            case "round": case "floor": case "ceiling":
                if (args.Count != 1) throw new ODataException($"{name}() takes one argument.");
                var number = args[0];
                if (number.Type != typeof(double) && number.Type != typeof(decimal)) number = Expression.Convert(number, typeof(double));
                return Expression.Call(typeof(Math), name == "round" ? "Round" : name == "floor" ? "Floor" : "Ceiling", null, number);
            default:
                throw new ODataException($"The function '{name}' is not supported.");
        }
    }

    private static Expression Str(Expression target, string method, Expression arg) =>
        Expression.Call(target, typeof(string).GetMethod(method, new[] { typeof(string) })!, arg);

    private static Expression Require(List<Expression> args, int index, Type type)
    {
        if (index >= args.Count) throw new ODataException("Too few arguments for a function.");
        var a = args[index];
        if (a.Type == type) return a;
        if (a is ConstantExpression) return Coerce(a, type);
        throw new ODataException($"A {type.Name} argument was expected.");
    }
}
