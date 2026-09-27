using System;
using System.Collections.Generic;
using HeroGame.Core.Foundation;
using HeroGame.Core.Time;

namespace HeroGame.Core.Economy
{
    /// <summary>
    /// Who caused a transaction. Player and Admin transactions are journaled (they cannot be
    /// re-derived after a crash). Simulation transactions are deterministic results of world
    /// time passing; after a crash they are recomputed by re-simulating from the snapshot, so
    /// journaling them would double count.
    /// </summary>
    public enum TransactionSource
    {
        Player = 0,
        Admin = 1,
        Simulation = 2,
    }

    [Serializable]
    public struct OwnershipChange
    {
        public EntityId Asset;
        public EntityId From;
        public EntityId To;
    }

    /// <summary>
    /// An all-or-nothing change to critical server state: money movements plus ownership
    /// changes (GDD §173). Plain data so it can be written to the journal and replayed.
    /// </summary>
    [Serializable]
    public sealed class WorldTransaction
    {
        /// <summary>Assigned by the processor. Strictly increasing; used for journal replay.</summary>
        public long Sequence;
        /// <summary>Client/request supplied key. A key is only ever applied once (retries are safe).</summary>
        public string IdempotencyKey = "";
        public TransactionSource Source;
        public EntityId Initiator;
        public GameDateTime Timestamp;
        public string Description = "";
        public LedgerTransaction Money;
        public List<OwnershipChange> Ownership = new List<OwnershipChange>();
        /// <summary>Ledger accounts opened by this transaction (zero balance) before its money moves.</summary>
        public List<LedgerAccount> OpenAccounts = new List<LedgerAccount>();
        /// <summary>
        /// Records created together with the money (a loan, a policy, a new business). They ride in the journal
        /// with the payment, so a crash can never leave money moved without the record that explains it.
        /// </summary>
        public TransactionRecords Records;
    }

    /// <summary>Persistent records attached to a <see cref="WorldTransaction"/>; registered by the world on apply and replay.</summary>
    [Serializable]
    public sealed class TransactionRecords
    {
        public Loan Loan;
        public InsurancePolicy Policy;
        public Business.BusinessRecord Business;
        /// <summary>Goods handed over with the payment (a weapon, ammunition).</summary>
        public List<ItemGrant> Items;
        /// <summary>A license issued with its fee (a firearm permit).</summary>
        public LicenseGrant License;
    }

    [Serializable]
    public sealed class ItemGrant
    {
        public EntityId Character;
        public string ItemId = "";
        public int Quantity = 1;
    }

    [Serializable]
    public sealed class LicenseGrant
    {
        public EntityId Character;
        public Characters.License License;
    }

    /// <summary>Durable append-only log of committed transactions (write-ahead journal).</summary>
    public interface ITransactionJournal
    {
        /// <summary>Must be durable (flushed to disk) before returning.</summary>
        void Append(WorldTransaction tx);
        IEnumerable<WorldTransaction> ReadAfter(long sequence);
        /// <summary>Drops entries with Sequence ≤ upTo once a snapshot contains them.</summary>
        void Compact(long upTo);
    }

    /// <summary>In-memory journal for tests and Story Mode (which uses save slots instead).</summary>
    public sealed class MemoryTransactionJournal : ITransactionJournal
    {
        public readonly List<WorldTransaction> Entries = new List<WorldTransaction>();
        public Func<WorldTransaction, bool> FailAppend;

        public void Append(WorldTransaction tx)
        {
            if (FailAppend != null && FailAppend(tx)) throw new System.IO.IOException("Simulated journal failure.");
            Entries.Add(tx);
        }

        public IEnumerable<WorldTransaction> ReadAfter(long sequence)
        {
            foreach (var e in Entries) if (e.Sequence > sequence) yield return e;
        }

        public void Compact(long upTo) => Entries.RemoveAll(e => e.Sequence <= upTo);
    }

    /// <summary>
    /// Executes <see cref="WorldTransaction"/>s atomically against the ledger and ownership registry.
    /// Protocol: validate everything → append to journal (durable) → apply in memory.
    /// A crash before the append means nothing happened; a crash after it is repaired by
    /// <see cref="Replay"/> on start-up. There is no state in which money moved but the asset
    /// did not, or vice-versa (GDD §174).
    /// </summary>
    public sealed class TransactionProcessor
    {
        private const int IdempotencyWindow = 4096;

        private readonly Ledger _ledger;
        private readonly OwnershipRegistry _ownership;
        private readonly ITransactionJournal _journal;
        private readonly HashSet<string> _recentKeys = new HashSet<string>();
        private readonly Queue<string> _recentKeyOrder = new Queue<string>();

