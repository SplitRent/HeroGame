using System;
using System.Collections.Generic;
using System.Globalization;

namespace HeroGame.Networking.Server
{
    using HeroGame.Core.Building;
    using HeroGame.Core.Crime;
    using HeroGame.Core.Economy;
    using HeroGame.Core.Foundation;
    using HeroGame.Core.Servers;
    using HeroGame.Core.Simulation;
    using HeroGame.Networking.Protocol;

    /// <summary>
    /// The request catalogue a client can call. Handlers are thin: they parse and bound arguments, check that the
    /// player is physically near what they act on (the server's authoritative position), then call the same core
    /// services single-player uses — so rules live in one place and a modified client gains nothing.
    /// </summary>
    public static class StandardRequests
    {
        /// <summary>How close (metres) the player must be to act on something.</summary>
        public const float CounterReach = 30f;
        public const float BuildingReach = 60f;
        public const float VehicleReach = 8f;
        public const float PersonReach = 4f;
        /// <summary>Schedule positions are approximate within a place, so reach for NPCs is looser than for players.</summary>
        public const float PickpocketReach = 12f;

        public static void Register(RequestRouter r)
        {
            r.Register("me.status", Status);

            r.Register("property.buy", ctx =>
            {
                var p = ctx.World.Properties.Get(ctx.Id("property"));
                if (p == null) return RequestContext.Fail("Unknown property.");
                var seller = ctx.World.AccountFor(ctx.World.Ownership.OwnerOf(p.Id));
                return RequestContext.From(ctx.World.Properties.Purchase(p.Id, ctx.Me.CharacterId, ctx.Me.CheckingAccount, seller, ctx.World.Accounts.Treasury, ctx.World.Clock.Now, ctx.Key));
            });
            r.Register("property.mortgage", ctx =>
            {
                var p = ctx.World.Properties.Get(ctx.Id("property"));
                if (p == null) return RequestContext.Fail("Unknown property.");
                return RequestContext.From(ctx.World.Finance.BuyWithMortgage(ctx.Me, p, new Money(ctx.Long("down", 0)), (int)ctx.Long("term", 6, 360), ctx.Key));
            });
            r.Register("property.repair", ctx =>
            {
                var p = ctx.World.Properties.Get(ctx.Id("property"));
                if (p == null) return RequestContext.Fail("Unknown property.");
                return RequestContext.From(ctx.World.Properties.Repair(p, ctx.Me.CharacterId, ctx.Me.CheckingAccount, ctx.World.Accounts.Contractors, ctx.World.Clock.Now, ctx.World.Macro.PriceLevel, ctx.Key));
            });
            r.Register("build.commit", BuildCommit);

            r.Register("business.buy", ctx => WithBusiness(ctx, b => ctx.World.BusinessOps.Buy(b, ctx.Me, ctx.Key)));
            r.Register("business.start", ctx =>
            {
                var p = ctx.World.Properties.Get(ctx.Id("property"));
                return RequestContext.From(ctx.World.BusinessOps.Start(p, ctx.Str("template", 64), ctx.Str("name", 64), ctx.Me, new Money(ctx.Long("capital", 0)), ctx.Key));
            });
            r.Register("business.price", ctx => WithBusiness(ctx, b => ctx.World.BusinessOps.SetPrice(b, ctx.Me.CharacterId, ctx.Float("level", 0f, 10f))));
            r.Register("business.wage", ctx => WithBusiness(ctx, b => ctx.World.BusinessOps.SetWageLevel(b, ctx.Me.CharacterId, ctx.Float("level", 0f, 10f))));
            r.Register("business.ads", ctx => WithBusiness(ctx, b => ctx.World.BusinessOps.SetAdvertising(b, ctx.Me.CharacterId, new Money(ctx.Long("perDay", 0)))));
            r.Register("business.hire", ctx => WithBusiness(ctx, b => ctx.World.BusinessOps.Hire(b, ctx.Me.CharacterId, ctx.Id("npc"))));
            r.Register("business.fire", ctx => WithBusiness(ctx, b => ctx.World.BusinessOps.Fire(b, ctx.Me.CharacterId, ctx.Id("npc"))));
            r.Register("business.withdraw", ctx => WithBusiness(ctx, b => ctx.World.BusinessOps.Withdraw(b, ctx.Me, new Money(ctx.Long("amount", 1)), ctx.Key)));
            r.Register("business.invest", ctx => WithBusiness(ctx, b => ctx.World.BusinessOps.Invest(b, ctx.Me, new Money(ctx.Long("amount", 1)), ctx.Key)));
            r.Register("business.restock", ctx => WithBusiness(ctx, b => ctx.World.BusinessOps.Restock(b, ctx.Me.CharacterId, ctx.Float("days", 0.1f, 30f), ctx.Key)));

            r.Register("finance.transfer", ctx => RequestContext.From(ctx.World.Finance.TransferOwn(ctx.Me, ctx.Id("from"), ctx.Id("to"), new Money(ctx.Long("amount", 1)), ctx.Key)));
            r.Register("finance.savings", ctx => RequestContext.From(ctx.World.Finance.OpenSavings(ctx.Me, ctx.Key)));
            r.Register("finance.loan", ctx =>
            {
                if (!Enum.TryParse(ctx.Str("kind", 16), out LoanKind kind)) return RequestContext.Fail("Unknown loan kind.");
                var collateral = ctx.Request.Args.ContainsKey("collateral") ? ctx.Id("collateral") : EntityId.None;
                return RequestContext.From(ctx.World.Finance.TakeLoan(ctx.Me, kind, new Money(ctx.Long("amount", 1)), (int)ctx.Long("term", 6, 360), collateral, ctx.Key));
            });
            r.Register("finance.repay", ctx =>
            {
                ctx.World.Loans.TryGet(ctx.Id("loan"), out var loan);
                return RequestContext.From(ctx.World.Finance.Repay(ctx.Me, loan, new Money(ctx.Long("amount", 1)), ctx.Key));
            });
            r.Register("finance.quote", ctx =>
            {
                if (!Enum.TryParse(ctx.Str("kind", 16), out LoanKind kind)) return RequestContext.Fail("Unknown loan kind.");
                var offer = ctx.World.Finance.QuoteLoan(ctx.Me, kind, new Money(ctx.Long("amount", 1)), (int)ctx.Long("term", 6, 360), EntityId.None);
                return RequestContext.Ok(new Dictionary<string, string>
                {
                    ["approved"] = offer.Approved ? "true" : "false", ["reason"] = offer.Reason ?? "",
                    ["rate"] = offer.AnnualRate.ToString("R", CultureInfo.InvariantCulture), ["monthly"] = offer.MonthlyPaymentCents.ToString(CultureInfo.InvariantCulture),
                });
            });
            r.Register("insurance.quote", ctx =>
            {
                if (!Enum.TryParse(ctx.Str("kind", 32), out InsuranceKind kind)) return RequestContext.Fail("Unknown insurance kind.");
                var asset = ctx.Request.Args.ContainsKey("asset") ? ctx.Id("asset") : EntityId.None;
                var q = ctx.World.Finance.QuoteInsurance(ctx.Me, kind, asset, new Money(ctx.Long("deductible", 0)));
                return RequestContext.Ok(new Dictionary<string, string>
                {
                    ["available"] = q.Available ? "true" : "false", ["reason"] = q.Reason ?? "",
                    ["premium"] = q.MonthlyPremiumCents.ToString(CultureInfo.InvariantCulture), ["cover"] = q.CoverageCents.ToString(CultureInfo.InvariantCulture),
                });
            });
            r.Register("insurance.cancel", ctx => RequestContext.From(ctx.World.Finance.CancelInsurance(ctx.Me, ctx.World.Insurance.Get(ctx.Id("policy")))));
            r.Register("insurance.buy", ctx =>
            {
                if (!Enum.TryParse(ctx.Str("kind", 32), out InsuranceKind kind)) return RequestContext.Fail("Unknown insurance kind.");
                var asset = ctx.Request.Args.ContainsKey("asset") ? ctx.Id("asset") : EntityId.None;
                return RequestContext.From(ctx.World.Finance.BuyInsurance(ctx.Me, kind, asset, new Money(ctx.Long("deductible", 0)), ctx.Key));
            });
            r.Register("insurance.claim", ctx => RequestContext.From(ctx.World.Finance.Claim(ctx.Me, ctx.World.Insurance.Get(ctx.Id("policy")), ctx.Key)));

            r.Register("character.mask", ctx =>
            {
                var on = ctx.Str("on", 5) == "true";
                if (on && !ctx.Me.Inventory.Exists(s => s.ItemId == "ski_mask")) return RequestContext.Fail("You don't have a mask.");
                ctx.Connection.MaskOn = on;
                return RequestContext.Ok();
            });
            r.Register("crime.shoplift", ctx => Crime(ctx, "business"));
            r.Register("crime.rob", ctx => Crime(ctx, "business"));
            r.Register("crime.burgle", ctx => Crime(ctx, "property"));
            r.Register("crime.steal_vehicle", ctx => Crime(ctx, "vehicle"));
            r.Register("crime.pickpocket", ctx => Crime(ctx, "npc"));
            r.Register("crime.fence", ctx => RequestContext.From(ctx.World.Crimes.SellToFence(ctx.Me, ctx.Key)));

            r.Register("power.use", UsePower);

            r.Register("combat.attack", Attack);
            r.Register("weapons.buy", ctx => AtShop(ctx, b => ctx.World.Combat.BuyWeapon(ctx.Me, b, ctx.Str("weapon", 64), ctx.Key)));
            r.Register("weapons.ammo", ctx => AtShop(ctx, b => ctx.World.Combat.BuyAmmo(ctx.Me, b, ctx.Str("weapon", 64), (int)ctx.Long("packs", 1, 10), ctx.Key)));
            r.Register("civic.firearm_permit", ctx =>
            {
                var cityHall = CityHall(ctx.World);
                if (cityHall != null && !ctx.Near(cityHall.Position, BuildingReach)) return RequestContext.Fail("Apply in person at City Hall.");
                return RequestContext.From(ctx.World.Combat.ApplyForFirearmPermit(ctx.Me, ctx.Key));
            });

            r.Register("justice.bail", ctx => RequestContext.From(ctx.World.Courts.PostBail(ctx.Me, ctx.Key)));
            r.Register("justice.fines", ctx => RequestContext.From(ctx.World.Courts.PayFines(ctx.Me, new Money(ctx.Long("amount", 1)), ctx.Key)));
            r.Register("justice.attorney", ctx => RequestContext.From(ctx.World.Courts.HireAttorney(ctx.Me, ctx.Key)));
            r.Register("justice.plea", ctx => RequestContext.From(ctx.World.Courts.AcceptPlea(ctx.Me)));
            r.Register("justice.surrender", ctx => RequestContext.From(ctx.World.Courts.TurnSelfIn(ctx.Me)));

            r.Register("ripple.post", ctx =>
            {
                if (ctx.Server.Moderation.IsMuted(ctx.AccountId, ctx.Server.UnixNow)) return RequestContext.Fail("You are muted.");
                var result = ctx.World.Feed.Post(ctx.Me, ctx.Connection.Ticket?.DisplayName ?? "", ctx.Str("text", Core.Simulation.RippleService.MaxPostLength), out var post);
                if (!result.Success) return RequestContext.Fail(result.Error);
                return RequestContext.Ok(new Dictionary<string, string> { ["id"] = post.Id.ToString(CultureInfo.InvariantCulture) });
            });
            r.Register("ripple.like", ctx => RequestContext.From(ctx.World.Feed.Like(ctx.Me, ctx.Long("post", 1))));
            r.Register("ripple.follow", ctx => RequestContext.From(ctx.World.Feed.Follow(ctx.Me, ctx.Id("account"), ctx.OptStr("follow", "true") != "false")));
            r.Register("ripple.feed", ctx =>
            {
                var posts = ctx.World.Feed.Feed(ctx.Me.CharacterId, (int)ctx.Long("count", 1, 50), ctx.OptStr("tag", ""));
                var data = new Dictionary<string, string> { ["count"] = posts.Count.ToString(CultureInfo.InvariantCulture) };
                for (var i = 0; i < posts.Count; i++)
                {
                    var p = posts[i];
                    data["post" + i] = p.Id.ToString(CultureInfo.InvariantCulture) + "|" + p.Author + "|" + Clean(p.AuthorName) + "|" + p.Likes.ToString(CultureInfo.InvariantCulture) +
                                       "|" + p.At.TotalSeconds.ToString(CultureInfo.InvariantCulture) + "|" + Clean(p.Text);
                }
                return RequestContext.Ok(data);
            });

            r.Register("prop.impact", ctx =>
            {
                // A vehicle hit street furniture. Everything but "which prop" comes from the server: where the player is,
                // which vehicle they are in, and how fast they were going.
                var w = ctx.World;
                var prop = w.Destructibles.Get((int)ctx.Long("prop", 1, int.MaxValue));
                if (prop == null) return RequestContext.Fail("Unknown prop.");
                if (!ctx.Near(prop.Position, 8f)) return RequestContext.Fail("You are not there.");
                var vehicle = ctx.Connection.Vehicle.IsValid ? w.Vehicles.Get(ctx.Connection.Vehicle) : null;
                if (vehicle == null) return RequestContext.Fail("Only vehicles knock things over.");
                var model = w.Vehicles.Model(vehicle.ModelId);
                var speed = Math.Max(0f, Math.Min(ctx.Connection.Speed, 75f));
                var broke = w.Destructibles.Impact(prop, speed, model != null ? model.MassKg : 1400f);
                // A light pole gives way (the car loses a few m/s); a concrete bollard stops it dead.
                var kind = w.Destructibles.Kind(prop.Kind);
                var deltaV = kind != null && kind.Material == "Concrete" ? speed : Math.Min(speed, 4f);
                if (speed >= 3f) w.Vehicles.ApplyCollision(vehicle, deltaV * (model != null ? model.MassKg : 1400f), frontal: true);
                w.Dirty.Mark(SaveChunks.Vehicles);
                return RequestContext.Ok(new Dictionary<string, string> { ["state"] = prop.State.ToString(), ["broke"] = broke ? "true" : "false" });
            });

            r.Register("radio.now", ctx =>
            {
                var seg = ctx.World.Radio.OnAir(ctx.Str("station", 64), ctx.World.Clock.Now);
                if (seg == null) return RequestContext.Fail("No such station.");
                return RequestContext.Ok(new Dictionary<string, string>
                {
                    ["kind"] = seg.Kind.ToString(), ["title"] = seg.Title, ["text"] = seg.Text.Length > 600 ? seg.Text.Substring(0, 600) : seg.Text,
                    ["track"] = seg.TrackId, ["start"] = seg.StartSecond.ToString(CultureInfo.InvariantCulture), ["end"] = seg.EndSecond.ToString(CultureInfo.InvariantCulture),
                });
            });

            r.Register("civic.register_powers", ctx => RequestContext.From(ctx.World.Government.RegisterPowers(ctx.Me)));
            r.Register("civic.ballot", ctx => RequestContext.From(ctx.World.Government.CastBallot(ctx.Me, ctx.Str("election", 96), ctx.Id("candidate"))));
            r.Register("civic.file", ctx =>
            {
                if (!Enum.TryParse(ctx.Str("office", 16), out Core.Civic.Office office)) return RequestContext.Fail("Unknown office.");
                var cityHall = CityHall(ctx.World);
                if (cityHall != null && !ctx.Near(cityHall.Position, BuildingReach)) return RequestContext.Fail("File in person at City Hall.");
                return RequestContext.From(ctx.World.Government.FileCandidacy(ctx.Me, office, ctx.OptStr("district", ""), ctx.OptStr("slate", ""), ctx.Connection.Ticket?.DisplayName ?? "Candidate", ctx.Key));
            });
            r.Register("civic.donate", ctx => RequestContext.From(ctx.World.Government.Donate(ctx.Me, ctx.Str("election", 96), ctx.Id("candidate"), new Money(ctx.Long("amount", 1)), ctx.Key)));
            r.Register("civic.campaign", ctx => RequestContext.From(ctx.World.Government.SpendCampaign(ctx.Me, ctx.Str("election", 96), new Money(ctx.Long("amount", 1, 100000000)), ctx.Key)));
            r.Register("civic.propose", ctx => RequestContext.From(ctx.World.Government.Propose(ctx.Me.CharacterId, ctx.Str("ordinance", 64), ctx.OptStr("repeal", "false") == "true")));
            r.Register("civic.council_vote", ctx => RequestContext.From(ctx.World.Government.CastCouncilVote(ctx.Me, ctx.Str("ordinance", 64), ctx.OptStr("aye", "true") != "false")));
            r.Register("civic.budget", ctx =>
            {
                var shares = new Dictionary<Core.Civic.Department, float>();
                foreach (Core.Civic.Department d in Enum.GetValues(typeof(Core.Civic.Department)))
                    if (ctx.Request.Args.ContainsKey(d.ToString())) shares[d] = ctx.Float(d.ToString(), 0f, 1f);
                return RequestContext.From(ctx.World.Government.SetBudget(ctx.Me, shares, ctx.Float("rate", 0.5f, 1.2f)));
            });

            r.Register("admin.kick", ctx => Moderate(ctx, ModerationActionKind.Kick));
            r.Register("admin.ban", ctx => Moderate(ctx, ModerationActionKind.Ban));
            r.Register("admin.unban", ctx => Moderate(ctx, ModerationActionKind.Unban));
            r.Register("admin.mute", ctx => Moderate(ctx, ModerationActionKind.Mute));
            r.Register("admin.unmute", ctx => Moderate(ctx, ModerationActionKind.Unmute));
            r.Register("admin.grant", AdminGrant);
            r.Register("admin.cmd", ctx =>
            {
                if (ctx.Server.Admin == null) return RequestContext.Fail("Admin commands are not available on this server.");
                var line = ctx.Str("line", 200);
                var action = new ModerationAction
                {
                    Kind = ModerationActionKind.AdminCommand, ActorAccountId = ctx.AccountId, Reason = line,
                    RealTimeUnixSeconds = DateTimeOffset.UtcNow.ToUnixTimeSeconds(), GameTime = ctx.World.Clock.Now,
                };
                if (!ctx.Server.Moderation.Perform(action)) return RequestContext.Fail("Not permitted.");
                var output = ctx.Server.Admin.Execute(line, ctx.Me, ctx.Connection.Position, ctx.Connection.Ticket?.DisplayName ?? ctx.AccountId);
                return RequestContext.Ok(new Dictionary<string, string> { ["output"] = output.Length > 4000 ? output.Substring(0, 4000) : output });
            });
            r.Register("admin.role", ctx =>
            {
                var ok = ctx.Server.Moderation.AssignRole(ctx.AccountId, ctx.Str("account", 64), ctx.Str("role", 32));
                return ok ? RequestContext.Ok() : RequestContext.Fail("Not permitted.");
            });
        }

