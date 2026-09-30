namespace Cida.Core;

/// <summary>
/// A JSON document Cida builds, stores or shows: request bodies, the <c>headers</c> and
/// <c>body</c> fields of the model configuration, and the command line's <c>--json</c> output.
/// Objects keep the order their members were added in, so a request reads
/// <c>model</c>, <c>stream</c>, <c>messages</c> the way Cida wrote it; objects parsed from
/// text order their members by key. Ported from upstream JSONValue.swift.
/// </summary>
public sealed record JsonValue
{
    public static readonly JsonValue Null = new(Kind.Null, null);

    public enum Kind { Null, Bool, Integer, Number, String, Array, Object }

    public JsonValue(Kind kind, object? value)
    {
        TokenType = kind;
        Value = value;
    }

    public Kind TokenType { get; }
    public object? Value { get; }

    // Records would compare Value by reference: arrays and objects need content equality.
    public bool Equals(JsonValue? other)
    {
        if (other == null || TokenType != other.TokenType) return false;
        return TokenType switch
        {
            Kind.Null => true,
            Kind.Bool => Equals(Value, other.Value),
            Kind.Integer => Equals(Value, other.Value),
            Kind.Number => Equals((double)Value!, (double)other.Value!),
            Kind.String => Equals(Value, other.Value),
            Kind.Array => SequenceEqual((IReadOnlyList<JsonValue>)Value!, (IReadOnlyList<JsonValue>)other.Value!),
            Kind.Object => ObjectValue!.Equals(other.ObjectValue),
            _ => false,
        };
    }

    private static bool SequenceEqual(IReadOnlyList<JsonValue> left, IReadOnlyList<JsonValue> right)
    {
        if (left.Count != right.Count) return false;
        for (var index = 0; index < left.Count; index++)
        {
            if (!left[index].Equals(right[index])) return false;
        }
        return true;
    }

    public override int GetHashCode() => TokenType switch
    {
        Kind.Null => 0,
        Kind.Bool or Kind.Integer => HashCode.Combine(TokenType, Value),
        Kind.Number => HashCode.Combine(TokenType, (double)Value!),
        Kind.String => HashCode.Combine(TokenType, (string)Value!),
        Kind.Array => ((IReadOnlyList<JsonValue>)Value!).Count,
        Kind.Object => ObjectValue!.GetHashCode(),
        _ => 0,
    };

    public static JsonValue Bool(bool value) => new(Kind.Bool, value);
    public static JsonValue Integer(long value) => new(Kind.Integer, value);
    public static JsonValue Number(double value) => new(Kind.Number, value);
    public static JsonValue String(string value) => new(Kind.String, value);
    public static JsonValue Array(IReadOnlyList<JsonValue> value) => new(Kind.Array, value);
    public static JsonValue Object(JsonObject value) => new(Kind.Object, value);

    public JsonObject? ObjectValue => Value as JsonObject;
    public string? StringValue => Value as string;
    public IReadOnlyList<JsonValue>? ArrayValue => Value as IReadOnlyList<JsonValue>;

    public int? IntValue => TokenType switch
    {
        Kind.Integer => checked((int)(long)Value!),
        Kind.Number => Convert.ToInt32((double)Value!),
        _ => null,
    };

    public JsonValue? this[string key] => ObjectValue?[key];

    // MARK: Parsing

    /// <summary>Parses JSON text; null when the text is not JSON.</summary>
    public static JsonValue? Parse(string text) => Parse(System.Text.Encoding.UTF8.GetBytes(text));

    public static JsonValue? Parse(byte[] data)
    {
        try
        {
            using var document = System.Text.Json.JsonDocument.Parse(data);
            return FromJsonElement(document.RootElement);
        }
        catch (System.Text.Json.JsonException)
        {
            return null;
        }
    }

    private static JsonValue? FromJsonElement(System.Text.Json.JsonElement element)
    {
        switch (element.ValueKind)
        {
            case System.Text.Json.JsonValueKind.Null:
            case System.Text.Json.JsonValueKind.Undefined:
                return Null;
            case System.Text.Json.JsonValueKind.True:
                return Bool(true);
            case System.Text.Json.JsonValueKind.False:
                return Bool(false);
            case System.Text.Json.JsonValueKind.String:
                return String(element.GetString() ?? "");
            case System.Text.Json.JsonValueKind.Number:
                if (element.TryGetInt64(out var integer))
                {
                    return Integer(integer);
                }
                return Number(element.GetDouble());
            case System.Text.Json.JsonValueKind.Array:
            {
                var values = new List<JsonValue>();
                foreach (var item in element.EnumerateArray())
                {
                    var value = FromJsonElement(item);
                    if (value == null) return null;
                    values.Add(value);
                }
                return Array(values);
            }
            case System.Text.Json.JsonValueKind.Object:
            {
                // Objects parsed from text order their members by key.
                var result = new JsonObject();
                foreach (var member in element.EnumerateObject().OrderBy(m => m.Name, StringComparer.Ordinal))
                {
                    var value = FromJsonElement(member.Value);
                    if (value == null) return null;
                    result[member.Name] = value;
                }
                return Object(result);
            }
            default:
                return null;
        }
    }

    // MARK: Writing

    /// <summary>Compact JSON for the wire and for storage.</summary>
    public string CompactText => Write(",", ":");

    /// <summary>One line with a space after every <c>,</c> and <c>:</c>, the way the command line shows a request.</summary>
    public string DisplayText => Write(", ", ": ");

    private string Write(string memberSeparator, string keySeparator)
    {
        var output = new System.Text.StringBuilder();
        WriteTo(output, memberSeparator, keySeparator);
        return output.ToString();
    }

