using NUnit.Framework;

namespace HeroGame.Tests
{
    using HeroGame.Core.Economy;
    using HeroGame.Core.Foundation;
    using HeroGame.Core.Time;

    public class EconomyTests
    {
        private IdAllocator _ids;
        private Ledger _ledger;
        private OwnershipRegistry _ownership;
        private MemoryTransactionJournal _journal;
        private TransactionProcessor _processor;
        private EntityId _external, _alice, _bob, _house, _aliceId, _bobId;

        [SetUp]
        public void SetUp()
        {
            _ids = new IdAllocator();
            _ledger = new Ledger();
            _ownership = new OwnershipRegistry();
            _journal = new MemoryTransactionJournal();
            _processor = new TransactionProcessor(_ledger, _ownership, _journal);
            _external = _ids.Next(EntityKind.LedgerAccount);
            _ledger.Open(_external, EntityId.None, LedgerAccountKind.External, "outside");
            _aliceId = _ids.Next(EntityKind.Character);
            _bobId = _ids.Next(EntityKind.Character);
            _alice = _ids.Next(EntityKind.LedgerAccount);
            _bob = _ids.Next(EntityKind.LedgerAccount);
            _ledger.Open(_alice, _aliceId, LedgerAccountKind.PersonalChecking, "alice");
            _ledger.Open(_bob, _bobId, LedgerAccountKind.PersonalChecking, "bob");
            _house = _ids.Next(EntityKind.Property);
            _ownership.AssignInitial(_house, _bobId);
            Assert.IsTrue(_ledger.Commit(LedgerTransaction.Transfer(_external, _alice, Money.FromDollars(1000L), TransactionReason.StartingFunds)).Success);
        }

        [Test]
        public void Ledger_RejectsUnbalancedAndOverdrawnTransactions()
        {
            var unbalanced = new LedgerTransaction().Add(_alice, -100).Add(_bob, 50);
            Assert.IsFalse(_ledger.Commit(unbalanced).Success);
            Assert.IsFalse(_ledger.Commit(LedgerTransaction.Transfer(_alice, _bob, Money.FromDollars(5000L), TransactionReason.PlayerTransfer)).Success);
            Assert.AreEqual(Money.FromDollars(1000L), _ledger.BalanceOf(_alice));
            Assert.IsTrue(_ledger.VerifyInvariant(out var sum));
            Assert.AreEqual(0L, sum);
        }

        [Test]
        public void Ledger_ExternalAccountTracksNetInflow()
        {
            Assert.AreEqual(Money.FromDollars(1000L), _ledger.NetExternalInflow());
        }

        [Test]
        public void Purchase_IsAtomic_MoneyAndOwnershipMoveTogether()
        {
            var tx = Purchase(Money.FromDollars(800L), "buy-1");
            Assert.IsTrue(_processor.Execute(tx).Success);
            Assert.AreEqual(_aliceId, _ownership.OwnerOf(_house));
            Assert.AreEqual(Money.FromDollars(200L), _ledger.BalanceOf(_alice));
            Assert.AreEqual(Money.FromDollars(800L), _ledger.BalanceOf(_bob));
            Assert.AreEqual(1, _journal.Entries.Count);
        }

        [Test]
        public void Purchase_FailsCleanly_WhenOwnershipIsStale()
        {
            // Bob sold the house to someone else in the meantime.
            _ownership.AssignInitial(_house, _ids.Next(EntityKind.Character));
            var result = _processor.Execute(Purchase(Money.FromDollars(800L), "buy-2"));
            Assert.IsFalse(result.Success);
            Assert.AreEqual(Money.FromDollars(1000L), _ledger.BalanceOf(_alice), "money must not move when the asset cannot");
            Assert.AreEqual(0, _journal.Entries.Count);
        }

        [Test]
        public void Purchase_FailsCleanly_WhenBuyerCannotPay()
        {
            var result = _processor.Execute(Purchase(Money.FromDollars(5000L), "buy-3"));
            Assert.IsFalse(result.Success);
            Assert.AreEqual(_bobId, _ownership.OwnerOf(_house), "asset must not move when money cannot");
        }

        [Test]
        public void IdempotencyKey_PreventsDoubleApplication()
        {
            Assert.IsTrue(_processor.Execute(Transfer(100, "tip-1")).Success);
            Assert.IsFalse(_processor.Execute(Transfer(100, "tip-1")).Success);
            Assert.AreEqual(Money.FromDollars(900L), _ledger.BalanceOf(_alice));
        }

        [Test]
        public void JournalFailure_AppliesNothing()
        {
            _journal.FailAppend = _ => true;
            Assert.IsFalse(_processor.Execute(Purchase(Money.FromDollars(800L), "buy-4")).Success);
            Assert.AreEqual(_bobId, _ownership.OwnerOf(_house));
            Assert.AreEqual(Money.FromDollars(1000L), _ledger.BalanceOf(_alice));
            Assert.AreEqual(0L, _processor.LastSequence);
        }