        private static Response Status(RequestContext ctx)
        {
            var me = ctx.Me;
            var data = new Dictionary<string, string>
            {
                ["cash"] = ctx.World.Ledger.BalanceOf(me.CheckingAccount).Cents.ToString(CultureInfo.InvariantCulture),
                ["character"] = me.CharacterId.ToString(),
                ["custody"] = me.Record.InCustody ? "true" : "false",
                ["injury"] = me.Injury.ToString(),
                ["owned"] = ctx.World.Ownership.AssetsOf(me.CharacterId).Count.ToString(CultureInfo.InvariantCulture),
            };
            var wanted = ctx.World.Wanted.Get(me.CharacterId);
            data["wanted"] = (wanted != null ? wanted.Level : 0).ToString(CultureInfo.InvariantCulture);
            return RequestContext.Ok(data);
        }

        /// <summary>
        /// Power use. The origin is always the server's authoritative position (a client cannot fire from somewhere
        /// else); a successful teleport moves that position and tells the client via the next snapshot.
        /// </summary>
        private static Response UsePower(RequestContext ctx)
        {
            if (!Enum.TryParse(ctx.OptStr("target", "None"), out Core.Powers.TargetKind target)) return RequestContext.Fail("Bad target.");
            var request = new PowerUseRequest
            {
                PowerIndex = (int)ctx.Long("power", 0, 8),
                Intensity = ctx.Float("intensity", 0f, 1f),
                Target = target,
                TargetId = ctx.Request.Args.ContainsKey("id") ? ctx.Id("id") : EntityId.None,
                Origin = ctx.Connection.Position,
                Point = ctx.Request.Args.ContainsKey("x")
                    ? new WorldPosition(ctx.Float("x", -20000f, 20000f), ctx.Connection.Position.Y, ctx.Float("z", -20000f, 20000f))
                    : ctx.Connection.Position,
            };
            var outcome = ctx.World.PowerUse.Use(ctx.Me, request);
            if (!outcome.Attempted) return RequestContext.Fail(outcome.Message);
            if (outcome.Teleported)
            {
                ctx.Connection.Position = outcome.TeleportTo;
                ctx.Connection.PendingCorrection = true;
            }
            return RequestContext.Ok(new Dictionary<string, string>
            {
                ["success"] = outcome.Use.Success ? "true" : "false",
                ["backfire"] = outcome.Use.Backfire ? "true" : "false",
                ["effect"] = outcome.Plan.Kind.ToString(),
                ["output"] = outcome.Use.Output.ToString("0.###", CultureInfo.InvariantCulture),
                ["affected"] = outcome.Affected.ToString(CultureInfo.InvariantCulture),
                ["witnesses"] = outcome.Witnesses.ToString(CultureInfo.InvariantCulture),
                ["amount"] = outcome.Plan.Amount.ToString("0.###", CultureInfo.InvariantCulture),
                ["duration"] = outcome.Plan.Duration.ToString("0.###", CultureInfo.InvariantCulture),
                ["impulse"] = outcome.Plan.Impulse.ToString("0", CultureInfo.InvariantCulture),
                ["message"] = outcome.Message.Length > 400 ? outcome.Message.Substring(0, 400) : outcome.Message,
            });
        }

