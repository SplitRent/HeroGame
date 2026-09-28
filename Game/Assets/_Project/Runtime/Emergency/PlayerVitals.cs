using UnityEngine;

namespace HeroGame.Runtime.Emergency
{
    using HeroGame.Core.Characters;
    using HeroGame.Runtime.Bootstrap;
    using HeroGame.Runtime.Player;
    using HeroGame.Runtime.UI;

    /// <summary>
    /// Player health on the client side: fall damage from landing speed and damage reported by other systems
    /// (collisions, combat). At zero the core dispatcher takes over — EMS, hospital, bill — and this component
    /// holds the player still until discharge, then places them at the hospital.
    /// </summary>
    [RequireComponent(typeof(PlayerMotor))]
    public sealed class PlayerVitals : MonoBehaviour
    {
        public float SafeFallSpeed = 12f;
        public float LethalFallSpeed = 26f;

        private PlayerMotor _motor;
        private float _lastVerticalSpeed;
        private bool _wasGrounded = true;
        private bool _incapacitated;

        private void Awake() => _motor = GetComponent<PlayerMotor>();

        public void ApplyDamage(float amount, string cause)
        {
            if (!ServiceRegistry.TryGet<GameSession>(out var session) || session.LocalCharacter == null) return;
            var c = session.LocalCharacter;
            if (!session.World.Config.Gameplay.InjuriesEnabled || c.Injury == InjuryState.Dead || c.Injury == InjuryState.Hospitalized) return;
            c.Health = Mathf.Clamp01(c.Health - amount);
            if (c.Health <= 0f) session.World.Dispatch.CharacterDowned(c, transform.position.ToWorld(), Mathf.Clamp01(amount + 0.3f), cause);
            else if (c.Health < 0.5f) c.Injury = InjuryState.Injured;
        }

        private void Update()
        {
            if (!ServiceRegistry.TryGet<GameSession>(out var session) || session.LocalCharacter == null) return;
            var c = session.LocalCharacter;

            // Fall damage when landing.
            if (_motor.IsGrounded && !_wasGrounded)
            {
                var speed = -_lastVerticalSpeed;
                if (speed > SafeFallSpeed) ApplyDamage((speed - SafeFallSpeed) / (LethalFallSpeed - SafeFallSpeed), "a fall");
            }
            _wasGrounded = _motor.IsGrounded;
            _lastVerticalSpeed = _motor.Velocity.y;

            var down = c.Injury == InjuryState.Incapacitated || c.Injury == InjuryState.Hospitalized || c.Injury == InjuryState.Dead;
            if (down && !_incapacitated)
            {
                _incapacitated = true;
                _motor.enabled = false;
                SubtitleFeed.Say("", c.Injury == InjuryState.Dead ? "Your story on this server has ended." : "Everything goes dark. Paramedics are on the way.", 6f);
            }
            else if (!down && _incapacitated)
            {
                _incapacitated = false;
                _motor.enabled = true;
                _motor.Warp(c.LastPosition.ToVector3() + Vector3.up * 0.2f, transform.rotation);
                SubtitleFeed.Say("", "You leave the hospital.", 4f);
            }
        }
    }
}
