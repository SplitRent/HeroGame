using System;
using System.Collections.Generic;
using HeroGame.Core.Characters;
using HeroGame.Core.Crime;
using HeroGame.Core.Economy;
using HeroGame.Core.Foundation;
using HeroGame.Core.Identity;
using HeroGame.Core.Vehicles;
using HeroGame.Core.World;
using PhoneCategory = HeroGame.Core.Phone.MessageCategory;

namespace HeroGame.Core.Simulation
{
    /// <summary>
    /// The legal system (GDD §32): arrests, charging decisions from evidence on file, bail, counsel and plea
    /// offers, hearings with evidence-weighted verdicts, sentencing (fines, jail, probation), custody and release,
    /// warrants for identified suspects and unpaid fines. Runs daily like everything else, so a case proceeds
    /// while the defendant is offline.
    /// </summary>
    public sealed class JusticeService
    {
        public const string CourtName = "Municipal Court";
        public const int FinePaymentDays = 30;

        private readonly World _w;

        public JusticeService(World world)
        {
            _w = world;
            _w.Wanted.WarrantIssued += OnWarrantIssued;
        }

        private JusticeState S => _w.Justice;

        private void OnWarrantIssued(EntityId suspect)
        {
            if (!_w.Characters.TryGetValue(suspect, out var c)) return;
            c.Record.ActiveWarrant = true;
            _w.Dirty.Mark(SaveChunks.CharacterPrefix + c.CharacterId);
        }

        private bool AliasLinked(ServerCharacter c) =>
            c.Alias != null && c.Alias.PublicExposure >= IdentityExposure.PartiallyDiscovered;

        /// <summary>Incidents the prosecutor can charge this person with right now (evidence ≥ threshold, not yet charged).</summary>
        public List<CrimeIncident> ChargeableIncidents(ServerCharacter c)
        {
            var charged = new HashSet<EntityId>();
            foreach (var ch in c.Record.Charges) charged.Add(ch.Incident);
            var list = new List<CrimeIncident>();
            var linked = AliasLinked(c);
            foreach (var i in S.Incidents)
            {
                if (i.Perpetrator != c.CharacterId || charged.Contains(i.Id) || !i.ReportedToPolice) continue;
                if (S.StrengthFor(i.Id, c.CharacterId, linked) >= SentencingGuidelines.ChargeThreshold) list.Add(i);
            }
            return list;
        }

        /// <summary>
        /// A player who disconnects while police are chasing them has fled: the officers' observation becomes an
        /// evading charge and evidence, and the manhunt carries on while they are away (it is saved with the world).
        /// </summary>
        public void LeftDuringPursuit(ServerCharacter c)
        {
            var wanted = _w.Wanted.Get(c.CharacterId);
            if (wanted == null || wanted.Phase != WantedPhase.Pursuit || c.Record.InCustody) return;
            var evading = new CrimeIncident
            {
                Id = _w.Ids.Next(EntityKind.CrimeIncident), CrimeTypeId = "evading_police", Perpetrator = c.CharacterId, OccurredAt = _w.Clock.Now,
                ReportedToPolice = true, ReportedAt = _w.Clock.Now, Position = c.LastPosition, District = _w.Crimes.DistrictAt(c.LastPosition),
            };
            S.Incidents.Add(evading);
            var e = new EvidenceItem { Kind = EvidenceKind.PoliceObservation, Incident = evading.Id, Suspect = c.CharacterId, Confidence = 0.9f, CollectedAt = _w.Clock.Now, Note = "Fled by disconnecting" };
            S.Evidence.Add(e);
            _w.Wanted.AddEvidence(e);
            _w.Dirty.Mark(SaveChunks.Justice);
        }

