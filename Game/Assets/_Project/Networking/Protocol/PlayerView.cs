using System.Collections.Generic;
using HeroGame.Core.Characters;
using HeroGame.Core.Foundation;
using HeroGame.Core.Presentation;

namespace HeroGame.Networking.Protocol
{
    /// <summary>
    /// A player's own state as the server knows it (cash, health, messages, statement, inventory, property, businesses):
    /// what the HUD and phone show while online, where the client's local world is only a presentation copy.
    /// Plain data, sent as JSON inside <see cref="PlayerViewMessage"/> whenever it changes.
    /// </summary>
    public sealed class PlayerViewData
    {
        public const int MaxMessages = 40;
        public const int MaxStatement = 30;

        public long CashCents;
        public long SavingsCents = -1;
        public float Health = 1f;
        public string Injury = "";
        public int WantedLevel;
        public bool InCustody;
        public long MedicalDebtCents;
        public long FinesOwedCents;
        public int Unread;
        public List<string> Licenses = new List<string>();
        public List<Message> Messages = new List<Message>();
        public List<StatementEntry> Statement = new List<StatementEntry>();
        public List<Item> Inventory = new List<Item>();
        public List<Property> Properties = new List<Property>();
        public long PropertyValueCents;
        public long PropertyOwedCents;
        public long PropertyMonthlyNetCents;
        public List<Business> Businesses = new List<Business>();
        public int CreditScore;
        public long MonthlyDebtCents;
        public long VerifiedIncomeCents;
        public List<LoanLine> Loans = new List<LoanLine>();
        public List<PolicyLine> Policies = new List<PolicyLine>();
        /// <summary>Things this player could insure: health plus owned properties, vehicles and businesses.</summary>
        public List<Insurable> Insurables = new List<Insurable>();
        /// <summary>A mugging in progress, or null.</summary>
        public EncounterView Encounter;

        public sealed class EncounterView
        {
            public string Mugger = "";
            public string Name = "";
            public long DemandCents;
            public string Weapon = "";
            public long SecondsLeft;
            public float X;
            public float Z;
        }

        public sealed class LoanLine
        {
            public string Id = "";
            public string Kind = "";
            public string Status = "";
            public long OutstandingCents;
            public long MonthlyCents;
            public double Rate;
        }

        public sealed class PolicyLine
        {
            public string Id = "";
            public string Kind = "";
            public string Status = "";
            public long PremiumCents;
            public long CoverCents;
            public int Claims;
            public long AssessedLossCents;
            public bool Active;
        }

        public sealed class Insurable
        {
            public string Kind = "";
            public string Asset = "";
            public string Label = "";
        }

        public sealed class Message
        {
            public long Id;
            public string From = "";
            public string Category = "";
            public string Body = "";
            public long Day;
            public int Hour;
            public int Minute;
            public bool Read;
        }

        public sealed class StatementEntry
        {
            public long AmountCents;
            public string Reason = "";
            public string Memo = "";
        }

        public sealed class Item
        {
            public string Name = "";
            public string Category = "";
            public int Quantity;
            public long TotalValueCents;
            public bool Stolen;
            public bool Illegal;
        }

        public sealed class Property
        {
            public string Id = "";
            public string Address = "";
            public string District = "";
            public string Kind = "";
            public long ValueCents;
            public long OwedCents;
            public long RentCents;
            public long NetCents;
            public int Units;
            public int Occupied;
            public bool Insured;
            public bool ForSale;
            public List<string> Alerts = new List<string>();
        }

        public sealed class Business
        {
            public string Id = "";
            public string Name = "";
            public long BalanceCents;
            public long LastDayProfitCents;
            public int LastDayCustomers;
            public bool LastDayClosed;
        }