        private static string Clean(string s) => (s ?? "").Replace('|', '/').Replace('\n', ' ');

        /// <summary>
        /// combat.attack: the server supplies both positions (the attacker's from its own movement record, an NPC's from
        /// its schedule, a player's from their connection), so a client can neither reach across town nor fake a target.
        /// </summary>
        private static Response Attack(RequestContext ctx)
        {
            var w = ctx.World;
            var request = new Core.Combat.AttackRequest { WeaponId = ctx.Str("weapon", 64), Origin = ctx.Connection.Position };
            switch (ctx.OptStr("target", "none"))
            {
                case "npc":
                {
                    var npc = w.Population.Get(ctx.Id("id"));
                    if (npc == null || !w.Director.TryGetPosition(w.Schedules.Resolve(npc, w.Clock.Now), out var at)) return RequestContext.Fail("There is nobody there.");
                    request.TargetKind = Core.Combat.AttackTargetKind.Npc;
                    request.Target = npc.Id;
                    request.TargetPosition = at;
                    break;
                }
                case "character":
                {
                    var target = ctx.Id("id");
                    ServerConnection other = null;
                    foreach (var p in ctx.Server.Players) if (p.Character.CharacterId == target) other = p;
                    if (other == null) return RequestContext.Fail("They are not here.");
                    request.TargetKind = Core.Combat.AttackTargetKind.Character;
                    request.Target = target;
                    request.TargetPosition = other.Position;
                    break;
                }
                default:
                    request.TargetPosition = ctx.Connection.Position;
                    break;
            }
            var outcome = w.Combat.Attack(ctx.Me, request);
            if (!outcome.Attempted) return RequestContext.Fail(outcome.Message.Length > 0 ? outcome.Message : "Not ready.");
            var data = new Dictionary<string, string>
            {
                ["hit"] = outcome.Hit ? "true" : "false",
                ["down"] = outcome.TargetDown ? "true" : "false",
                ["killed"] = outcome.TargetKilled ? "true" : "false",
                ["justified"] = outcome.Justified ? "true" : "false",
                ["retaliated"] = outcome.Retaliated ? "true" : "false",
                ["message"] = outcome.Message,
            };
            if (outcome.AmmoLeft >= 0) data["ammo"] = outcome.AmmoLeft.ToString(System.Globalization.CultureInfo.InvariantCulture);
            if (outcome.Crime != null && outcome.Crime.Reported) data["reported"] = "true";
            return RequestContext.Ok(data);
        }