        /// <summary>
        /// Police take the character into custody. When officers were in pursuit they add their own observation of
        /// the offences reported this episode. Stolen goods and contraband are seized.
        /// </summary>
        public OpResult Arrest(ServerCharacter c, bool caughtInPursuit, bool resisted)
        {
            if (c.Record.InCustody) return OpResult.Fail("Already in custody.");
            var wanted = _w.Wanted.Get(c.CharacterId);
            if (caughtInPursuit && wanted != null)
            {
                foreach (var i in S.Incidents)
                {
                    if (i.Perpetrator != c.CharacterId || !i.ReportedToPolice || i.Solved || i.ReportedAt < wanted.LastSeen.AddHours(-2)) continue;
                    var e = new EvidenceItem { Kind = EvidenceKind.PoliceObservation, Incident = i.Id, Suspect = c.CharacterId, Confidence = 0.85f, CollectedAt = _w.Clock.Now, Note = "Arresting officer" };
                    S.Evidence.Add(e);
                }
            }
            var incidents = ChargeableIncidents(c);
            // A firearm carried without a permit is its own offence, found at the search.
            if (!_w.Combat.HasLicense(c, CombatService.FirearmPermit))
                foreach (var weapon in _w.Content.Weapons)
                {
                    if (!string.IsNullOrEmpty(weapon.RequiresLicense) && CombatService.Count(c, weapon.ItemId) > 0)
                    {
                        var carrying = new CrimeIncident
                        {
                            Id = _w.Ids.Next(EntityKind.CrimeIncident), CrimeTypeId = "unlicensed_firearm", Perpetrator = c.CharacterId, OccurredAt = _w.Clock.Now,
                            ReportedToPolice = true, ReportedAt = _w.Clock.Now, Position = c.LastPosition,
                        };
                        S.Incidents.Add(carrying);
                        S.Evidence.Add(new EvidenceItem { Kind = EvidenceKind.PoliceObservation, Incident = carrying.Id, Suspect = c.CharacterId, Confidence = 1f, CollectedAt = _w.Clock.Now, Note = "Found at search" });
                        incidents.Add(carrying);
                        c.Inventory.RemoveAll(s => s.ItemId == weapon.ItemId || s.ItemId == weapon.AmmoItemId);
                        break;
                    }
                }
            if (resisted)
            {
                var evading = new CrimeIncident
                {
                    Id = _w.Ids.Next(EntityKind.CrimeIncident), CrimeTypeId = "evading_police", Perpetrator = c.CharacterId, OccurredAt = _w.Clock.Now,
                    ReportedToPolice = true, ReportedAt = _w.Clock.Now, Position = c.LastPosition,
                };
                S.Incidents.Add(evading);
                S.Evidence.Add(new EvidenceItem { Kind = EvidenceKind.PoliceObservation, Incident = evading.Id, Suspect = c.CharacterId, Confidence = 0.95f, CollectedAt = _w.Clock.Now });
                incidents.Add(evading);
            }
            var fineWarrant = c.Record.WarrantForFines;
            if (incidents.Count == 0 && !fineWarrant)
            {
                _w.Wanted.Arrested(c.CharacterId);
                return OpResult.Fail("Released: police have nothing they can charge you with.");
            }

            // Seize stolen goods and contraband.
            var seized = c.Inventory.RemoveAll(s => s.Stolen || (_w.Content.FindItem(s.ItemId) is ItemDefinition d && !d.Legal));
            // Recover any stolen vehicle the suspect is holding.
            foreach (var v in _w.Vehicles.All)
                if (v.StolenBy == c.CharacterId) Recover(v);

            c.Record.Arrests++;
            c.Record.ActiveWarrant = false;
            c.Record.InCustody = true;
            c.Record.CustodyUntilDay = -1;
            c.Record.ServingSentence = false;
            _w.Wanted.Arrested(c.CharacterId);

            if (incidents.Count == 0)
            {
                // Warrant only for unpaid fines: pay or serve a day per $250 owed (capped), then released.
                var days = (int)Math.Min(10, Math.Max(1, c.Record.FinesOwedCents / 25000));
                c.Record.ServingSentence = true;
                c.Record.CustodyUntilDay = _w.Today + Math.Max(1, (int)Math.Round(days * _w.Config.Gameplay.SentenceScale * 10));
                c.Record.WarrantForFines = false;
                c.Record.FinesOwedCents = 0;
                c.Record.FinesDueDay = -1;
                Message(c, "Arrested on a warrant for unpaid fines. You will serve " + (c.Record.CustodyUntilDay - _w.Today) + " day(s) in lieu of payment.");
                Dirty(c);
                return OpResult.Ok();
            }

            var court = new CourtCase
            {
                Id = _w.Ids.Next(EntityKind.CourtCase),
                Defendant = c.CharacterId,
                ArrestDay = _w.Today,
                HearingDay = _w.Today + SentencingGuidelines.HearingDelayDays,
                Stage = CaseStage.Charged,
            };
            var types = new List<CrimeType>();
            var miss = 1.0;
            var linked = AliasLinked(c);
            foreach (var i in incidents)
            {
                var charge = new Charge { CrimeTypeId = i.CrimeTypeId, Incident = i.Id, FiledAt = _w.Clock.Now };
                court.Charges.Add(charge);
                c.Record.Charges.Add(charge);
                i.Solved = true;
                var type = _w.Content.FindCrime(i.CrimeTypeId);
                if (type != null) types.Add(type);
                miss *= 1.0 - Math.Min(0.99, S.StrengthFor(i.Id, c.CharacterId, linked));
            }
            // The case is as strong as its best-proven charge, helped a little by the rest.
            court.EvidenceStrength = (float)Math.Min(0.99, 1.0 - Math.Sqrt(miss));
            court.BailCents = SentencingGuidelines.Bail(types, c.Record, c.Record.OnProbation(_w.Clock.Now));
            court.PleaOffered = court.EvidenceStrength >= 0.4f;
            if (court.BailCents < 0) court.Stage = CaseStage.HeldInCustody;
            S.Cases.Add(court);

            var summary = court.Charges.Count + " charge(s): " + string.Join(", ", ChargeNames(court)) + ". Hearing on day " + court.HearingDay + ". "
                          + (court.BailCents < 0 ? "Held without bail." : "Bail set at " + new Money(court.BailCents) + ".")
                          + (court.PleaOffered ? " The prosecutor offers a plea deal (one third off the sentence)." : "")
                          + (seized > 0 ? " " + seized + " item(s) seized." : "");
            court.Summary = summary;
            Message(c, "Arrested. " + summary);
            if (types.Exists(t => t.Severity >= 6))
                _w.History.Record(_w.Today, HistoryCategory.Crime, 3, "Arrest made in " + types[0].DisplayName.ToLowerInvariant() + " case", "", EntityId.None, court.Id);
            Dirty(c);
            return OpResult.Ok();
        }