        public long LastSequence { get; private set; }

        public event Action<WorldTransaction> Committed;

        public TransactionProcessor(Ledger ledger, OwnershipRegistry ownership, ITransactionJournal journal, long lastSequence = 0)
        {
            _ledger = ledger ?? throw new ArgumentNullException(nameof(ledger));
            _ownership = ownership ?? throw new ArgumentNullException(nameof(ownership));
            _journal = journal ?? new MemoryTransactionJournal();
            LastSequence = lastSequence;
        }

        public OpResult Validate(WorldTransaction tx)
        {
            if (tx == null) return OpResult.Fail("Null transaction.");
            if (!string.IsNullOrEmpty(tx.IdempotencyKey) && _recentKeys.Contains(tx.IdempotencyKey))
                return OpResult.Fail("Duplicate request " + tx.IdempotencyKey + ".");
            if (tx.Money == null && (tx.Ownership == null || tx.Ownership.Count == 0) && tx.Records == null
                && (tx.OpenAccounts == null || tx.OpenAccounts.Count == 0)) return OpResult.Fail("Empty transaction.");
            if (tx.OpenAccounts != null)
            {
                var ids = new HashSet<EntityId>();
                foreach (var a in tx.OpenAccounts)
                {
                    if (a == null || a.Id.Kind != EntityKind.LedgerAccount) return OpResult.Fail("Invalid new account.");
                    if (_ledger.Exists(a.Id) || !ids.Add(a.Id)) return OpResult.Fail("Account already exists: " + a.Id);
                    if (a.BalanceCents != 0) return OpResult.Fail("New accounts open with a zero balance.");
                }
            }
            if (tx.Money != null)
            {
                var m = _ledger.Validate(tx.Money, tx.OpenAccounts);
                if (!m.Success) return m;
            }
            if (tx.Ownership != null)
            {
                var seen = new HashSet<EntityId>();
                foreach (var change in tx.Ownership)
                {
                    if (!seen.Add(change.Asset)) return OpResult.Fail("Asset listed twice: " + change.Asset);
                    var o = _ownership.ValidateTransfer(change.Asset, change.From, change.To);
                    if (!o.Success) return o;
                }
            }
            return OpResult.Ok();
        }

        public OpResult Execute(WorldTransaction tx)
        {
            var v = Validate(tx);
            if (!v.Success) return v;

            if (tx.Source == TransactionSource.Simulation)
            {
                tx.Sequence = 0;
                ApplyValidated(tx);
                Committed?.Invoke(tx);
                return OpResult.Ok();
            }

            tx.Sequence = LastSequence + 1;
            try
            {
                _journal.Append(tx);
            }
            catch (Exception ex)
            {
                // Nothing was applied; the request can be retried.
                tx.Sequence = 0;
                return OpResult.Fail("Journal unavailable: " + ex.Message);
            }

            LastSequence = tx.Sequence;
            ApplyValidated(tx);
            Committed?.Invoke(tx);
            return OpResult.Ok();
        }

        /// <summary>Re-applies journaled transactions newer than the loaded snapshot. Returns count applied.</summary>
        public int Replay(IEnumerable<WorldTransaction> entries)
        {
            var applied = 0;
            foreach (var tx in entries)
            {
                if (tx.Sequence <= LastSequence || tx.Source == TransactionSource.Simulation) continue;
                ApplyValidated(tx);
                LastSequence = tx.Sequence;
                applied++;
            }
            return applied;
        }

        /// <summary>Raised whenever a transaction is applied, live or during journal replay (used to register attached records).</summary>
        public event Action<WorldTransaction> Applied;

        private void ApplyValidated(WorldTransaction tx)
        {
            if (tx.OpenAccounts != null)
                foreach (var a in tx.OpenAccounts)
                    if (!_ledger.Exists(a.Id))
                        _ledger.Restore(new LedgerAccount { Id = a.Id, Owner = a.Owner, Kind = a.Kind, Label = a.Label, OverdraftLimitCents = a.OverdraftLimitCents });
            if (tx.Money != null) _ledger.ApplyUnchecked(tx.Money);
            if (tx.Ownership != null)
                foreach (var change in tx.Ownership) _ownership.SetOwnerUnchecked(change.Asset, change.To);
            RememberKey(tx.IdempotencyKey);
            Applied?.Invoke(tx);
        }

        private void RememberKey(string key)
        {
            if (string.IsNullOrEmpty(key) || !_recentKeys.Add(key)) return;
            _recentKeyOrder.Enqueue(key);
            while (_recentKeyOrder.Count > IdempotencyWindow) _recentKeys.Remove(_recentKeyOrder.Dequeue());
        }
    }
}
