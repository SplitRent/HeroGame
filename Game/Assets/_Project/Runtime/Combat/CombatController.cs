using System.Collections.Generic;
using UnityEngine;

namespace HeroGame.Runtime.Combat
{
    using HeroGame.Core.Audio;
    using HeroGame.Core.Combat;
    using HeroGame.Core.Foundation;
    using HeroGame.Runtime.Audio;
    using HeroGame.Runtime.Bootstrap;
    using HeroGame.Runtime.Online;
    using HeroGame.Runtime.Player;
    using HeroGame.Runtime.Population;
    using HeroGame.Runtime.UI;

    /// <summary>
    /// The player's weapons (GDD crime/action layer): X cycles through what you carry (fists first), left mouse or
    /// the right shoulder attacks whoever is in front of you. Offline the local world resolves the attack; online the
    /// server does (combat.attack) using its own positions. Presentation only: hits, reactions and crimes all come
    /// from CombatService. Placeholder feedback (flinch, procedural sounds, HUD line) until animation and VFX exist.
    /// </summary>
    public sealed class CombatController : MonoBehaviour
    {
        public float AimCone = 0.55f;
        public float MaxAimDistance = 50f;

        private IPlayerInputSource _input;
        private string _weapon = "fists";
        private string _last = "";
        private float _lastShown;
        private readonly Collider[] _hits = new Collider[32];

        public string EquippedWeapon => _weapon;
        public static CombatController Current { get; private set; }

        /// <summary>The last attack's result for the HUD, while it is fresh (else "").</summary>
        public string Feedback => Time.unscaledTime - _lastShown < 3f ? _last : "";

        private void OnEnable() => Current = this;

        private void OnDisable()
        {
            if (Current == this) Current = null;
        }

        private void Update()
        {
            if (_input == null) _input = PlayerInputRegistry.Create();
            if (UiFocus.Active || !_input.GameplayEnabled || !ServiceRegistry.TryGet<GameSession>(out var session) || session.LocalCharacter == null) return;
            var input = _input.Read();
            if (input.NextWeaponPressed) Cycle(session);
            if (input.AttackPressed && !InBuildOrVehicle()) Attack(session);
        }

        private bool InBuildOrVehicle()
        {
            if (Building.BuildModeController.AnyActive) return true;
            return transform.parent != null && transform.parent.GetComponentInParent<Vehicles.VehicleController>() != null;
        }

        private void Cycle(GameSession session)
        {
            var usable = session.World.Combat.Usable(session.LocalCharacter);
            var i = usable.FindIndex(w => w.Id == _weapon);
            _weapon = usable[(i + 1) % usable.Count].Id;
            Say(session.World.Combat.Weapon(_weapon).DisplayName);
        }

        private void Attack(GameSession session)
        {
            var world = session.World;
            var weapon = world.Combat.Weapon(_weapon) ?? world.Combat.Weapon("fists");
            if (!string.IsNullOrEmpty(weapon.ItemId) && Core.Simulation.CombatService.Count(session.LocalCharacter, weapon.ItemId) <= 0) weapon = world.Combat.Weapon(_weapon = "fists");
            var target = FindTarget(weapon.Range + 0.5f, out var npcAvatar, out var remote);
            var origin = transform.position.ToWorld();

            PlaySound(weapon, target != null);
            if (Replica() && NetworkSession.Current != null)
            {
                var args = new Dictionary<string, string> { ["weapon"] = weapon.Id, ["target"] = npcAvatar != null ? "npc" : remote != null ? "character" : "none" };
                if (npcAvatar != null) args["id"] = npcAvatar.NpcId.ToString();
                if (remote != null) args["id"] = remote.CharacterId.ToString();
                _ = NetworkSession.Current.Request("combat.attack", args).ContinueWith(t =>
                {
                    var r = t.Result;
                    Say(r.Success ? (r.Data.TryGetValue("message", out var m) ? m : "") : r.Error);
                    if (r.Success && r.Data.TryGetValue("hit", out var hit) && hit == "true" && npcAvatar != null) npcAvatar.Flinch();
                }, System.Threading.Tasks.TaskScheduler.FromCurrentSynchronizationContext());
                return;
            }

            var request = new AttackRequest { WeaponId = weapon.Id, Origin = origin, TargetPosition = target != null ? target.position.ToWorld() : origin };
            if (npcAvatar != null) { request.TargetKind = AttackTargetKind.Npc; request.Target = npcAvatar.NpcId; }
            var outcome = world.Combat.Attack(session.LocalCharacter, request);
            if (outcome.Hit && npcAvatar != null) npcAvatar.Flinch();
            Say(outcome.Message + (outcome.AmmoLeft >= 0 ? "  (" + outcome.AmmoLeft + " left)" : ""));
            if (outcome.Crime != null && outcome.Crime.Reported) ToastPresenter.Push(Core.Presentation.ToastKind.Danger, "911", "Someone called the police.");
        }

