using System;
using System.Collections.Generic;
using HeroGame.Core.Business;
using HeroGame.Core.Characters;
using HeroGame.Core.Economy;
using HeroGame.Core.Foundation;
using HeroGame.Core.Population;
using HeroGame.Core.Property;
using HeroGame.Core.World;
using PhoneCategory = HeroGame.Core.Phone.MessageCategory;

namespace HeroGame.Core.Simulation
{
    /// <summary>
    /// Player-run businesses (GDD §18–19): buying an existing business, founding one in a converted building,
    /// pricing, wages, advertising, stock, hiring and firing real NPCs, withdrawing profits and selling. The
    /// daily trading itself is the same aggregate model NPC-owned businesses use (<see cref="BusinessSimulator"/>),
    /// so a player's shop keeps trading while they are offline.
    /// </summary>
    public sealed class BusinessOperations
    {
        public const float MinPriceLevel = 0.5f;
        public const float MaxPriceLevel = 3f;
        public const float MinWageLevel = 0.8f;
        public const float MaxWageLevel = 2f;
        public const long MaxAdvertisingCents = 200000;
        public const int MaxNameLength = 40;

        private readonly World _w;

        public BusinessOperations(World world)
        {
            _w = world;
        }

        public List<BusinessRecord> OwnedBy(EntityId owner)
        {
            var list = new List<BusinessRecord>();
            foreach (var b in _w.Businesses.Values) if (_w.Ownership.IsOwnedBy(b.Id, owner)) list.Add(b);
            list.Sort((a, b) => a.Id.CompareTo(b.Id));
            return list;
        }

        public List<BusinessRecord> ForSale()
        {
            var list = new List<BusinessRecord>();
            foreach (var b in _w.Businesses.Values) if (b.ForSale || !_w.Ownership.OwnerOf(b.Id).IsValid) list.Add(b);
            list.Sort((a, b) => a.Id.CompareTo(b.Id));
            return list;
        }

        public bool IsPlayerOwned(BusinessRecord b) => _w.Characters.ContainsKey(_w.Ownership.OwnerOf(b.Id));

        public bool CanManage(BusinessRecord b, EntityId actor) => _w.Ownership.IsOwnedBy(b.Id, actor) || b.Managers.Contains(actor);

        /// <summary>Mean profit over the last <paramref name="days"/> trading (not closed) days.</summary>
        public long AverageDailyProfit(BusinessRecord b, int days)
        {
            long sum = 0;
            var n = 0;
            for (var i = b.Reports.Count - 1; i >= 0 && n < days; i--)
            {
                if (b.Reports[i].WasClosed) continue;
                sum += b.Reports[i].ProfitCents;
                n++;
            }
            return n == 0 ? 0 : sum / n;
        }

        /// <summary>Goodwill + equipment value (excludes the building and the cash in the till).</summary>
        public Money Valuation(BusinessRecord b)
        {
            var t = _w.BusinessSim.Template(b.TemplateId);
            if (t == null) return Money.Zero;
            var assets = t.PurchasePriceCents * _w.Macro.PriceLevel * (0.7 + 0.6 * b.Reputation);
            var earnings = AverageDailyProfit(b, 30) * 365.0 * 3.0;
            var value = b.Reports.Count < 7 ? assets : earnings > 0 ? Math.Max(assets * 0.6, 0.5 * assets + 0.5 * earnings) : assets * 0.6;
            return new Money((long)Math.Round(value / 100.0) * 100);
        }

        /// <summary>The building sells with the business when both belong to the same seller (or to nobody).</summary>
        private PropertyRecord IncludedProperty(BusinessRecord b)
        {
            if (!b.Property.IsValid) return null;
            var p = _w.Properties.Get(b.Property);
            if (p == null) return null;
            return _w.Ownership.OwnerOf(p.Id) == _w.Ownership.OwnerOf(b.Id) ? p : null;
        }

        public sealed class BusinessPrice
        {
            public Money Business;
            public Money Cash;
            public Money Property;
            public Money Tax;
            public Money Total => Business + Cash + Property + Tax;
        }

