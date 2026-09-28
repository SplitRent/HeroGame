using System;
using System.Collections.Generic;

namespace HeroGame.Core.Economy
{
    using HeroGame.Core.Foundation;

    public enum LedgerAccountKind
    {
        PersonalChecking = 0,
        PersonalSavings = 1,
        Business = 2,
        Government = 3,
        Bank = 4,
        Organization = 5,
        Escrow = 6,
        /// <summary>
        /// The world outside the simulated city economy (imports, exports, tourism, federal money).
        /// The only account allowed to go arbitrarily negative; its negated balance is the net
        /// money that has entered the city, which makes currency generation auditable (GDD §175).
        /// </summary>
        External = 7,
    }

    /// <summary>Why money moved. Persisted; append only.</summary>
    public enum TransactionReason
    {
        Unspecified = 0,
        Wage = 1,
        Purchase = 2,
        Sale = 3,
        Tax = 4,
        Rent = 5,
        LoanDisbursement = 6,
        LoanRepayment = 7,
        Interest = 8,
        BusinessRevenue = 9,
        SupplyPurchase = 10,
        Payroll = 11,
        Fine = 12,
        Insurance = 13,
        CrimeProceeds = 14,
        AdminGrant = 15,
        PlayerTransfer = 16,
        Utilities = 17,
        Maintenance = 18,
        StartingFunds = 19,
        WorldGeneration = 20,
        Bail = 21,
        Fee = 22,
        Reversal = 23,
        Refund = 24,
        Deposit = 25,
        Payout = 26,
        Premium = 27,
    }

    [Serializable]
    public sealed class LedgerAccount
    {
        public EntityId Id;
        public EntityId Owner;
        public LedgerAccountKind Kind;
        public string Label = "";
        public long BalanceCents;
        /// <summary>Overdraft allowed down to -OverdraftLimitCents (0 = none).</summary>
        public long OverdraftLimitCents;
        public bool Frozen;

        public Money Balance => new Money(BalanceCents);
    }

    /// <summary>One line of a double-entry transaction. Positive = credit (money in), negative = debit.</summary>
    [Serializable]
    public struct Posting
    {
        public EntityId Account;
        public long AmountCents;

        public Posting(EntityId account, long amountCents)
        {
            Account = account;
            AmountCents = amountCents;
        }
    }

    /// <summary>A balanced set of postings (sum == 0). Plain data so it can be journaled and replayed.</summary>
    [Serializable]
    public sealed class LedgerTransaction
    {
        public TransactionReason Reason;
        public string Memo = "";
        public List<Posting> Postings = new List<Posting>();

        public static LedgerTransaction Transfer(EntityId from, EntityId to, Money amount, TransactionReason reason, string memo = "")
        {
            var tx = new LedgerTransaction { Reason = reason, Memo = memo ?? "" };
            tx.Postings.Add(new Posting(from, -amount.Cents));
            tx.Postings.Add(new Posting(to, amount.Cents));
            return tx;
        }

        public LedgerTransaction Add(EntityId account, long amountCents)
        {
            Postings.Add(new Posting(account, amountCents));
            return this;
        }
    }

    /// <summary>
    /// Authoritative double-entry ledger. Every transaction balances to zero, so the sum of all
    /// balances is always zero — any duplication bug shows up as a broken invariant
    /// (<see cref="VerifyInvariant"/>), which the server checks on every save.
    /// </summary>
    public sealed class Ledger
    {
        private readonly Dictionary<EntityId, LedgerAccount> _accounts = new Dictionary<EntityId, LedgerAccount>();

        public event Action<LedgerTransaction> Committed;

        public IEnumerable<LedgerAccount> Accounts => _accounts.Values;
        public int AccountCount => _accounts.Count;

        public LedgerAccount Open(EntityId id, EntityId owner, LedgerAccountKind kind, string label, long overdraftLimitCents = 0)
        {
            if (id.Kind != EntityKind.LedgerAccount) throw new ArgumentException("Ledger account ids must be of kind LedgerAccount.");
            if (_accounts.ContainsKey(id)) throw new InvalidOperationException("Account already exists: " + id);
            var account = new LedgerAccount { Id = id, Owner = owner, Kind = kind, Label = label ?? "", OverdraftLimitCents = overdraftLimitCents };
            _accounts.Add(id, account);
            return account;
        }

