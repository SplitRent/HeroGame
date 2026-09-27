using HeroGame.Runtime.Bootstrap;
using HeroGame.Runtime.Player;
using HeroGame.Runtime.UI;
using UnityEngine;

namespace HeroGame.Runtime.Crime
{
    /// <summary>
    /// Keeps the player in the holding cell while the core says they are in custody, and returns them to the
    /// station steps on release. Custody itself (hearings, sentence length) is decided by the justice service.
    /// </summary>
    [RequireComponent(typeof(PlayerMotor))]
    public sealed class CustodyPresenter : MonoBehaviour
    {
        public Transform Cell;
        public Transform ReleasePoint;

        private PlayerMotor _motor;
        private bool _held;

        private void Awake() => _motor = GetComponent<PlayerMotor>();

        private void Update()
        {
            if (!ServiceRegistry.TryGet<GameSession>(out var session) || session.LocalCharacter == null) return;
            var inCustody = session.LocalCharacter.Record.InCustody;
            if (inCustody && !_held)
            {
                _held = true;
                if (Cell != null) _motor.Warp(Cell.position, Cell.rotation);
                SubtitleFeed.Say("", "You are in custody. Use the desk to post bail or check your case.", 5f);
            }
            else if (!inCustody && _held)
            {
                _held = false;
                if (ReleasePoint != null) _motor.Warp(ReleasePoint.position, ReleasePoint.rotation);
                SubtitleFeed.Say("", "You walk out of the station.", 3f);
            }
            // The cell door is locked: pull the player back if physics or input moved them out.
            if (_held && Cell != null && (transform.position - Cell.position).sqrMagnitude > 16f) _motor.Warp(Cell.position, Cell.rotation);
        }
    }
}