        public BusinessPrice PriceOf(BusinessRecord b)
        {
            var goodwill = b.ForSale && b.AskingPriceCents > 0 ? new Money(b.AskingPriceCents) : Valuation(b);
            var property = IncludedProperty(b);
            var propertyPrice = property == null ? Money.Zero : new Money(property.ForSale && property.ListingPriceCents > 0 ? property.ListingPriceCents : property.MarketValueCents);
            var cash = Money.Max(Money.Zero, _w.Ledger.BalanceOf(b.Account));
            return new BusinessPrice { Business = goodwill, Cash = cash, Property = propertyPrice, Tax = _w.Taxes.TransferTax(goodwill + propertyPrice) };
        }

        /// <summary>
        /// Buys a listed (or unowned) business atomically: goodwill + the cash in its account go to the seller, the
        /// building to its owner, transfer tax to the city, and title to both moves to the buyer.
        /// </summary>
        public OpResult Buy(BusinessRecord b, ServerCharacter buyer, string idempotencyKey)
        {
            var seller = _w.Ownership.OwnerOf(b.Id);
            if (seller == buyer.CharacterId) return OpResult.Fail("You already own " + b.Name + ".");
            if (seller.IsValid && !b.ForSale) return OpResult.Fail(b.Name + " is not for sale.");
            var price = PriceOf(b);
            var property = IncludedProperty(b);
            // Players are paid into their checking account; NPC owners (aggregate organisations) take the money out of the city economy.
            var sellerAccount = _w.Characters.TryGetValue(seller, out var sellerCharacter) ? sellerCharacter.CheckingAccount : _w.Accounts.External;

            var money = new LedgerTransaction { Reason = TransactionReason.Purchase, Memo = "Purchase of " + b.Name };
            money.Add(buyer.CheckingAccount, -price.Total.Cents);
            money.Add(sellerAccount, (price.Business + price.Cash).Cents);
            if (property != null && price.Property.Cents > 0) money.Add(seller.IsValid ? sellerAccount : _w.Accounts.Treasury, price.Property.Cents);
            if (price.Tax.Cents != 0) money.Add(_w.Accounts.Treasury, price.Tax.Cents);
            var tx = new WorldTransaction
            {
                IdempotencyKey = idempotencyKey ?? "",
                Initiator = buyer.CharacterId,
                Timestamp = _w.Clock.Now,
                Description = "Business purchase " + b.Name,
                Money = money,
            };
            tx.Ownership.Add(new OwnershipChange { Asset = b.Id, From = seller, To = buyer.CharacterId });
            if (property != null) tx.Ownership.Add(new OwnershipChange { Asset = property.Id, From = seller, To = buyer.CharacterId });
            var result = _w.Transactions.Execute(tx);
            if (!result.Success) return result;

            b.ForSale = false;
            b.AskingPriceCents = 0;
            b.Managers.Clear();
            b.AcquiredDay = _w.Today;
            if (property != null) property.ForSale = false;
            TakeOverStaffing(b);
            _w.History.Record(_w.Today, HistoryCategory.Business, 2, b.Name + " changes hands", "The business has a new owner.", DistrictOf(b), b.Id);
            _w.Phone.Send(buyer, b.Id, b.Name, PhoneCategory.Business, "You now own " + b.Name + ". " + b.Employees.Count + " staff kept their jobs. Manage prices, wages and stock from the office.");
            MarkDirty();
            return result;
        }