        private void Recover(VehicleRecord v)
        {
            v.StolenBy = EntityId.None;
            v.ReportedStolen = false;
            v.StolenSinceDay = -1;
            v.LocationKind = VehicleLocationKind.Impound;
            v.ImpoundFeeCents = 0;
            _w.Dirty.Mark(SaveChunks.Vehicles);
            if (_w.Characters.TryGetValue(_w.Ownership.OwnerOf(v.Id), out var owner))
                Message(owner, "Police recovered your stolen vehicle " + v.Plate + ". Collect it from the impound lot (no fee).");
        }

        /// <summary>Surrendering at a police station: arrested without a resisting charge; cooperation is noted by the court.</summary>
        public OpResult TurnSelfIn(ServerCharacter c)
        {
            var r = Arrest(c, caughtInPursuit: false, resisted: false);
            var open = S.OpenCaseFor(c.CharacterId);
            if (r.Success && open != null) open.PleaOffered = true;
            return r;
        }

        public OpResult PostBail(ServerCharacter c, string idempotencyKey)
        {
            var court = S.OpenCaseFor(c.CharacterId);
            if (court == null || court.Stage != CaseStage.Charged) return OpResult.Fail("There is no bail to post.");
            if (court.BailCents < 0) return OpResult.Fail("Bail was denied.");
            var result = _w.Transactions.Execute(new WorldTransaction
            {
                IdempotencyKey = idempotencyKey ?? "",
                Initiator = c.CharacterId,
                Timestamp = _w.Clock.Now,
                Description = "Bail",
                Money = LedgerTransaction.Transfer(c.CheckingAccount, _w.Accounts.Treasury, new Money(court.BailCents), TransactionReason.Bail, "Bail " + court.Id),
            });
            if (!result.Success) return result;
            court.BailPaid = true;
            court.Stage = CaseStage.OnBail;
            c.Record.InCustody = false;
            Message(c, "Bail posted. You are free until your hearing on day " + court.HearingDay + ". Skipping it forfeits bail and issues a warrant.");
            Dirty(c);
            return result;
        }

