using System.Collections.Generic;
using System.Linq;

namespace ERP.SharedKernel;

/// <summary>
/// Base class for Value Objects per DDD: objects defined entirely by their
/// attributes, immutable, with structural equality. Used throughout the domain
/// for concepts like Money, AccountCode, TaxRate, Address — anything that has
/// no identity of its own and should never be mutated after construction.
/// </summary>
public abstract class ValueObject
{
    protected abstract IEnumerable<object?> GetEqualityComponents();

    public override bool Equals(object? obj)
    {
        if (obj is null || obj.GetType() != GetType()) return false;
        var other = (ValueObject)obj;
        return GetEqualityComponents().SequenceEqual(other.GetEqualityComponents());
    }

    public override int GetHashCode() =>
        GetEqualityComponents()
            .Select(x => x?.GetHashCode() ?? 0)
            .Aggregate((x, y) => x ^ y);

    public static bool operator ==(ValueObject? left, ValueObject? right) =>
        left is null ? right is null : left.Equals(right);

    public static bool operator !=(ValueObject? left, ValueObject? right) => !(left == right);
}

/// <summary>
/// Marker base for Aggregate Roots — the only entities that repositories operate on
/// directly. Child entities within the aggregate boundary (e.g. JournalLine inside
/// JournalEntry) are only ever reachable through the root, enforcing invariants
/// (e.g. "an entry must always be balanced") at the single point of entry.
/// </summary>
public abstract class AggregateRoot : BaseEntity
{
}

/// <summary>
/// Standard operation-result wrapper avoiding exceptions for expected business-rule
/// failures (e.g. "cannot post an unbalanced journal entry"). Exceptions are
/// reserved for truly exceptional/programmer-error conditions.
/// </summary>
public class Result
{
    public bool IsSuccess { get; }
    public bool IsFailure => !IsSuccess;
    public string[] Errors { get; }

    protected Result(bool isSuccess, string[] errors)
    {
        IsSuccess = isSuccess;
        Errors = errors;
    }

    public static Result Success() => new(true, System.Array.Empty<string>());
    public static Result Failure(params string[] errors) => new(false, errors);

    public static Result<T> Success<T>(T value) => new(value, true, System.Array.Empty<string>());
    public static Result<T> Failure<T>(params string[] errors) => new(default!, false, errors);
}

/// <summary>Typed variant of <see cref="Result"/> carrying a return value on success.</summary>
public class Result<T> : Result
{
    public T Value { get; }

    protected internal Result(T value, bool isSuccess, string[] errors) : base(isSuccess, errors)
    {
        Value = value;
    }
}