        /// <summary>
        /// Founds a business in a building the character owns: the layout must satisfy the business type (change of
        /// use), the permit fee goes to the city and the opening capital into a new operating account — atomically.
        /// </summary>
        public OpResult Start(PropertyRecord property, string templateId, string name, ServerCharacter owner, Money capital, string idempotencyKey)
        {
            var template = _w.BusinessSim.Template(templateId);
            if (template == null) return OpResult.Fail("Unknown business type.");
            if (property == null || !_w.Ownership.IsOwnedBy(property.Id, owner.CharacterId)) return OpResult.Fail("You can only open a business in a building you own.");
            foreach (var other in _w.Businesses.Values)
                if (other.Property == property.Id && _w.Ownership.OwnerOf(other.Id).IsValid) return OpResult.Fail(other.Name + " already operates here.");
            name = (name ?? "").Trim();
            if (name.Length == 0 || name.Length > MaxNameLength) return OpResult.Fail("Business names are 1–" + MaxNameLength + " characters.");
            if (capital.Cents < 0) return OpResult.Fail("Capital cannot be negative.");
            var req = _w.Construction.Requirement(template.Id);
            if (req == null) return OpResult.Fail("No permit rules for " + template.DisplayName + ".");
            if (property.Layout == null) return OpResult.Fail("The building has no layout.");
            var report = _w.Construction.Validator.Validate(property.Layout, property, true, req);
            if (report.HasErrors)
                foreach (var m in report.Messages)
                    if (m.Severity == Severity.Error) return OpResult.Fail(m.Path + ": " + m.Message);
            var place = PlaceOf(property);
            if (place == null) return OpResult.Fail("The building is not on the map.");

            var business = new BusinessRecord
            {
                Id = _w.Ids.Next(EntityKind.Business),
                Name = name,
                TemplateId = template.Id,
                Place = place.Id,
                Property = property.Id,
                Account = _w.Ids.Next(EntityKind.LedgerAccount),
                Staff = 0,
                InventoryDays = 0f,
                Reputation = 0.4f,
                AcquiredDay = _w.Today,
                LastSimulatedDay = _w.Today,
            };
            var money = new LedgerTransaction { Reason = TransactionReason.Fee, Memo = "Opening " + name };
            var permitFee = _w.Construction.PermitFee(req);
            money.Add(owner.CheckingAccount, -(capital.Cents + permitFee));
            money.Add(_w.Accounts.Treasury, permitFee);
            if (capital.Cents > 0) money.Add(business.Account, capital.Cents);
            var tx = new WorldTransaction
            {
                IdempotencyKey = idempotencyKey ?? "",
                Initiator = owner.CharacterId,
                Timestamp = _w.Clock.Now,
                Description = "Business founded: " + name,
                Money = money,
                OpenAccounts = { new LedgerAccount { Id = business.Account, Owner = business.Id, Kind = LedgerAccountKind.Business, Label = name + " operating" } },
                Records = new TransactionRecords { Business = business },
            };
            tx.Ownership.Add(new OwnershipChange { Asset = business.Id, From = EntityId.None, To = owner.CharacterId });
            var result = _w.Transactions.Execute(tx);
            if (!result.Success) return result;

            _w.History.Record(_w.Today, HistoryCategory.Business, 3, name + " opens its doors", "A new " + template.DisplayName.ToLowerInvariant() + " opens at " + property.Address + ".", place.District, business.Id);
            _w.Phone.Send(owner, business.Id, name, PhoneCategory.Business, name + " is registered. Hire staff and buy stock before customers arrive.");
            MarkDirty();
            _w.Dirty.Mark(SaveChunks.Environment);
            return result;
        }

        private Place PlaceOf(PropertyRecord property)
        {
            if (property.Place.IsValid) return _w.Geography.GetPlace(property.Place);
            foreach (var p in _w.Geography.Places) if (p.Property == property.Id) return p;
            return null;
        }

        /// <summary>
        /// Called whenever a business record appears (founded live or recovered from the journal): a player-run
        /// business is staffed by its owner's hiring, not by the NPC labour market.
        /// </summary>
        internal void OnRegistered(BusinessRecord b)
        {
            var place = _w.Geography.GetPlace(b.Place);
            var t = _w.BusinessSim.Template(b.TemplateId);
            if (place == null) return;
            if (t != null)
            {
                place.Kind = t.PlaceKind;
                place.OpenMinute = t.OpenMinute;
                place.CloseMinute = t.CloseMinute;
            }
            place.PlayerStaffed = true;
            _w.WorkplaceVersion++;
            _w.Dirty.Mark(SaveChunks.Environment);
        }

        /// <summary>After a load (and journal replay): places of player-owned businesses are player-staffed.</summary>
        internal void Reconcile()
        {
            foreach (var b in _w.Businesses.Values)
            {
                var place = _w.Geography.GetPlace(b.Place);
                if (place == null) continue;
                var player = IsPlayerOwned(b);
                if (place.PlayerStaffed == player) continue;
                place.PlayerStaffed = player;
                _w.WorkplaceVersion++;
            }
        }

        private void TakeOverStaffing(BusinessRecord b)
        {
            var place = _w.Geography.GetPlace(b.Place);
            if (place != null && !place.PlayerStaffed)
            {
                place.PlayerStaffed = true;
                _w.WorkplaceVersion++;
            }
            b.Employees.Clear();
            foreach (var npc in _w.Population.Ordered)
                if (npc.Workplace == b.Place && npc.Employment == EmploymentStatus.Employed) b.Employees.Add(npc.Id);
            b.Staff = b.Employees.Count;
        }