        private static Response AtShop(RequestContext ctx, Func<Core.Business.BusinessRecord, OpResult> action)
        {
            if (!ctx.World.Businesses.TryGetValue(ctx.Id("business"), out var b)) return RequestContext.Fail("Unknown shop.");
            var place = ctx.World.Geography.GetPlace(b.Place);
            if (place == null || !ctx.Near(place.Position, CounterReach)) return RequestContext.Fail("You are not there.");
            return RequestContext.From(action(b));
        }

        private static Core.World.Place CityHall(World w)
        {
            // The metro has a real City Hall; the vertical slice's civic counter is its first government building.
            Core.World.Place best = null;
            foreach (var p in w.Geography.Places)
            {
                if (p.Kind != Core.World.PlaceKind.Government) continue;
                var named = p.Name.IndexOf("City Hall", StringComparison.OrdinalIgnoreCase) >= 0;
                var bestNamed = best != null && best.Name.IndexOf("City Hall", StringComparison.OrdinalIgnoreCase) >= 0;
                if (best == null || named && !bestNamed || named == bestNamed && p.Id.CompareTo(best.Id) < 0) best = p;
            }
            return best;
        }

        private static Response WithBusiness(RequestContext ctx, Func<Core.Business.BusinessRecord, OpResult> action)
        {
            if (!ctx.World.Businesses.TryGetValue(ctx.Id("business"), out var b)) return RequestContext.Fail("Unknown business.");
            return RequestContext.From(action(b));
        }

