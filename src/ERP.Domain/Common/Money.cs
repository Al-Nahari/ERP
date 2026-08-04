using System;
using System.Collections.Generic;
using ERP.SharedKernel;

namespace ERP.Domain.Common;

/// <summary>
/// Represents a monetary amount tied to a specific currency. Never use decimal/float
/// directly for money in the domain — this VO prevents accidental cross-currency
/// arithmetic (e.g. adding SAR to USD without conversion) at compile/run time and
/// centralizes rounding rules (banker's rounding to the currency's minor unit).
/// </summary>
public sealed class Money : ValueObject
{
    public decimal Amount { get; }
    public string CurrencyCode { get; } // ISO 4217, e.g. "SAR", "USD"

    private Money(decimal amount, string currencyCode)
    {
        Amount = amount;
        CurrencyCode = currencyCode;
    }

    public static Money Zero(string currencyCode) => new(0m, currencyCode);

    public static Result<Money> Create(decimal amount, string currencyCode)
    {
        if (string.IsNullOrWhiteSpace(currencyCode) || currencyCode.Length != 3)
            return Result.Failure<Money>("Currency code must be a valid ISO 4217 3-letter code.");

        var rounded = Math.Round(amount, 2, MidpointRounding.ToEven);
        return Result.Success(new Money(rounded, currencyCode.ToUpperInvariant()));
    }

    public Money Add(Money other)
    {
        EnsureSameCurrency(other);
        return new Money(Amount + other.Amount, CurrencyCode);
    }

    public Money Subtract(Money other)
    {
        EnsureSameCurrency(other);
        return new Money(Amount - other.Amount, CurrencyCode);
    }

    public Money Multiply(decimal factor) => new(Math.Round(Amount * factor, 2, MidpointRounding.ToEven), CurrencyCode);

    public bool IsZero => Amount == 0m;
    public bool IsPositive => Amount > 0m;
    public bool IsNegative => Amount < 0m;

    private void EnsureSameCurrency(Money other)
    {
        if (other.CurrencyCode != CurrencyCode)
            throw new InvalidOperationException(
                $"Cannot operate on Money with different currencies: {CurrencyCode} vs {other.CurrencyCode}. " +
                "Use ICurrencyExchangeService to convert first.");
    }

    protected override IEnumerable<object?> GetEqualityComponents()
    {
        yield return Amount;
        yield return CurrencyCode;
    }

    public override string ToString() => $"{Amount:N2} {CurrencyCode}";
}
