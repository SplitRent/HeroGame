using System;
using System.Collections.Generic;
using HeroGame.Core.Characters;
using HeroGame.Core.Economy;
using HeroGame.Core.Foundation;
using HeroGame.Core.Population;
using HeroGame.Core.Time;
using HeroGame.Core.Weather;
using HeroGame.Core.World;
using SimWorld = HeroGame.Core.Simulation.World;

namespace HeroGame.Core.Phone
{
    public enum MessageCategory
    {
        Personal,
        Work,
        Bank,
        News,
        Emergency,
        Server,
        Business,
    }

    [Serializable]
    public sealed class PhoneMessage
    {
        public long Id;
        public EntityId From;
        public string FromName = "";
        public MessageCategory Category;
        public string Body = "";
        public GameDateTime At;
        public bool Read;
    }

    [Serializable]
    public sealed class StatementLine
    {
        public GameDateTime At;
        public EntityId Account;
        public long AmountCents;
        public long BalanceAfterCents;
        public TransactionReason Reason;
        public string Memo = "";
    }

    [Serializable]
    public sealed class PhoneContact
    {
        public EntityId Npc;
        public string Name = "";
        public long AddedDay;
        public bool Favorite;
    }

    /// <summary>
    /// The in-world phone (GDD §69) as data: messages, bank statement, contacts, alerts. Every message is
    /// caused by something that actually happened in the world — a transaction, a news event, a storm
    /// advisory, an NPC who likes you. The Unity phone UI only renders this.
    /// </summary>
    public sealed class PhoneService
    {
        public const int MaxInbox = 200;
        public const int MaxStatement = 150;
        public const long LargeTransactionCents = 100000; // $1,000

        private readonly SimWorld _world;
        private readonly Dictionary<EntityId, EntityId> _accountOwner = new Dictionary<EntityId, EntityId>();

        public event Action<ServerCharacter, PhoneMessage> MessageReceived;

        public PhoneService(SimWorld world)
        {
            _world = world;
            world.Transactions.Committed += OnTransaction;
            world.History.Recorded += OnHistory;
            world.Weather.TropicalSystemAnnounced += OnStormAnnounced;
            world.Weather.TropicalSystemLandfall += OnLandfall;
        }

        public PhoneMessage Send(ServerCharacter to, EntityId from, string fromName, MessageCategory category, string body)
        {
            var msg = new PhoneMessage { Id = ++to.NextMessageId, From = from, FromName = fromName, Category = category, Body = body, At = _world.Clock.Now };
            to.Inbox.Add(msg);
            while (to.Inbox.Count > MaxInbox) to.Inbox.RemoveAt(0);
            _world.Dirty.Mark(Simulation.SaveChunks.CharacterPrefix + to.CharacterId);
            MessageReceived?.Invoke(to, msg);
            return msg;
        }

        public int UnreadCount(ServerCharacter c)
        {
            var n = 0;
            foreach (var m in c.Inbox) if (!m.Read) n++;
            return n;
        }

        public void MarkRead(ServerCharacter c, long messageId)
        {
            foreach (var m in c.Inbox) if (m.Id == messageId) m.Read = true;
        }

        /// <summary>Map app search over real places.</summary>
        public List<Place> SearchMap(string query, int max = 20)
        {
            var results = new List<Place>();
            if (string.IsNullOrWhiteSpace(query)) return results;
            var q = query.ToLowerInvariant();
            foreach (var p in _world.Geography.Places)
            {
                if (p.Name.ToLowerInvariant().Contains(q) || p.Kind.ToString().ToLowerInvariant() == q) results.Add(p);
                if (results.Count >= max) break;
            }
            results.Sort((a, b) => string.CompareOrdinal(a.Name, b.Name));
            return results;
        }

        public List<NewsArticle> News(int days = 3) => NewsDesk.Edition(_world.History, _world.Today - days, _world.Config.Identity.NewsOrganizations, 12);