        private static Response Crime(RequestContext ctx, string target)
        {
            var w = ctx.World;
            var concealment = new ConcealmentState { FaceConcealment = ctx.Connection.MaskOn ? 0.95f : 0f };
            CrimeResult result;
            switch (target)
            {
                case "business":
                {
                    if (!w.Businesses.TryGetValue(ctx.Id("business"), out var b)) return RequestContext.Fail("Unknown business.");
                    var place = w.Geography.GetPlace(b.Place);
                    if (place == null || !ctx.Near(place.Position, CounterReach)) return RequestContext.Fail("You are not there.");
                    result = ctx.Request.Op == "crime.rob" ? w.Crimes.RobStore(ctx.Me, b, concealment) : w.Crimes.Shoplift(ctx.Me, b, concealment);
                    break;
                }
                case "property":
                {
                    var p = w.Properties.Get(ctx.Id("property"));
                    var place = p != null ? w.Geography.GetPlace(p.Place) : null;
                    if (place == null || !ctx.Near(place.Position, BuildingReach)) return RequestContext.Fail("You are not there.");
                    result = w.Crimes.Burglary(ctx.Me, p, concealment);
                    break;
                }
                case "vehicle":
                {
                    var v = w.Vehicles.Get(ctx.Id("vehicle"));
                    if (v == null || !ctx.Near(v.Position, VehicleReach)) return RequestContext.Fail("You are not at that vehicle.");
                    result = w.Crimes.StealVehicle(ctx.Me, v, concealment);
                    break;
                }
                default:
                {
                    var npc = w.Population.Get(ctx.Id("npc"));
                    if (npc == null) return RequestContext.Fail("Nobody there.");
                    // Where the NPC really is comes from their schedule on the server, never from the client.
                    if (!w.Director.TryGetPosition(w.Schedules.Resolve(npc, w.Clock.Now), out var npcPos) || !ctx.Near(npcPos, PickpocketReach))
                        return RequestContext.Fail("They are not within reach.");
                    result = w.Crimes.Pickpocket(ctx.Me, npc, ctx.Connection.Position, concealment);
                    break;
                }
            }
            if (!result.Attempted) return RequestContext.Fail(result.Message);
            return RequestContext.Ok(new Dictionary<string, string>
            {
                ["succeeded"] = result.Succeeded ? "true" : "false",
                ["reported"] = result.Reported ? "true" : "false",
                ["cash"] = result.Cash.Cents.ToString(CultureInfo.InvariantCulture),
                ["loot"] = result.Loot.Count.ToString(CultureInfo.InvariantCulture),
                ["message"] = result.Message.Length > 400 ? result.Message.Substring(0, 400) : result.Message,
            });
        }