        /// <summary>Builds the view from the authoritative world.</summary>
        public static PlayerViewData Build(Core.Simulation.World w, ServerCharacter c)
        {
            var v = new PlayerViewData
            {
                CashCents = w.Ledger.BalanceOf(c.CheckingAccount).Cents,
                Health = c.Health,
                Injury = c.Injury.ToString(),
                InCustody = c.Record.InCustody,
                MedicalDebtCents = c.MedicalDebtCents,
                FinesOwedCents = c.Record.FinesOwedCents,
                Unread = w.Phone.UnreadCount(c),
            };
            var savings = w.Finance.SavingsOf(c);
            if (savings.IsValid) v.SavingsCents = w.Ledger.BalanceOf(savings).Cents;
            var wanted = w.Wanted.Get(c.CharacterId);
            v.WantedLevel = wanted != null ? wanted.Level : 0;
            foreach (var l in c.Licenses) if (!l.Suspended && (l.ExpiresDay <= 0 || l.ExpiresDay > w.Today)) v.Licenses.Add(l.Kind);
            for (var i = c.Inbox.Count - 1; i >= 0 && v.Messages.Count < MaxMessages; i--)
            {
                var m = c.Inbox[i];
                v.Messages.Add(new Message { Id = m.Id, From = m.FromName, Category = m.Category.ToString(), Body = m.Body, Day = m.At.DayIndex, Hour = m.At.Hour, Minute = m.At.Minute, Read = m.Read });
            }
            for (var i = c.Statement.Count - 1; i >= 0 && v.Statement.Count < MaxStatement; i--)
            {
                var s = c.Statement[i];
                v.Statement.Add(new StatementEntry { AmountCents = s.AmountCents, Reason = s.Reason.ToString(), Memo = s.Memo });
            }
            foreach (var l in PlayerViews.InventoryOf(w.Content, c))
                v.Inventory.Add(new Item { Name = l.Name, Category = l.Category, Quantity = l.Quantity, TotalValueCents = l.TotalValue.Cents, Stolen = l.Stolen, Illegal = l.Illegal });
            var portfolio = PlayerViews.PortfolioOf(w, c);
            v.PropertyValueCents = portfolio.TotalValue.Cents;
            v.PropertyOwedCents = portfolio.TotalOwed.Cents;
            v.PropertyMonthlyNetCents = portfolio.MonthlyNet.Cents;
            foreach (var e in portfolio.Entries)
                v.Properties.Add(new Property
                {
                    Id = e.Property.ToString(), Address = e.Address, District = e.District, Kind = e.Kind.ToString(), ValueCents = e.MarketValue.Cents, OwedCents = e.MortgageOwed.Cents,
                    RentCents = e.MonthlyRentIncome.Cents, NetCents = e.MonthlyNet.Cents, Units = e.Units, Occupied = e.OccupiedUnits, Insured = e.Insured, ForSale = e.ForSale,
                    Alerts = new List<string>(e.Alerts),
                });
            var encounter = w.StreetCrime.ActiveFor(c.CharacterId);
            if (encounter != null)
                v.Encounter = new EncounterView
                {
                    Mugger = encounter.Mugger.ToString(), Name = encounter.MuggerName, DemandCents = encounter.DemandCents, Weapon = encounter.WeaponId,
                    SecondsLeft = System.Math.Max(0, encounter.DeadlineSecond - w.Clock.Now.TotalSeconds), X = encounter.Position.X, Z = encounter.Position.Z,
                };
            var credit = w.Finance.Credit(c);
            v.CreditScore = credit.Score;
            v.MonthlyDebtCents = credit.MonthlyDebtPaymentsCents;
            v.VerifiedIncomeCents = w.Finance.VerifiedMonthlyIncome(c).Cents;
            foreach (var loan in w.Loans.Loans)
                if (loan.Borrower == c.CharacterId && loan.Status != Core.Economy.LoanStatus.PaidOff)
                    v.Loans.Add(new LoanLine { Id = loan.Id.ToString(), Kind = loan.Kind.ToString(), Status = loan.Status.ToString(), OutstandingCents = loan.OutstandingCents, MonthlyCents = loan.MonthlyPaymentCents, Rate = loan.AnnualRate });
            foreach (var policy in w.Insurance.ForHolder(c.CharacterId))
                v.Policies.Add(new PolicyLine
                {
                    Id = policy.Id.ToString(), Kind = policy.Kind.ToString(), Status = policy.Status.ToString(), PremiumCents = policy.MonthlyPremiumCents,
                    CoverCents = policy.RemainingCoverageCents, Claims = policy.Claims, AssessedLossCents = policy.Active ? w.Finance.AssessedLoss(policy).Cents : 0, Active = policy.Active,
                });
            v.Insurables.Add(new Insurable { Kind = "Health", Label = "Health" });
            var assets = new List<EntityId>(w.Ownership.AssetsOf(c.CharacterId));
            assets.Sort();
            foreach (var asset in assets)
            {
                if (asset.Kind == EntityKind.Property) v.Insurables.Add(new Insurable { Kind = "Property", Asset = asset.ToString(), Label = w.Properties.Get(asset)?.Address ?? asset.ToString() });
                else if (asset.Kind == EntityKind.Vehicle) v.Insurables.Add(new Insurable { Kind = "Vehicle", Asset = asset.ToString(), Label = w.Vehicles.Get(asset)?.Plate ?? asset.ToString() });
                else if (asset.Kind == EntityKind.Business && w.Businesses.TryGetValue(asset, out var biz)) v.Insurables.Add(new Insurable { Kind = "BusinessInterruption", Asset = asset.ToString(), Label = biz.Name });
            }
            foreach (var b in w.BusinessOps.OwnedBy(c.CharacterId))
            {
                var last = b.Reports.Count > 0 ? b.Reports[b.Reports.Count - 1] : null;
                v.Businesses.Add(new Business
                {
                    Id = b.Id.ToString(), Name = b.Name, BalanceCents = w.Ledger.BalanceOf(b.Account).Cents,
                    LastDayProfitCents = last != null ? last.ProfitCents : 0, LastDayCustomers = last != null ? last.Customers : 0, LastDayClosed = last != null && last.WasClosed,
                });
            }
            return v;
        }
    }

    /// <summary>Server → client: the player's own state (JSON of <see cref="PlayerViewData"/>), sent when it changes.</summary>
    public sealed class PlayerViewMessage : NetMessage
    {
        public string Json = "";
        public override MessageType Type => MessageType.PlayerView;
        public override void Write(PacketWriter w) => w.String(Json, Wire.MaxLongStringBytes);
        public override void Read(PacketReader r) => Json = r.String(Wire.MaxLongStringBytes);
    }
}