        /// <summary>
        /// Daily social pass: contacts who like the player occasionally reach out, with invitations to places they
        /// actually frequent. Deterministic per (world, character, day).
        /// </summary>
        public void DailyMessages(long day)
        {
            foreach (var character in _world.Characters.Values)
            {
                var rng = DeterministicRandom.For(_world.Seed, character.CharacterId.Value, (ulong)day, 0x9404E);
                foreach (var contact in character.PhoneContacts)
                {
                    var npc = _world.Population.Get(contact.Npc);
                    if (npc == null || !npc.Alive) continue;
                    var memory = npc.MemoryOf(character.CharacterId, false, day);
                    if (memory == null || memory.Affinity < 0.2f) continue;
                    var chance = 0.02 + memory.Affinity * 0.08 + npc.Personality.Extraversion * 0.04;
                    if (!rng.Chance(chance)) continue;
                    Send(character, npc.Id, npc.FullName, MessageCategory.Personal, Invitation(npc, rng));
                }
            }
        }

        private string Invitation(NpcRecord npc, DeterministicRandom rng)
        {
            if (npc.FavoritePlaces.Count == 0) return "Hey! Been a minute. How've you been?";
            var place = _world.Geography.GetPlace(npc.FavoritePlaces[rng.NextInt(0, npc.FavoritePlaces.Count)]);
            if (place == null) return "Hey, how's it going?";
            var templates = new[]
            {
                "Heading to {0} later if you want to come.",
                "You around? I'll be at {0} this evening.",
                "Haven't seen you in a while. {0} tonight?",
                "Thinking about {0} after work. You in?",
            };
            return string.Format(templates[rng.NextInt(0, templates.Length)], place.Name);
        }

        private void OnTransaction(WorldTransaction tx)
        {
            if (tx.Money == null || _world.Characters.Count == 0) return;
            foreach (var posting in tx.Money.Postings)
            {
                var character = OwnerCharacter(posting.Account);
                if (character == null) continue;
                var balance = _world.Ledger.BalanceOf(posting.Account).Cents;
                character.Statement.Add(new StatementLine
                {
                    At = tx.Timestamp.TotalSeconds != 0 ? tx.Timestamp : _world.Clock.Now,
                    Account = posting.Account,
                    AmountCents = posting.AmountCents,
                    BalanceAfterCents = balance,
                    Reason = tx.Money.Reason,
                    Memo = string.IsNullOrEmpty(tx.Money.Memo) ? tx.Description : tx.Money.Memo,
                });
                while (character.Statement.Count > MaxStatement) character.Statement.RemoveAt(0);
                if (Math.Abs(posting.AmountCents) >= LargeTransactionCents)
                {
                    var verb = posting.AmountCents > 0 ? "Deposit" : "Payment";
                    Send(character, _world.Accounts.BankOrganization, "Gulf Tidewater Bank", MessageCategory.Bank,
                        verb + " of " + new Money(Math.Abs(posting.AmountCents)) + " (" + tx.Money.Reason + "). Balance " + new Money(balance) + ".");
                }
            }
        }

        private ServerCharacter OwnerCharacter(EntityId account)
        {
            if (!_accountOwner.TryGetValue(account, out var owner))
            {
                foreach (var c in _world.Characters.Values)
                {
                    _accountOwner[c.CheckingAccount] = c.CharacterId;
                    _accountOwner[c.SavingsAccount] = c.CharacterId;
                }
                if (!_accountOwner.TryGetValue(account, out owner)) return null;
            }
            return _world.Characters.TryGetValue(owner, out var character) ? character : null;
        }

        private void OnHistory(HistoryRecord r)
        {
            if (r.Importance < 4) return;
            var outlet = _world.Config.Identity.NewsOrganizations.Count > 0 ? _world.Config.Identity.NewsOrganizations[0] : "News";
            foreach (var c in _world.Characters.Values) Send(c, EntityId.None, outlet, MessageCategory.News, "BREAKING: " + r.Headline);
        }

        private void OnStormAnnounced(TropicalSystem s)
        {
            var kind = s.Category >= 1 ? "Hurricane " : "Tropical Storm ";
            var when = new GameDateTime(s.LandfallHour * GameDateTime.SecondsPerHour);
            foreach (var c in _world.Characters.Values)
                Send(c, _world.Accounts.Government, _world.Config.Identity.GovernmentName + " Emergency Management", MessageCategory.Emergency,
                    kind + s.Name + " expected to reach the coast around " + when + ". Secure property, stock water, know your evacuation zone.");
        }

        private void OnLandfall(TropicalSystem s)
        {
            foreach (var c in _world.Characters.Values)
                Send(c, _world.Accounts.Government, _world.Config.Identity.GovernmentName + " Emergency Management", MessageCategory.Emergency,
                    s.Name + " is making landfall. Shelter in place. Do not drive through flooded roads.");
        }
    }
}
