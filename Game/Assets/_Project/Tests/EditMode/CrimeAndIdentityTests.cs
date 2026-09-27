using System.Collections.Generic;
using HeroGame.Core.Crime;
using HeroGame.Core.Foundation;
using HeroGame.Core.Identity;
using HeroGame.Core.Time;
using NUnit.Framework;

namespace HeroGame.Tests
{
    public class CrimeAndIdentityTests
    {
        private static readonly EntityId Suspect = EntityId.Create(EntityKind.Character, 1);
        private static readonly GameDateTime T0 = GameDateTime.FromCalendar(2030, 5, 6, 22, 0);

        private static CrimeType Robbery() => new CrimeType { Id = "store_robbery", Severity = 6, ReportLikelihood = 0.95f };
        private static CrimeType Shoplift() => new CrimeType { Id = "shoplifting", Severity = 1, ReportLikelihood = 0.5f };

        private static CrimeIncident Incident() => new CrimeIncident
        {
            Id = EntityId.Create(EntityKind.CrimeIncident, 1),
            Perpetrator = Suspect,
            Position = new WorldPosition(0, 0, 0),
            OccurredAt = T0,
        };

        [Test]
        public void NoReport_NoWantedLevel()
        {
            var wanted = new WantedSystem();
            Assert.IsNull(wanted.Get(Suspect));
            wanted.ObserveSuspect(Suspect, new WorldPosition(), T0);
            Assert.IsNull(wanted.Get(Suspect), "police cannot chase someone for a crime they don't know about");
        }

        [Test]
        public void UnidentifiedSuspect_OnlyTriggersInvestigation()
        {
            var wanted = new WantedSystem();
            var status = wanted.ReportCrime(Incident(), Robbery(), T0, policeWitnessed: false);
            Assert.AreEqual(WantedPhase.Investigating, status.Phase);
            Assert.LessOrEqual(status.Level, 1);
            // Officers see the (unidentified) suspect: no pursuit without identification.
            wanted.ObserveSuspect(Suspect, new WorldPosition(10, 0, 0), T0.AddMinutes(2));
            Assert.AreEqual(WantedPhase.Investigating, wanted.Get(Suspect).Phase);
        }

        [Test]
        public void Evidence_EscalatesToPursuit_AndLevelFollowsSeverity()
        {
            var wanted = new WantedSystem();
            wanted.ReportCrime(Incident(), Robbery(), T0, false);
            wanted.AddEvidence(new EvidenceItem { Suspect = Suspect, Kind = EvidenceKind.Cctv, Confidence = 0.7f });
            wanted.ObserveSuspect(Suspect, new WorldPosition(10, 0, 0), T0.AddMinutes(2));
            var robbery = wanted.Get(Suspect);
            Assert.AreEqual(WantedPhase.Pursuit, robbery.Phase);

            var petty = new WantedSystem();
            petty.ReportCrime(Incident(), Shoplift(), T0, true);
            Assert.Greater(robbery.Level, petty.Get(Suspect).Level);

            wanted.AssaultedOfficer(Suspect);
            Assert.Greater(wanted.Get(Suspect).Level, robbery.Level - 1);
        }

        [Test]
        public void EscapingSearchArea_EndsEpisode_WithWarrantIfIdentified()
        {
            var wanted = new WantedSystem();
            var warrants = new List<EntityId>();
            wanted.WarrantIssued += warrants.Add;
            wanted.ReportCrime(Incident(), Robbery(), T0, policeWitnessed: true);
            wanted.AddEvidence(new EvidenceItem { Suspect = Suspect, Kind = EvidenceKind.Eyewitness, Confidence = 0.9f });

            // Suspect breaks line of sight; search radius grows but suspect is still close.
            wanted.Tick(T0.AddMinutes(2), id => new WorldPosition(50, 0, 0));
            Assert.AreEqual(WantedPhase.Searching, wanted.Get(Suspect).Phase);

            // Suspect gets well outside the search area.
            wanted.Tick(T0.AddMinutes(4), id => new WorldPosition(5000, 0, 0));
            Assert.IsNull(wanted.Get(Suspect));
            Assert.AreEqual(1, warrants.Count);
        }