        public OpResult HireAttorney(ServerCharacter c, string idempotencyKey)
        {
            var court = S.OpenCaseFor(c.CharacterId);
            if (court == null) return OpResult.Fail("You have no open case.");
            if (court.Counsel == Counsel.PrivateAttorney) return OpResult.Fail("You already have an attorney.");
            var fee = new Money((long)(SentencingGuidelines.PrivateAttorneyFeeCents * _w.Macro.PriceLevel));
            var result = _w.Transactions.Execute(new WorldTransaction
            {
                IdempotencyKey = idempotencyKey ?? "",
                Initiator = c.CharacterId,
                Timestamp = _w.Clock.Now,
                Description = "Attorney retainer",
                Money = LedgerTransaction.Transfer(c.CheckingAccount, _w.Accounts.Contractors, fee, TransactionReason.Fee, "Legal defence"),
            });
            if (result.Success) court.Counsel = Counsel.PrivateAttorney;
            Dirty(c);
            return result;
        }

        public OpResult AcceptPlea(ServerCharacter c)
        {
            var court = S.OpenCaseFor(c.CharacterId);
            if (court == null || !court.PleaOffered) return OpResult.Fail("No plea deal is on offer.");
            court.PleadedGuilty = true;
            Dirty(c);
            return OpResult.Ok();
        }

        public OpResult PayFines(ServerCharacter c, Money amount, string idempotencyKey)
        {
            var pay = Math.Min(amount.Cents, c.Record.FinesOwedCents);
            if (pay <= 0) return OpResult.Fail("You owe no fines.");
            var result = _w.Transactions.Execute(new WorldTransaction
            {
                IdempotencyKey = idempotencyKey ?? "",
                Initiator = c.CharacterId,
                Timestamp = _w.Clock.Now,
                Description = "Court fines",
                Money = LedgerTransaction.Transfer(c.CheckingAccount, _w.Accounts.Treasury, new Money(pay), TransactionReason.Fine, "Fines"),
            });
            if (!result.Success) return result;
            c.Record.FinesOwedCents -= pay;
            if (c.Record.FinesOwedCents <= 0)
            {
                c.Record.FinesDueDay = -1;
                if (c.Record.WarrantForFines)
                {
                    c.Record.WarrantForFines = false;
                    c.Record.ActiveWarrant = false;
                }
            }
            Dirty(c);
            return result;
        }

        // ------------------------------------------------------------------ daily

        public void ProcessDay(long day)
        {
            // Hearings.
            foreach (var court in S.Cases)
            {
                if (court.Stage == CaseStage.Closed || court.HearingDay > day) continue;
                if (!_w.Characters.TryGetValue(court.Defendant, out var c)) continue;
                Hear(court, c, day);
            }
            // Custody, fines and releases.
            foreach (var c in _w.Characters.Values)
            {
                var r = c.Record;
                if (r.InCustody && r.ServingSentence && r.CustodyUntilDay >= 0 && day >= r.CustodyUntilDay)
                {
                    r.InCustody = false;
                    r.ServingSentence = false;
                    r.CustodyUntilDay = -1;
                    Message(c, "Released from custody." + (r.OnProbation(_w.Clock.Now) ? " You are on probation until day " + r.ProbationUntil.DayIndex + "." : ""));
                    Dirty(c);
                }
                if (r.FinesOwedCents > 0 && r.FinesDueDay >= 0 && day >= r.FinesDueDay && !r.WarrantForFines)
                {
                    r.WarrantForFines = true;
                    r.ActiveWarrant = true;
                    Message(c, "Your fines of " + new Money(r.FinesOwedCents) + " are overdue. A warrant has been issued.");
                    Dirty(c);
                }
            }
            S.Trim();
        }

