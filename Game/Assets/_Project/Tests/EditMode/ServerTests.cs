using System.Collections.Generic;
using HeroGame.Core.Servers;
using NUnit.Framework;

namespace HeroGame.Tests
{
    public class ServerTests
    {
        private static List<ServerListing> Listings()
        {
            return new List<ServerListing>
            {
                new ServerListing { ServerId = "a", Name = "Arden Official #1", Region = "NA-South", Population = 60, MaxPopulation = 64, PingMs = 40, Kind = ServerKind.Official, Version = "0.1.3" },
                new ServerListing { ServerId = "b", Name = "Bayou RP", Region = "NA-South", Population = 30, MaxPopulation = 128, PingMs = 70, Roleplay = true, Kind = ServerKind.Community, Version = "0.1.0", FriendsOnline = { "Jess" } },
                new ServerListing { ServerId = "c", Name = "No Capes", Region = "EU-West", Population = 0, MaxPopulation = 32, PingMs = 140, Powers = PowerFrequency.None, Version = "0.1.0" },
                new ServerListing { ServerId = "d", Name = "Old Build", Region = "NA-South", Population = 12, MaxPopulation = 32, PingMs = 50, Version = "0.0.9" },
                new ServerListing { ServerId = "e", Name = "Locked", Region = "NA-South", Population = 64, MaxPopulation = 64, PingMs = 30, PasswordProtected = true, Version = "0.1.0" },
            };
        }

        [Test]
        public void Browser_FiltersAndSorts()
        {
            var all = ServerBrowser.Apply(Listings(), new ServerFilter(), ServerSort.Population, "0.1.5");
            Assert.AreEqual(4, all.Count, "incompatible versions hidden by default");
            Assert.AreEqual("e", all[0].ServerId);

            var rp = ServerBrowser.Apply(Listings(), new ServerFilter { Roleplay = true }, ServerSort.Ping, "0.1.5");
            Assert.AreEqual(1, rp.Count);

            var friends = ServerBrowser.Apply(Listings(), new ServerFilter { FriendsOnly = true }, ServerSort.Friends, "0.1.5");
            Assert.AreEqual("b", friends[0].ServerId);

            var noPowers = ServerBrowser.Apply(Listings(), new ServerFilter { PowersEnabled = false }, ServerSort.Name, "0.1.5");
            Assert.AreEqual("c", noPowers[0].ServerId);

            var open = ServerBrowser.Apply(Listings(), new ServerFilter { HideFull = true, HidePasswordProtected = true, HideEmpty = true }, ServerSort.Ping, "0.1.5");
            Assert.AreEqual(2, open.Count);
            Assert.AreEqual("a", open[0].ServerId, "sorted by ping");

            var search = ServerBrowser.Apply(Listings(), new ServerFilter { Search = "bayou" }, ServerSort.Name, "0.1.5");
            Assert.AreEqual(1, search.Count);
        }

        [Test]
        public void Moderation_EnforcesRanksAndLogsActions()
        {
            var mod = new ModerationService();
            Assert.IsTrue(mod.AssignRole("", "owner", "owner", bootstrap: true));
            Assert.IsTrue(mod.AssignRole("owner", "alice", "admin"));
            Assert.IsTrue(mod.AssignRole("alice", "bob", "moderator"));
            Assert.IsFalse(mod.AssignRole("alice", "carol", "admin"), "cannot grant own rank");
            Assert.IsFalse(mod.AssignRole("bob", "carol", "moderator"), "moderators cannot manage roles");

            Assert.IsTrue(mod.Perform(new ModerationAction { Kind = ModerationActionKind.Kick, ActorAccountId = "bob", TargetAccountId = "griefer" }));
            Assert.IsFalse(mod.Perform(new ModerationAction { Kind = ModerationActionKind.Ban, ActorAccountId = "bob", TargetAccountId = "griefer" }), "moderators cannot ban");
            Assert.IsFalse(mod.Perform(new ModerationAction { Kind = ModerationActionKind.Kick, ActorAccountId = "bob", TargetAccountId = "alice" }), "cannot act on superiors");

            Assert.IsTrue(mod.Perform(new ModerationAction { Kind = ModerationActionKind.Ban, ActorAccountId = "alice", TargetAccountId = "griefer", RealTimeUnixSeconds = 1000, DurationSeconds = 3600 }));
            Assert.IsTrue(mod.IsBanned("griefer", 2000));
            Assert.IsFalse(mod.IsBanned("griefer", 5000));
            Assert.AreEqual(2, mod.History.Count);
        }
    }
}