    private void WriteTo(System.Text.StringBuilder output, string memberSeparator, string keySeparator)
    {
        switch (TokenType)
        {
            case Kind.Null:
                output.Append("null");
                break;
            case Kind.Bool:
                output.Append((bool)Value! ? "true" : "false");
                break;
            case Kind.Integer:
                output.Append((long)Value!);
                break;
            case Kind.Number:
            {
                var number = (double)Value!;
                output.Append(double.IsFinite(number) ? number.ToString("R", System.Globalization.CultureInfo.InvariantCulture) : "null");
                break;
            }
            case Kind.String:
                WriteString((string)Value!, output);
                break;
            case Kind.Array:
            {
                output.Append('[');
                var first = true;
                foreach (var value in (IReadOnlyList<JsonValue>)Value!)
                {
                    if (!first) output.Append(memberSeparator);
                    first = false;
                    value.WriteTo(output, memberSeparator, keySeparator);
                }
                output.Append(']');
                break;
            }
            case Kind.Object:
            {
                output.Append('{');
                var first = true;
                foreach (var member in ((JsonObject)Value!).Members)
                {
                    if (!first) output.Append(memberSeparator);
                    first = false;
                    WriteString(member.Key, output);
                    output.Append(keySeparator);
                    member.Value.WriteTo(output, memberSeparator, keySeparator);
                }
                output.Append('}');
                break;
            }
        }
    }

    private static void WriteString(string value, System.Text.StringBuilder output)
    {
        output.Append('"');
        foreach (var scalar in value)
        {
            switch (scalar)
            {
                case '"':
                    output.Append("\\\"");
                    break;
                case '\\':
                    output.Append("\\\\");
                    break;
                case '\n':
                    output.Append("\\n");
                    break;
                case '\r':
                    output.Append("\\r");
                    break;
                case '\t':
                    output.Append("\\t");
                    break;
                case '\b':
                    output.Append("\\b");
                    break;
                case '\f':
                    output.Append("\\f");
                    break;
                default:
                    if (scalar < 0x20)
                    {
                        output.Append("\\u").Append(((int)scalar).ToString("x4"));
                    }
                    else
                    {
                        output.Append(scalar);
                    }
                    break;
            }
        }
        output.Append('"');
    }

    // MARK: Merging

    /// <summary>
    /// <paramref name="overrideValue"/> laid over this value: objects merge member by member
    /// and recursively, a <c>null</c> member removes the key, and anything else replaces what
    /// was there.
    /// </summary>
    public JsonValue Merging(JsonValue overrideValue)
    {
        if (TokenType != Kind.Object || overrideValue.TokenType != Kind.Object)
        {
            return overrideValue;
        }
        var @base = new JsonObject(
            ((JsonObject)Value!).Members.Select(m => new JsonObject.Member(m.Key, m.Value)).ToList());
        foreach (var member in ((JsonObject)overrideValue.Value!).Members)
        {
            if (member.Value.TokenType == Kind.Null)
            {
                @base[member.Key] = null;
            }
            else if (@base[member.Key] is { } existing)
            {
                @base[member.Key] = existing.Merging(member.Value);
            }
            else
            {
                @base[member.Key] = member.Value;
            }
        }
        return Object(@base);
    }
}

/// <summary>
/// A JSON object whose members keep the order they were added in. Two objects are equal when
/// they hold the same members, in any order.
/// </summary>
public sealed class JsonObject : IEquatable<JsonObject>
{
    public sealed record Member(string Key, JsonValue Value);

    public IReadOnlyList<Member> Members => _members;

    private readonly List<Member> _members = [];

    public JsonObject(IReadOnlyList<Member> members)
    {
        foreach (var member in members)
        {
            this[member.Key] = member.Value;
        }
    }

    public JsonObject() {}

    public bool IsEmpty => _members.Count == 0;
    public IReadOnlyList<string> Keys => _members.Select(m => m.Key).ToList();

    public JsonValue? this[string key]
    {
        get => _members.FirstOrDefault(m => m.Key == key)?.Value;
        set
        {
            var index = _members.FindIndex(m => m.Key == key);
            if (index >= 0)
            {
                if (value != null)
                {
                    _members[index] = new Member(key, value);
                }
                else
                {
                    _members.RemoveAt(index);
                }
            }
            else if (value != null)
            {
                _members.Add(new Member(key, value));
            }
        }
    }

    /// <summary>The same members ordered by key, for fingerprints that must not depend on order.</summary>
    public JsonObject SortedByKey()
    {
        var sorted = new JsonObject();
        foreach (var member in _members.OrderBy(m => m.Key, StringComparer.Ordinal))
        {
            sorted[member.Key] = SortValue(member.Value);
        }
        return sorted;
    }

    private static JsonValue SortValue(JsonValue value)
    {
        switch (value.TokenType)
        {
            case JsonValue.Kind.Object:
                return JsonValue.Object(value.ObjectValue!.SortedByKey());
            case JsonValue.Kind.Array:
                return JsonValue.Array(value.ArrayValue!.Select(SortValue).ToList());
            default:
                return value;
        }
    }

    public bool Equals(JsonObject? other)
    {
        if (other == null || _members.Count != other._members.Count) return false;
        return _members.All(member => other[member.Key] == member.Value);
    }

    public override bool Equals(object? obj) => Equals(obj as JsonObject);

    public override int GetHashCode() =>
        _members.Aggregate(17, (hash, member) => hash * 31 + member.Key.GetHashCode() * 31 + member.Value.GetHashCode());
}