        private void Hear(CourtCase court, ServerCharacter c, long day)
        {
            var types = new List<CrimeType>();
            foreach (var ch in court.Charges)
            {
                var t = _w.Content.FindCrime(ch.CrimeTypeId);
                if (t != null) types.Add(t);
            }
            var onProbation = c.Record.OnProbation(_w.Clock.Now);
            var rng = DeterministicRandom.For(_w.Seed, court.Id.Value, (ulong)day, 0xC0C7);
            bool guilty;
            if (court.PleadedGuilty)
            {
                court.Verdict = Verdict.GuiltyPlea;
                guilty = true;
            }
            else
            {
                guilty = rng.Chance(SentencingGuidelines.ConvictionChance(court.EvidenceStrength, court.Counsel));
                court.Verdict = guilty ? Verdict.Guilty : Verdict.Acquitted;
            }

            var bailRefund = court.BailPaid ? court.BailCents : 0;
            string text;
            if (guilty)
            {
                court.Sentence = SentencingGuidelines.For(types, c.Record, court.PleadedGuilty, onProbation, _w.Config.Gameplay.SentenceScale);
                foreach (var ch in court.Charges) ch.Convicted = true;
                c.Record.Convictions++;
                // Bail is applied to the fine first; any balance is refunded.
                var fine = court.Sentence.FineCents;
                var fromBail = Math.Min(fine, bailRefund);
                bailRefund -= fromBail;
                fine -= fromBail;
                if (fine > 0)
                {
                    c.Record.FinesOwedCents += fine;
                    c.Record.FinesDueDay = day + FinePaymentDays;
                }
                if (court.Sentence.JailDays > 0)
                {
                    c.Record.InCustody = true;
                    c.Record.ServingSentence = true;
                    c.Record.CustodyUntilDay = day + court.Sentence.JailDays;
                }
                else
                {
                    c.Record.InCustody = false;
                }
                if (court.Sentence.ProbationDays > 0)
                    c.Record.ProbationUntil = new Time.GameDateTime(Math.Max(c.Record.ProbationUntil.DayIndex, day + court.Sentence.JailDays + court.Sentence.ProbationDays) * Time.GameDateTime.SecondsPerDay);
                c.Reputation.Add(ReputationDimension.Public, -2f * types.Count);
                text = (court.Verdict == Verdict.GuiltyPlea ? "You pleaded guilty. " : "Found guilty. ") + "Sentence: fine " + new Money(court.Sentence.FineCents)
                       + (court.Sentence.JailDays > 0 ? ", " + court.Sentence.JailDays + " day(s) in custody" : "")
                       + (court.Sentence.ProbationDays > 0 ? ", " + court.Sentence.ProbationDays + " day(s) probation" : "") + ".";
            }
            else
            {
                foreach (var ch in court.Charges) ch.Dismissed = true;
                c.Record.InCustody = false;
                text = "Acquitted: the evidence did not convince the court.";
            }
            if (bailRefund > 0)
                _w.Transactions.Execute(new WorldTransaction
                {
                    Source = TransactionSource.Simulation,
                    Timestamp = _w.Clock.Now,
                    Description = "Bail refund",
                    Money = LedgerTransaction.Transfer(_w.Accounts.Treasury, c.CheckingAccount, new Money(bailRefund), TransactionReason.Refund, "Bail " + court.Id),
                });
            court.Stage = CaseStage.Closed;
            court.Summary += " Outcome: " + text;
            Message(c, text + (bailRefund > 0 ? " Bail refunded: " + new Money(bailRefund) + "." : ""));
            if (types.Exists(t => t.Severity >= 6))
                _w.History.Record(day, HistoryCategory.Crime, 3, guilty ? "Guilty verdict in " + types[0].DisplayName.ToLowerInvariant() + " case" : "Jury acquits in " + types[0].DisplayName.ToLowerInvariant() + " case",
                    "", EntityId.None, court.Id);
            Dirty(c);
        }

        private IEnumerable<string> ChargeNames(CourtCase court)
        {
            foreach (var ch in court.Charges)
            {
                var t = _w.Content.FindCrime(ch.CrimeTypeId);
                yield return t != null ? t.DisplayName.ToLowerInvariant() : ch.CrimeTypeId;
            }
        }

        private void Message(ServerCharacter c, string text) => _w.Phone.Send(c, _w.Accounts.Government, CourtName, PhoneCategory.Server, text);

        private void Dirty(ServerCharacter c)
        {
            _w.Dirty.Mark(SaveChunks.Justice);
            _w.Dirty.Mark(SaveChunks.CharacterPrefix + c.CharacterId);
        }
    }
}