        /// <summary>
        /// Build ops arrive as compact CSV (one arg per op: "op0".."op19"): kind,target,floor,x0,z0,x1,z1,wallKind|openingKind|roomType,
        /// width|rotation,catalogId, then room polygon points. Everything is re-validated by the construction service.
        /// </summary>
        private static Response BuildCommit(RequestContext ctx)
        {
            var p = ctx.World.Properties.Get(ctx.Id("property"));
            if (p == null) return RequestContext.Fail("Unknown property.");
            var place = ctx.World.Geography.GetPlace(p.Place);
            if (place == null || !ctx.Near(place.Position, BuildingReach * 2)) return RequestContext.Fail("You must be at the property to build.");
            var ops = new List<BuildOp>();
            for (var i = 0; i < 20; i++)
            {
                if (!ctx.Request.Args.TryGetValue("op" + i, out var csv)) break;
                ops.Add(ParseOp(csv));
            }
            if (ops.Count == 0) return RequestContext.Fail("No changes.");
            return RequestContext.From(ctx.World.Build(p, ops, ctx.Me, ctx.Key));
        }

        public static string EncodeOp(BuildOp op)
        {
            var parts = new List<string>
            {
                op.Kind.ToString(), op.TargetId.ToString(CultureInfo.InvariantCulture), op.Floor.ToString(CultureInfo.InvariantCulture),
                F(op.X0), F(op.Z0), F(op.X1), F(op.Z1),
                op.Kind == BuildOpKind.AddWall ? op.WallKind.ToString() : op.Kind == BuildOpKind.AddOpening ? op.OpeningKind.ToString() : op.RoomType.ToString(),
                F(op.Kind == BuildOpKind.AddOpening ? op.Width : op.Rotation), op.CatalogId ?? "", F(op.Offset),
            };
            foreach (var v in op.Polygon) parts.Add(F(v));
            return string.Join(",", parts);
        }