        // ------------------------------------------------------------------ management

        public OpResult SetPrice(BusinessRecord b, EntityId actor, float level) =>
            Manage(b, actor, () => b.PriceLevel = Clamp(level, MinPriceLevel, MaxPriceLevel));

        public OpResult SetWageLevel(BusinessRecord b, EntityId actor, float level) =>
            Manage(b, actor, () => b.WageLevel = Clamp(level, MinWageLevel, MaxWageLevel));

        public OpResult SetAdvertising(BusinessRecord b, EntityId actor, Money perDay) =>
            Manage(b, actor, () => b.AdvertisingCents = Math.Max(0, Math.Min(MaxAdvertisingCents, perDay.Cents)));

        public OpResult SetOpen(BusinessRecord b, EntityId actor, bool open) => Manage(b, actor, () => b.Open = open);

        public OpResult SetAutoRestock(BusinessRecord b, EntityId actor, bool on) => Manage(b, actor, () => b.AutoRestock = on);

        public OpResult Rename(BusinessRecord b, EntityId actor, string name)
        {
            if (!_w.Ownership.IsOwnedBy(b.Id, actor)) return OpResult.Fail("Only the owner can rename the business.");
            name = (name ?? "").Trim();
            if (name.Length == 0 || name.Length > MaxNameLength) return OpResult.Fail("Business names are 1–" + MaxNameLength + " characters.");
            b.Name = name;
            MarkDirty();
            return OpResult.Ok();
        }

        public OpResult AddManager(BusinessRecord b, EntityId owner, EntityId manager)
        {
            if (!_w.Ownership.IsOwnedBy(b.Id, owner)) return OpResult.Fail("Only the owner can appoint managers.");
            if (!_w.Characters.ContainsKey(manager)) return OpResult.Fail("Managers must be players on this server.");
            if (!b.Managers.Contains(manager)) b.Managers.Add(manager);
            MarkDirty();
            return OpResult.Ok();
        }

        public OpResult RemoveManager(BusinessRecord b, EntityId owner, EntityId manager)
        {
            if (!_w.Ownership.IsOwnedBy(b.Id, owner)) return OpResult.Fail("Only the owner can remove managers.");
            b.Managers.Remove(manager);
            MarkDirty();
            return OpResult.Ok();
        }

        private OpResult Manage(BusinessRecord b, EntityId actor, Action change)
        {
            if (!CanManage(b, actor)) return OpResult.Fail("You do not manage " + b.Name + ".");
            change();
            MarkDirty();
            return OpResult.Ok();
        }

        /// <summary>Wholesale stock purchase paid from the business account.</summary>
        public OpResult Restock(BusinessRecord b, EntityId actor, float days, string idempotencyKey)
        {
            if (!CanManage(b, actor)) return OpResult.Fail("You do not manage " + b.Name + ".");
            var t = _w.BusinessSim.Template(b.TemplateId);
            if (t == null || t.InventoryDays <= 0) return OpResult.Fail(b.Name + " does not hold stock.");
            days = Math.Min(days, t.InventoryDays * 3f - b.InventoryDays);
            if (days <= 0.01f) return OpResult.Fail("The stockroom is full.");
            var cost = RestockCost(b, days);
            var result = _w.Transactions.Execute(new WorldTransaction
            {
                IdempotencyKey = idempotencyKey ?? "",
                Initiator = actor,
                Timestamp = _w.Clock.Now,
                Description = "Stock for " + b.Name,
                Money = LedgerTransaction.Transfer(b.Account, _w.Accounts.Contractors, cost, TransactionReason.SupplyPurchase, "Wholesale order"),
            });
            if (result.Success)
            {
                b.InventoryDays += days;
                MarkDirty();
            }
            return result;
        }

        public Money RestockCost(BusinessRecord b, float days)
        {
            var t = _w.BusinessSim.Template(b.TemplateId);
            if (t == null) return Money.Zero;
            var normalDaily = t.BaseCustomersPerHour * BusinessSimulator.OpenHours(t);
            return new Money((long)(Math.Max(0f, days) * normalDaily * t.AverageTicketCents * t.CostOfGoodsRatio * _w.Macro.PriceLevel));
        }

