using System;

namespace SignalForge.Domain.ValueObjects;

/// <summary>
/// Strongly-typed ID for an API key.
/// </summary>
public readonly struct ApiKeyId : IEquatable<ApiKeyId>
{
    private readonly Guid _value;

    public ApiKeyId(Guid value)
    {
        _value = value;
    }

    public Guid Value => _value;

    public static ApiKeyId Create(Guid value) => new ApiKeyId(value);

    public static ApiKeyId Empty => new ApiKeyId(Guid.Empty);

    public bool IsEmpty => _value == Guid.Empty;

    public override string ToString() => _value.ToString();

    public static ApiKeyId Parse(string s)
    {
        if (Guid.TryParse(s, out var guid))
        {
            return new ApiKeyId(guid);
        }

        throw new FormatException($"Invalid ApiKeyId format: {s}");
    }

    public bool Equals(ApiKeyId other) => _value == other._value;

    public override bool Equals(object? obj) => obj is ApiKeyId other && Equals(other);

    public override int GetHashCode() => _value.GetHashCode();

    public static bool operator ==(ApiKeyId left, ApiKeyId right) => left.Equals(right);

    public static bool operator !=(ApiKeyId left, ApiKeyId right) => !left.Equals(right);
}