        public static BuildOp ParseOp(string csv)
        {
            var p = csv.Split(',');
            if (p.Length < 11 || p.Length > 11 + 32) throw new ArgumentException("Malformed build op.");
            if (!Enum.TryParse(p[0], out BuildOpKind kind)) throw new ArgumentException("Unknown build op.");
            var op = new BuildOp
            {
                Kind = kind,
                TargetId = int.Parse(p[1], CultureInfo.InvariantCulture),
                Floor = Math.Max(0, Math.Min(9, int.Parse(p[2], CultureInfo.InvariantCulture))),
                X0 = P(p[3]), Z0 = P(p[4]), X1 = P(p[5]), Z1 = P(p[6]),
                CatalogId = p[9],
                Offset = P(p[10]),
            };
            switch (kind)
            {
                case BuildOpKind.AddWall:
                    if (!Enum.TryParse(p[7], out WallKind wk)) throw new ArgumentException("Bad wall kind.");
                    op.WallKind = wk;
                    break;
                case BuildOpKind.AddOpening:
                    if (!Enum.TryParse(p[7], out OpeningKind ok)) throw new ArgumentException("Bad opening kind.");
                    op.OpeningKind = ok;
                    op.Width = P(p[8]);
                    break;
                case BuildOpKind.AddRoom:
                    if (!Enum.TryParse(p[7], out RoomType rt)) throw new ArgumentException("Bad room type.");
                    op.RoomType = rt;
                    break;
                default:
                    op.Rotation = P(p[8]);
                    break;
            }
            for (var i = 11; i < p.Length; i++) op.Polygon.Add(P(p[i]));
            return op;
        }

