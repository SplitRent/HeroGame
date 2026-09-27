using HeroGame.Runtime.Bootstrap;
using UnityEngine;

namespace HeroGame.Runtime.Player
{
    /// <summary>
    /// Keeps the server-character record in step with the player's body: position for saves, anomaly
    /// exposure and proximity systems; on start, restores the last saved position.
    /// </summary>
    public sealed class PlayerCharacterSync : MonoBehaviour
    {
        public float Interval = 0.5f;
        private float _timer;

        private void Start()
        {
            if (!ServiceRegistry.TryGet<GameSession>(out var session) || session.LocalCharacter == null) return;
            var saved = session.LocalCharacter.LastPosition.ToVector3();
            if (saved.sqrMagnitude > 1f)
            {
                var motor = GetComponent<PlayerMotor>();
                if (motor != null) motor.Warp(saved + Vector3.up * 0.2f, transform.rotation);
                else transform.position = saved;
            }
        }

        private void Update()
        {
            _timer -= Time.deltaTime;
            if (_timer > 0f) return;
            _timer = Interval;
            if (ServiceRegistry.TryGet<GameSession>(out var session) && session.LocalCharacter != null)
                session.LocalCharacter.LastPosition = transform.position.ToWorld();
        }
    }
}
