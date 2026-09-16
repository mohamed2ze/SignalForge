using System;

namespace SignalForge.Domain.ValueObjects;

public readonly struct TenantId : IEquatable<TenantId>
{
    public Guid Value { get; }

    public TenantId(Guid value)
    {
        Value = value;
    }

    public static TenantId New() => new TenantId(Guid.NewGuid());

    public static TenantId Empty() => new TenantId(Guid.Empty);

    public bool IsEmpty() => Value == Guid.Empty;

    public override string ToString() => Value.ToString();

    public static implicit operator Guid(TenantId id) => id.Value;
    public static explicit operator TenantId(Guid guid) => new TenantId(guid);

    public bool Equals(TenantId other) => Value.Equals(other.Value);
    public override bool Equals(object? obj) => obj is TenantId other && Equals(other);
    public override int GetHashCode() => Value.GetHashCode();
    public static bool operator ==(TenantId left, TenantId right) => left.Equals(right);
    public static bool operator !=(TenantId left, TenantId right) => !left.Equals(right);
}