        [Test]
        public void Replay_RecoversCommittedTransactionsAfterCrash()
        {
            // Snapshot state before the purchase.
            var snapshotAlice = _ledger.Get(_alice).BalanceCents;
            var snapshotBob = _ledger.Get(_bob).BalanceCents;
            var snapshotExternal = _ledger.Get(_external).BalanceCents;
            Assert.IsTrue(_processor.Execute(Purchase(Money.FromDollars(800L), "buy-5")).Success);

            // "Crash": rebuild from the snapshot and replay the journal.
            var ledger = new Ledger();
            ledger.Restore(new LedgerAccount { Id = _external, Kind = LedgerAccountKind.External, BalanceCents = snapshotExternal });
            ledger.Restore(new LedgerAccount { Id = _alice, Owner = _aliceId, Kind = LedgerAccountKind.PersonalChecking, BalanceCents = snapshotAlice });
            ledger.Restore(new LedgerAccount { Id = _bob, Owner = _bobId, Kind = LedgerAccountKind.PersonalChecking, BalanceCents = snapshotBob });
            var ownership = new OwnershipRegistry();
            ownership.AssignInitial(_house, _bobId);
            var recovered = new TransactionProcessor(ledger, ownership, new MemoryTransactionJournal());
            Assert.AreEqual(1, recovered.Replay(_journal.ReadAfter(0)));
            Assert.AreEqual(_aliceId, ownership.OwnerOf(_house));
            Assert.AreEqual(Money.FromDollars(200L), ledger.BalanceOf(_alice));
            Assert.IsTrue(ledger.VerifyInvariant(out _));
            // Replaying again is a no-op.
            Assert.AreEqual(0, recovered.Replay(_journal.ReadAfter(0)));
        }

        [Test]
        public void SimulationTransactions_AreNotJournaled()
        {
            var tx = Transfer(10, "");
            tx.Source = TransactionSource.Simulation;
            Assert.IsTrue(_processor.Execute(tx).Success);
            Assert.AreEqual(0, _journal.Entries.Count);
        }

        [Test]
        public void Loan_AmortizationMatchesFormulaAndPaysOff()
        {
            // $200,000 at 6% over 360 months ≈ $1,199.10 per month.
            var payment = Loan.ComputeMonthlyPayment(20000000, 0.06, 360);
            Assert.AreEqual(119910, payment, 2);

            var bank = _ids.Next(EntityKind.LedgerAccount);
            _ledger.Open(bank, EntityId.None, LedgerAccountKind.Bank, "bank");
            _ledger.Commit(LedgerTransaction.Transfer(_external, bank, Money.FromDollars(100000L), TransactionReason.WorldGeneration));
            var book = new LoanBook();
            var start = GameDateTime.FromCalendar(2030, 1, 1);
            var loan = book.Originate(_ids.Next(EntityKind.Loan), LoanKind.Personal, _aliceId, _alice, bank, Money.FromDollars(1200L), 0.1, 12, start, EntityId.None, out var disbursement);
            Assert.IsTrue(_processor.Execute(new WorldTransaction { Money = disbursement }).Success);
            _ledger.Commit(LedgerTransaction.Transfer(_external, _alice, Money.FromDollars(500L), TransactionReason.Wage));
            book.ProcessDuePayments(start.AddDays(400), _processor, _ledger);
            Assert.AreEqual(LoanStatus.PaidOff, loan.Status);
            Assert.AreEqual(0L, loan.OutstandingCents);
            Assert.IsTrue(_ledger.VerifyInvariant(out _));
        }

        [Test]
        public void Loan_DefaultsAfterMissedPayments()
        {
            var bank = _ids.Next(EntityKind.LedgerAccount);
            _ledger.Open(bank, EntityId.None, LedgerAccountKind.Bank, "bank");
            _ledger.Commit(LedgerTransaction.Transfer(_external, bank, Money.FromDollars(100000L), TransactionReason.WorldGeneration));
            var book = new LoanBook();
            var start = GameDateTime.FromCalendar(2030, 1, 1);
            var loan = book.Originate(_ids.Next(EntityKind.Loan), LoanKind.Mortgage, _aliceId, _alice, bank, Money.FromDollars(50000L), 0.07, 360, start, _house, out var disbursement);
            Assert.IsTrue(_processor.Execute(new WorldTransaction { Money = disbursement }).Success);
            // Alice spends everything.
            _ledger.Commit(LedgerTransaction.Transfer(_alice, _external, _ledger.BalanceOf(_alice), TransactionReason.Purchase));
            var defaulted = book.ProcessDuePayments(start.AddDays(100), _processor, _ledger);
            Assert.AreEqual(1, defaulted.Count);
            Assert.AreEqual(LoanStatus.Defaulted, loan.Status);
        }

        private WorldTransaction Purchase(Money price, string key)
        {
            var tx = new WorldTransaction
            {
                IdempotencyKey = key,
                Initiator = _aliceId,
                Money = LedgerTransaction.Transfer(_alice, _bob, price, TransactionReason.Purchase),
            };
            tx.Ownership.Add(new OwnershipChange { Asset = _house, From = _bobId, To = _aliceId });
            return tx;
        }

        private WorldTransaction Transfer(long dollars, string key)
        {
            return new WorldTransaction { IdempotencyKey = key, Money = LedgerTransaction.Transfer(_alice, _bob, Money.FromDollars(dollars), TransactionReason.PlayerTransfer) };
        }
    }
}