        [Test]
        public void Masks_ReduceIdentification_AndCostumesPointAtTheAlias()
        {
            var observers = new List<Observer>
            {
                new Observer { Id = EntityId.Create(EntityKind.Npc, 5), Distance = 5, Visibility = 1, Civic = 0.8f },
                new Observer { Id = EntityId.Create(EntityKind.Place, 9), IsCamera = true, Distance = 8, Visibility = 1, Monitored = true },
            };
            var bare = WitnessModel.Evaluate(Incident(), Robbery(), observers, new ConcealmentState(), 0.6f, new DeterministicRandom(1));
            var masked = WitnessModel.Evaluate(Incident(), Robbery(), observers, new ConcealmentState { FaceConcealment = 0.9f }, 0.6f, new DeterministicRandom(1));
            var costumed = WitnessModel.Evaluate(Incident(), Robbery(), observers, new ConcealmentState { FaceConcealment = 1f, InAliasCostume = true }, 0.6f, new DeterministicRandom(1));

            float Max(WitnessOutcome o) { var m = 0f; foreach (var e in o.Evidence) m = System.Math.Max(m, e.Confidence); return m; }
            Assert.Greater(Max(bare), Max(masked) * 2);
            Assert.IsTrue(bare.Reported, "a monitored camera always reports");
            Assert.IsTrue(costumed.Evidence.TrueForAll(e => e.PointsToAlias));

            var wanted = new WantedSystem();
            foreach (var e in costumed.Evidence) wanted.AddEvidence(e);
            Assert.AreEqual(0f, wanted.IdentificationOf(Suspect), "costume evidence does not identify the civilian");
        }

        [Test]
        public void SecretIdentity_IsNeverDiscoveredAutomatically()
        {
            var alias = new AliasIdentity { Alias = "Nightjar" };
            var detective = EntityId.Create(EntityKind.Npc, 11);
            IdentityDiscovery.AddClue(alias, detective, ClueKind.LocationPattern, T0);
            Assert.AreEqual(IdentityExposure.Unknown, IdentityDiscovery.BeliefLevel(alias, detective));
            IdentityDiscovery.AddClue(alias, detective, ClueKind.VehicleLink, T0);
            Assert.AreEqual(IdentityExposure.Suspected, IdentityDiscovery.BeliefLevel(alias, detective));
            IdentityDiscovery.AddClue(alias, detective, ClueKind.Photograph, T0);
            IdentityDiscovery.AddClue(alias, detective, ClueKind.SeenChanging, T0);
            Assert.AreEqual(IdentityExposure.PartiallyDiscovered, IdentityDiscovery.BeliefLevel(alias, detective));
            Assert.AreNotEqual(IdentityExposure.PubliclyKnown, alias.PublicExposure, "one person knowing is not the public knowing");
            Assert.AreEqual(IdentityExposure.Unknown, IdentityDiscovery.BeliefLevel(alias, EntityId.Create(EntityKind.Npc, 12)));
        }

        [Test]
        public void SecretIdentity_SpreadsThroughCorroboratedSharing()
        {
            var alias = new AliasIdentity();
            var witnessA = EntityId.Create(EntityKind.Npc, 1);
            var witnessB = EntityId.Create(EntityKind.Npc, 2);
            IdentityDiscovery.AddClue(alias, witnessA, ClueKind.Unmasked, T0);

            // One source telling people only creates suspicion.
            for (ulong i = 10; i < 20; i++) IdentityDiscovery.Share(alias, witnessA, EntityId.Create(EntityKind.Npc, i), 1f, T0);
            Assert.AreEqual(IdentityExposure.Suspected, IdentityDiscovery.BeliefLevel(alias, EntityId.Create(EntityKind.Npc, 10)));
            Assert.AreNotEqual(IdentityExposure.PubliclyKnown, alias.PublicExposure);

            // A second, independent witness corroborates: listeners become believers and it goes public.
            IdentityDiscovery.AddClue(alias, witnessB, ClueKind.SeenChanging, T0);
            for (ulong i = 10; i < 20; i++) IdentityDiscovery.Share(alias, witnessB, EntityId.Create(EntityKind.Npc, i), 1f, T0);
            var listener = alias.Knowledge.Find(k => k.Observer == EntityId.Create(EntityKind.Npc, 10));
            Assert.GreaterOrEqual(listener.Belief, IdentityDiscovery.DiscoveredThreshold);
            Assert.AreEqual(IdentityExposure.PubliclyKnown, alias.PublicExposure);
            Assert.AreEqual(IdentityExposure.PubliclyKnown, IdentityDiscovery.BeliefLevel(alias, EntityId.Create(EntityKind.Npc, 999)), "public means everyone");
        }

        [Test]
        public void Reputation_HasIndependentAxesWithDiminishingReturns()
        {
            var r = new ReputationProfile();
            r.Add(ReputationDimension.Criminal, 30);
            r.Add(ReputationDimension.Business, -10);
            Assert.AreEqual(30f, r.Get(ReputationDimension.Criminal), 0.01f);
            Assert.AreEqual(-10f, r.Get(ReputationDimension.Business), 0.01f);
            Assert.AreEqual(0f, r.Get(ReputationDimension.Heroic));
            for (var i = 0; i < 100; i++) r.Add(ReputationDimension.Criminal, 50);
            Assert.LessOrEqual(r.Get(ReputationDimension.Criminal), 100f);
        }
    }
}
