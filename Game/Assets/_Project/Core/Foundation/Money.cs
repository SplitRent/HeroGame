using System;
using System.Globalization;

namespace HeroGame.Core.Foundation
{
    /// <summary>
    /// Currency amount stored as integer cents. Floating point is never used for money
    /// (TDD §8.1) so balances are exact and replay-safe.
    /// </summary>
    [Serializable]
    public readonly struct Money : IEquatable<Money>, IComparable<Money>
    {
        public static readonly Money Zero = default;

        public readonly long Cents;

        public Money(long cents)
        {
            Cents = cents;
        }

        public static Money FromDollars(long dollars) => new Money(checked(dollars * 100));

        public static Money FromDollars(double dollars) => new Money((long)Math.Round(dollars * 100.0, MidpointRounding.AwayFromZero));

        public double Dollars => Cents / 100.0;
        public bool IsNegative => Cents < 0;
        public bool IsZero => Cents == 0;

        /// <summary>Multiplies by a factor with banker-safe rounding (away from zero).</summary>
        public Money Scale(double factor) => new Money((long)Math.Round(Cents * factor, MidpointRounding.AwayFromZero));

        public static Money operator +(Money a, Money b) => new Money(checked(a.Cents + b.Cents));
        public static Money operator -(Money a, Money b) => new Money(checked(a.Cents - b.Cents));
        public static Money operator -(Money a) => new Money(checked(-a.Cents));
        public static Money operator *(Money a, long n) => new Money(checked(a.Cents * n));
        public static bool operator <(Money a, Money b) => a.Cents < b.Cents;
        public static bool operator >(Money a, Money b) => a.Cents > b.Cents;
        public static bool operator <=(Money a, Money b) => a.Cents <= b.Cents;
        public static bool operator >=(Money a, Money b) => a.Cents >= b.Cents;
        public static bool operator ==(Money a, Money b) => a.Cents == b.Cents;
        public static bool operator !=(Money a, Money b) => a.Cents != b.Cents;

        public static Money Max(Money a, Money b) => a.Cents >= b.Cents ? a : b;
        public static Money Min(Money a, Money b) => a.Cents <= b.Cents ? a : b;

        public bool Equals(Money other) => Cents == other.Cents;
        public override bool Equals(object obj) => obj is Money other && Equals(other);
        public override int GetHashCode() => Cents.GetHashCode();
        public int CompareTo(Money other) => Cents.CompareTo(other.Cents);

        public override string ToString()
        {
            var abs = Math.Abs((decimal)Cents) / 100m;
            var text = abs.ToString("#,##0.00", CultureInfo.InvariantCulture);
            return (Cents < 0 ? "-$" : "$") + text;
        }
    }
}