        /// <summary>Owner draws profit out of (or puts money into) the business.</summary>
        public OpResult Withdraw(BusinessRecord b, ServerCharacter owner, Money amount, string idempotencyKey) =>
            MoveCash(b, owner, amount, fromBusiness: true, idempotencyKey);

        public OpResult Invest(BusinessRecord b, ServerCharacter owner, Money amount, string idempotencyKey) =>
            MoveCash(b, owner, amount, fromBusiness: false, idempotencyKey);

        private OpResult MoveCash(BusinessRecord b, ServerCharacter owner, Money amount, bool fromBusiness, string idempotencyKey)
        {
            if (!_w.Ownership.IsOwnedBy(b.Id, owner.CharacterId)) return OpResult.Fail("Only the owner can move money in or out.");
            if (amount.Cents <= 0) return OpResult.Fail("Amount must be positive.");
            return _w.Transactions.Execute(new WorldTransaction
            {
                IdempotencyKey = idempotencyKey ?? "",
                Initiator = owner.CharacterId,
                Timestamp = _w.Clock.Now,
                Description = (fromBusiness ? "Owner draw from " : "Owner investment in ") + b.Name,
                Money = fromBusiness
                    ? LedgerTransaction.Transfer(b.Account, owner.CheckingAccount, amount, TransactionReason.Payout, "Owner draw")
                    : LedgerTransaction.Transfer(owner.CheckingAccount, b.Account, amount, TransactionReason.PlayerTransfer, "Owner investment"),
            });
        }

        public OpResult ListForSale(BusinessRecord b, EntityId owner, Money price)
        {
            if (!_w.Ownership.IsOwnedBy(b.Id, owner)) return OpResult.Fail("Only the owner can sell the business.");
            if (price.Cents <= 0) return OpResult.Fail("Price must be positive.");
            b.ForSale = true;
            b.AskingPriceCents = price.Cents;
            MarkDirty();
            return OpResult.Ok();
        }

        public OpResult Delist(BusinessRecord b, EntityId owner)
        {
            if (!_w.Ownership.IsOwnedBy(b.Id, owner)) return OpResult.Fail("Only the owner can delist the business.");
            b.ForSale = false;
            b.AskingPriceCents = 0;
            MarkDirty();
            return OpResult.Ok();
        }

        /// <summary>Quick sale to an outside buyer at 60 % of valuation; the buyer takes the till with it. The building is kept.</summary>
        public OpResult SellToMarket(BusinessRecord b, ServerCharacter owner, string idempotencyKey)
        {
            if (!_w.Ownership.IsOwnedBy(b.Id, owner.CharacterId)) return OpResult.Fail("Only the owner can sell the business.");
            var offer = Valuation(b).Scale(0.6) + Money.Max(Money.Zero, _w.Ledger.BalanceOf(b.Account));
            var tx = new WorldTransaction
            {
                IdempotencyKey = idempotencyKey ?? "",
                Initiator = owner.CharacterId,
                Timestamp = _w.Clock.Now,
                Description = "Sale of " + b.Name,
                Money = LedgerTransaction.Transfer(_w.Accounts.External, owner.CheckingAccount, offer, TransactionReason.Sale, "Sale of " + b.Name),
            };
            tx.Ownership.Add(new OwnershipChange { Asset = b.Id, From = owner.CharacterId, To = EntityId.None });
            var result = _w.Transactions.Execute(tx);
            if (!result.Success) return result;
            b.ForSale = true;
            b.AskingPriceCents = 0;
            b.Managers.Clear();
            var place = _w.Geography.GetPlace(b.Place);
            if (place != null && place.PlayerStaffed)
            {
                place.PlayerStaffed = false;
                _w.WorkplaceVersion++;
            }
            _w.Phone.Send(owner, b.Id, b.Name, PhoneCategory.Business, "Sold " + b.Name + " for " + offer + ".");
            MarkDirty();
            return result;
        }

        // ------------------------------------------------------------------ staff

        /// <summary>Hourly pay a business offers (template wage × wage level × price level).</summary>
        public Money OfferedHourly(BusinessRecord b)
        {
            var t = _w.BusinessSim.Template(b.TemplateId);
            return t == null ? Money.Zero : new Money((long)(t.HourlyWageCents * b.WageLevel * _w.Macro.PriceLevel * _w.Config.Economy.WageMultiplier));
        }