        private static string F(float v) => v.ToString("R", CultureInfo.InvariantCulture);

        private static float P(string s)
        {
            if (!float.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var v) || float.IsNaN(v) || float.IsInfinity(v) || Math.Abs(v) > 1000f)
                throw new ArgumentException("Bad number in build op.");
            return v;
        }

        private static Response Moderate(RequestContext ctx, ModerationActionKind kind)
        {
            var target = ctx.Str("account", 64);
            var minutes = ctx.Request.Args.ContainsKey("minutes") ? ctx.Long("minutes", 0, 60L * 24 * 365) : 0;
            var action = new ModerationAction
            {
                Kind = kind,
                ActorAccountId = ctx.AccountId,
                TargetAccountId = target,
                Reason = ctx.OptStr("reason"),
                RealTimeUnixSeconds = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
                GameTime = ctx.World.Clock.Now,
                DurationSeconds = minutes * 60,
            };
            if (!ctx.Server.Moderation.Perform(action)) return RequestContext.Fail("Not permitted.");
            var online = ctx.Server.FindByAccount(target);
            if (online != null && (kind == ModerationActionKind.Kick || kind == ModerationActionKind.Ban))
                ctx.Server.Kick(online, (kind == ModerationActionKind.Ban ? "Banned" : "Kicked") + (string.IsNullOrEmpty(action.Reason) ? "." : ": " + action.Reason));
            return RequestContext.Ok();
        }

        /// <summary>Audited admin money grant (WorldAdmin permission): recorded in the moderation log and the ledger.</summary>
        private static Response AdminGrant(RequestContext ctx)
        {
            var amount = new Money(ctx.Long("amount", 1, 100_000_000_00L));
            var reason = ctx.Str("reason", 200);
            var action = new ModerationAction
            {
                Kind = ModerationActionKind.MoneyGrant, ActorAccountId = ctx.AccountId, TargetAccountId = ctx.OptStr("account", ctx.AccountId),
                Reason = amount + " — " + reason, RealTimeUnixSeconds = DateTimeOffset.UtcNow.ToUnixTimeSeconds(), GameTime = ctx.World.Clock.Now,
            };
            if (!ctx.Server.Moderation.Perform(action)) return RequestContext.Fail("Not permitted.");
            var target = ctx.Server.FindByAccount(action.TargetAccountId);
            var character = target != null ? target.Character : ctx.Me;
            return RequestContext.From(ctx.World.AdminGrant(character.CheckingAccount, amount, ctx.AccountId, reason));
        }
    }
}
