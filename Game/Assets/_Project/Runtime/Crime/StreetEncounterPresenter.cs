using System.Collections.Generic;
using HeroGame.Core.Foundation;
using HeroGame.Runtime.Bootstrap;
using HeroGame.Runtime.Online;
using HeroGame.Runtime.Player;
using HeroGame.Runtime.Population;
using HeroGame.Runtime.UI;
using UnityEngine;

namespace HeroGame.Runtime.Crime
{
    /// <summary>
    /// Shows a street encounter: the mugger steps out in front of the player; E hands the money over, attacking
    /// fights back (self-defence), running clears it. Offline the local world decides; online the server does.
    /// Placeholder presentation (an NPC avatar, no animation) until the animation set exists.
    /// </summary>
    public sealed class StreetEncounterPresenter : MonoBehaviour
    {
        public Transform Player;
        public NpcAvatar AvatarPrefab;

        private IPlayerInputSource _input;
        private NpcAvatar _mugger;
        private string _shownFor = "";

        public static bool Active { get; private set; }

        /// <summary>Card text for the HUD ("" when nothing is happening).</summary>
        public static string Card { get; private set; } = "";

        private void Update()
        {
            if (_input == null) _input = PlayerInputRegistry.Create();
            if (!ServiceRegistry.TryGet<GameSession>(out var session) || session.LocalCharacter == null || Player == null)
            {
                Clear();
                return;
            }

            string muggerId, name, weapon;
            long demand, secondsLeft;
            var online = NetworkSession.Me;
            if (NetworkSession.Connected)
            {
                var e = online?.Encounter;
                if (e == null) { Clear(); return; }
                muggerId = e.Mugger; name = e.Name; demand = e.DemandCents; weapon = e.Weapon; secondsLeft = e.SecondsLeft;
            }
            else
            {
                var e = session.World.StreetCrime.ActiveFor(session.LocalCharacter.CharacterId);
                if (e == null) { Clear(); return; }
                muggerId = e.Mugger.ToString(); name = e.MuggerName; demand = e.DemandCents; weapon = e.WeaponId;
                secondsLeft = System.Math.Max(0, e.DeadlineSecond - session.World.Clock.Now.TotalSeconds);
            }

            Active = true;
            Card = name.Split(' ')[0] + (weapon == "fists" ? "" : " has a knife") + " and wants " + new Money(demand) + ".   [E] hand it over   ·   [Attack] fight   ·   run to get away" +
                   (secondsLeft > 0 ? "   (" + secondsLeft + "s)" : "");
            if (_shownFor != muggerId) Spawn(session, muggerId, name);
            if (_mugger != null)
            {
                var to = Player.position - _mugger.transform.position;
                to.y = 0f;
                if (to.sqrMagnitude > 0.01f) _mugger.transform.rotation = Quaternion.LookRotation(to);
            }

            if (UiFocus.Active || !_input.Read().InteractPressed) return;
            if (NetworkSession.Connected) _ = NetworkSession.Current.Request("encounter.comply");
            else
            {
                var r = session.World.StreetCrime.Comply(session.LocalCharacter, session.NextRequestKey("comply"));
                if (!r.Success) SubtitleFeed.Say("", r.Error, 3f);
            }
        }

        private void Spawn(GameSession session, string muggerId, string name)
        {
            Clear(keepCard: true);
            _shownFor = muggerId;
            if (AvatarPrefab == null)
            {
                var population = FindFirstObjectByType<NpcPopulationPresenter>();
                if (population != null) AvatarPrefab = population.AvatarPrefab;
            }
            if (AvatarPrefab == null) return;
            var at = Player.position + Player.forward * 2.2f;
            _mugger = Instantiate(AvatarPrefab, at, Quaternion.identity, transform);
            EntityId.TryParse(muggerId, out var id);
            var record = session.World.Population.Get(id);
            if (record != null) _mugger.Bind(record, Core.Population.SimulationTier.Full, at);
            else _mugger.name = name;
        }

        private void Clear(bool keepCard = false)
        {
            if (_mugger != null) Destroy(_mugger.gameObject);
            _mugger = null;
            _shownFor = "";
            if (keepCard) return;
            Active = false;
            Card = "";
        }
    }
}