        /// <summary>Job seekers who would take this job, best first (education, conscientiousness, then id for stability).</summary>
        public List<NpcRecord> Candidates(BusinessRecord b, int max = 10)
        {
            var today = _w.Today;
            var offeredAnnual = OfferedHourly(b).Cents * 2080;
            var list = new List<NpcRecord>();
            foreach (var npc in _w.Population.Ordered)
            {
                var age = npc.AgeYears(today);
                if (age < 18 || age > 66) continue;
                if (npc.Employment == EmploymentStatus.Unemployed) list.Add(npc);
                else if (npc.Employment == EmploymentStatus.Employed && npc.Workplace != b.Place && npc.AnnualSalaryCents * 1.1 < offeredAnnual) list.Add(npc);
            }
            list.Sort((x, y) =>
            {
                var sx = (int)x.Education * 10 + x.Personality.Conscientiousness * 10;
                var sy = (int)y.Education * 10 + y.Personality.Conscientiousness * 10;
                var c = sy.CompareTo(sx);
                return c != 0 ? c : x.Id.CompareTo(y.Id);
            });
            if (list.Count > max) list.RemoveRange(max, list.Count - max);
            return list;
        }

        public OpResult Hire(BusinessRecord b, EntityId actor, EntityId npcId)
        {
            if (!CanManage(b, actor)) return OpResult.Fail("You do not manage " + b.Name + ".");
            var t = _w.BusinessSim.Template(b.TemplateId);
            var npc = _w.Population.Get(npcId);
            if (t == null || npc == null) return OpResult.Fail("Unknown person.");
            if (b.Employees.Contains(npcId)) return OpResult.Fail(npc.FullName + " already works here.");
            if (b.Employees.Count >= Math.Max(2, t.StaffRequired * 2)) return OpResult.Fail("There is no room for more staff.");
            var age = npc.AgeYears(_w.Today);
            if (age < 18 || age > 66) return OpResult.Fail(npc.FullName + " is not looking for work.");
            var offeredAnnual = OfferedHourly(b).Cents * 2080;
            if (npc.Employment == EmploymentStatus.Employed && npc.AnnualSalaryCents * 1.1 >= offeredAnnual)
                return OpResult.Fail(npc.FullName + " declines: the pay is no better than their current job.");
            if (npc.Employment != EmploymentStatus.Employed && npc.Employment != EmploymentStatus.Unemployed)
                return OpResult.Fail(npc.FullName + " is not looking for work.");

            var previous = npc.Workplace;
            npc.Employment = EmploymentStatus.Employed;
            npc.Workplace = b.Place;
            npc.OccupationId = OccupationFor(t) ?? npc.OccupationId;
            npc.JobStartDay = _w.Today;
            npc.JobPerformance = 0.5f;
            npc.AnnualSalaryCents = offeredAnnual;
            npc.AddHistory(_w.Today, "hired", npc.FullName + " started work at " + b.Name + ".");
            if (previous.IsValid) RemoveFromOtherBusinesses(npc.Id, previous);
            b.Employees.Add(npc.Id);
            b.Staff = b.Employees.Count;
            _w.Population.Reindex(npc);
            _w.Director.Invalidate(npc.Id);
            MarkDirty();
            _w.Dirty.Mark(SaveChunks.Population);
            return OpResult.Ok();
        }

        public OpResult Fire(BusinessRecord b, EntityId actor, EntityId npcId)
        {
            if (!CanManage(b, actor)) return OpResult.Fail("You do not manage " + b.Name + ".");
            if (!b.Employees.Remove(npcId)) return OpResult.Fail("That person does not work here.");
            var npc = _w.Population.Get(npcId);
            if (npc != null && npc.Workplace == b.Place)
            {
                npc.Employment = EmploymentStatus.Unemployed;
                npc.Workplace = EntityId.None;
                npc.AddHistory(_w.Today, "fired", npc.FullName + " was let go from " + b.Name + ".");
                _w.Population.Reindex(npc);
                _w.Director.Invalidate(npc.Id);
                _w.Dirty.Mark(SaveChunks.Population);
            }
            b.Staff = b.Employees.Count;
            MarkDirty();
            return OpResult.Ok();
        }