        /// <summary>Restores an account from a save. Balances are trusted only because the invariant is re-verified after load.</summary>
        public void Restore(LedgerAccount account)
        {
            _accounts[account.Id] = account;
        }

        public bool TryGet(EntityId id, out LedgerAccount account) => _accounts.TryGetValue(id, out account);

        public LedgerAccount Get(EntityId id)
        {
            if (!_accounts.TryGetValue(id, out var account)) throw new KeyNotFoundException("Unknown ledger account " + id);
            return account;
        }

        public Money BalanceOf(EntityId id) => Get(id).Balance;

        /// <summary>Checks every rule without mutating anything.</summary>
        public OpResult Validate(LedgerTransaction tx) => Validate(tx, null);

        /// <summary>Validates as if <paramref name="pending"/> accounts (opened by the same transaction) already existed.</summary>
        public OpResult Validate(LedgerTransaction tx, IReadOnlyList<LedgerAccount> pending)
        {
            if (tx == null || tx.Postings == null || tx.Postings.Count < 2) return OpResult.Fail("Transaction needs at least two postings.");
            long sum = 0;
            // Net change per account (an account may appear more than once).
            var net = new Dictionary<EntityId, long>();
            foreach (var p in tx.Postings)
            {
                if (!TryFind(p.Account, pending, out var acc)) return OpResult.Fail("Unknown account " + p.Account);
                if (acc.Frozen) return OpResult.Fail("Account frozen: " + p.Account);
                try
                {
                    sum = checked(sum + p.AmountCents);
                    net.TryGetValue(p.Account, out var n);
                    net[p.Account] = checked(n + p.AmountCents);
                }
                catch (OverflowException)
                {
                    return OpResult.Fail("Amount overflow.");
                }
            }
            if (sum != 0) return OpResult.Fail("Postings do not balance (sum " + sum + ").");
            foreach (var kv in net)
            {
                TryFind(kv.Key, pending, out var acc);
                if (acc.Kind == LedgerAccountKind.External || kv.Value >= 0) continue;
                long after;
                try { after = checked(acc.BalanceCents + kv.Value); }
                catch (OverflowException) { return OpResult.Fail("Balance overflow."); }
                if (after < -acc.OverdraftLimitCents) return OpResult.Fail("Insufficient funds in " + acc.Label + " (" + acc.Id + ").");
            }
            return OpResult.Ok();
        }

        private bool TryFind(EntityId id, IReadOnlyList<LedgerAccount> pending, out LedgerAccount account)
        {
            if (_accounts.TryGetValue(id, out account)) return true;
            if (pending != null)
                foreach (var a in pending)
                    if (a.Id == id) { account = a; return true; }
            return false;
        }

        public bool Exists(EntityId id) => _accounts.ContainsKey(id);

        public OpResult Commit(LedgerTransaction tx)
        {
            var v = Validate(tx);
            if (!v.Success) return v;
            ApplyUnchecked(tx);
            Committed?.Invoke(tx);
            return v;
        }

        /// <summary>Applies a transaction that has already been validated (used by the atomic processor and journal replay).</summary>
        internal void ApplyUnchecked(LedgerTransaction tx)
        {
            foreach (var p in tx.Postings) _accounts[p.Account].BalanceCents += p.AmountCents;
        }

        /// <summary>Undo of <see cref="ApplyUnchecked"/> for in-memory rollback.</summary>
        internal void RevertUnchecked(LedgerTransaction tx)
        {
            foreach (var p in tx.Postings) _accounts[p.Account].BalanceCents -= p.AmountCents;
        }

        /// <summary>Sum of all balances. Must always be zero.</summary>
        public bool VerifyInvariant(out long sum)
        {
            sum = 0;
            foreach (var a in _accounts.Values) sum += a.BalanceCents;
            return sum == 0;
        }

        /// <summary>Net money that entered the city from outside (negated External balances).</summary>
        public Money NetExternalInflow()
        {
            long total = 0;
            foreach (var a in _accounts.Values) if (a.Kind == LedgerAccountKind.External) total -= a.BalanceCents;
            return new Money(total);
        }
    }
}