        private static bool Replica() => NetworkSession.Replica != null;

        /// <summary>The nearest NPC or remote player roughly in front of the player within reach.</summary>
        private Transform FindTarget(float range, out NpcAvatar npc, out RemotePlayerTag remote)
        {
            npc = null;
            remote = null;
            var count = Physics.OverlapSphereNonAlloc(transform.position, Mathf.Min(range, MaxAimDistance), _hits, ~0, QueryTriggerInteraction.Collide);
            Transform best = null;
            var bestScore = float.MaxValue;
            var forward = transform.forward;
            var cam = Camera.main;
            if (cam != null) forward = Vector3.ProjectOnPlane(cam.transform.forward, Vector3.up).normalized;
            for (var i = 0; i < count; i++)
            {
                var a = _hits[i].GetComponentInParent<NpcAvatar>();
                var r = a == null ? _hits[i].GetComponentInParent<RemotePlayerTag>() : null;
                if (a == null && r == null) continue;
                var t = a != null ? a.transform : r.transform;
                var to = t.position - transform.position;
                to.y = 0f;
                var dist = to.magnitude;
                if (dist < 0.01f) continue;
                var facing = Vector3.Dot(forward, to / dist);
                if (facing < AimCone) continue;
                var score = dist * (2f - facing);
                if (score >= bestScore) continue;
                bestScore = score;
                best = t;
                npc = a;
                remote = r;
            }
            return best;
        }

        private void PlaySound(WeaponDefinition weapon, bool contact)
        {
            Synth.CombatSound kind;
            switch (weapon.Kind)
            {
                case WeaponKind.Firearm: kind = Synth.CombatSound.Gunshot; break;
                case WeaponKind.Spray: kind = Synth.CombatSound.Spray; break;
                case WeaponKind.Stun: kind = Synth.CombatSound.Zap; break;
                default: kind = !contact ? Synth.CombatSound.Whoosh : weapon.Id == "fists" ? Synth.CombatSound.Punch : weapon.Id == "folding_knife" ? Synth.CombatSound.Blade : Synth.CombatSound.Blunt; break;
            }
            var clip = ProceduralClips.Prefer("combat_" + kind.ToString().ToLowerInvariant(), "combat:" + kind, () => Synth.Combat(kind));
            AudioDirector.PlayAt(clip, transform.position + Vector3.up, AudioChannel.Effects);
        }

        private void Say(string text)
        {
            if (string.IsNullOrEmpty(text)) return;
            _last = text;
            _lastShown = Time.unscaledTime;
        }

        private void OnGUI()
        {
            if (GameUi.Active || !ServiceRegistry.TryGet<GameSession>(out var session) || session.LocalCharacter == null) return;
            var weapon = session.World.Combat.Weapon(_weapon);
            if (weapon == null) return;
            var ammo = weapon.UsesAmmo ? "  " + Core.Simulation.CombatService.Count(session.LocalCharacter, weapon.AmmoItemId) + " rounds" : "";
            var scale = SettingsService.Current.UiScale;
            GUI.Label(new Rect(Screen.width - 260 * scale, Screen.height - 50 * scale, 250 * scale, 22 * scale), "[X] " + weapon.DisplayName + ammo);
            if (Time.unscaledTime - _lastShown < 3f) GUI.Label(new Rect(Screen.width / 2f - 200, Screen.height * 0.62f, 400, 24), _last);
        }
    }
}