        private string OccupationFor(BusinessTemplate t)
        {
            OccupationDefinition best = null;
            foreach (var o in _w.Occupations.All)
            {
                if (o.PlayerCareer || !o.WorkplaceKinds.Contains(t.PlaceKind)) continue;
                if (best == null || o.MinSalaryCents < best.MinSalaryCents || (o.MinSalaryCents == best.MinSalaryCents && string.CompareOrdinal(o.Id, best.Id) < 0)) best = o;
            }
            return best?.Id;
        }

        private void RemoveFromOtherBusinesses(EntityId npc, EntityId previousPlace)
        {
            foreach (var other in _w.Businesses.Values)
                if (other.Place == previousPlace && other.Employees.Remove(npc) && IsPlayerOwned(other)) other.Staff = other.Employees.Count;
        }

        // ------------------------------------------------------------------ daily

        /// <summary>Before trading: player-run staff lists follow the people who actually still work there; some quit if underpaid.</summary>
        internal void BeforeDay(BusinessRecord b, long day)
        {
            if (!IsPlayerOwned(b)) return;
            for (var i = b.Employees.Count - 1; i >= 0; i--)
            {
                var npc = _w.Population.Get(b.Employees[i]);
                if (npc == null || npc.Workplace != b.Place || npc.Employment != EmploymentStatus.Employed)
                {
                    b.Employees.RemoveAt(i);
                    continue;
                }
                // Annual turnover ≈ 55 % at 0.8× pay, ≈ 46 % at template pay (typical retail/hospitality), 3 % at double pay.
                var annualQuit = Math.Max(0.03, 0.55 - 0.45 * (b.WageLevel - 0.8));
                var quitChance = -Math.Log(1.0 - annualQuit) / 365.0;
                if (DeterministicRandom.For(_w.Seed, 0x9017, npc.Id.Value, (ulong)day).Chance(quitChance))
                {
                    npc.Employment = EmploymentStatus.Unemployed;
                    npc.Workplace = EntityId.None;
                    npc.AddHistory(day, "quit", npc.FullName + " quit " + b.Name + ".");
                    _w.Population.Reindex(npc);
                    _w.Director.Invalidate(npc.Id);
                    b.Employees.RemoveAt(i);
                    Notify(b, npc.FullName + " quit.");
                }
            }
            b.Staff = b.Employees.Count;
        }

        /// <summary>After trading: a missed payroll costs employees; owners get a weekly summary.</summary>
        internal void AfterDay(BusinessRecord b, BusinessDayReport report, long day)
        {
            if (!IsPlayerOwned(b)) return;
            while (b.Employees.Count > b.Staff && b.Employees.Count > 0)
            {
                var id = b.Employees[b.Employees.Count - 1];
                b.Employees.RemoveAt(b.Employees.Count - 1);
                var npc = _w.Population.Get(id);
                if (npc == null) continue;
                npc.Employment = EmploymentStatus.Unemployed;
                npc.Workplace = EntityId.None;
                npc.AddHistory(day, "quit", npc.FullName + " quit " + b.Name + " after missing a paycheck.");
                _w.Population.Reindex(npc);
                _w.Director.Invalidate(npc.Id);
                Notify(b, "Payroll bounced — " + npc.FullName + " walked out. Put money into the business account.");
            }
            if ((day - b.AcquiredDay) % 7 == 6)
            {
                long revenue = 0, profit = 0;
                var n = 0;
                for (var i = b.Reports.Count - 1; i >= 0 && n < 7; i--, n++)
                {
                    revenue += b.Reports[i].RevenueCents;
                    profit += b.Reports[i].ProfitCents;
                }
                Notify(b, "Weekly report: revenue " + new Money(revenue) + ", profit " + new Money(profit) + ", reputation " + (int)(b.Reputation * 100) + "/100, staff " + b.Staff + ".");
            }
        }

        private void Notify(BusinessRecord b, string text)
        {
            if (_w.Characters.TryGetValue(_w.Ownership.OwnerOf(b.Id), out var owner)) _w.Phone.Send(owner, b.Id, b.Name, PhoneCategory.Business, text);
        }

        private EntityId DistrictOf(BusinessRecord b)
        {
            var place = _w.Geography.GetPlace(b.Place);
            return place != null ? place.District : EntityId.None;
        }

        private void MarkDirty() => _w.Dirty.Mark(SaveChunks.Businesses);

        private static float Clamp(float v, float min, float max) => float.IsNaN(v) ? min : Math.Max(min, Math.Min(max, v));
    }
}
