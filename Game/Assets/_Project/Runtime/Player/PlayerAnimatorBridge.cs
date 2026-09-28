using UnityEngine;

namespace HeroGame.Runtime.Player
{
    /// <summary>
    /// Animation framework hook (GDD §93): translates motor state into Animator parameters. The
    /// controller asset defines states; code only feeds parameters, so animators can iterate freely.
    /// Parameters that do not exist on the controller are skipped.
    /// </summary>
    [RequireComponent(typeof(PlayerMotor))]
    public sealed class PlayerAnimatorBridge : MonoBehaviour
    {
        public Animator Animator;
        public float SpeedDamping = 0.08f;

        private static readonly int SpeedId = Animator.StringToHash("Speed");
        private static readonly int GroundedId = Animator.StringToHash("Grounded");
        private static readonly int JumpId = Animator.StringToHash("Jump");
        private static readonly int CrouchId = Animator.StringToHash("Crouch");
        private static readonly int VerticalSpeedId = Animator.StringToHash("VerticalSpeed");

        private PlayerMotor _motor;
        private bool _hasSpeed, _hasGrounded, _hasJump, _hasCrouch, _hasVertical;

        private void Awake()
        {
            _motor = GetComponent<PlayerMotor>();
            if (Animator == null) Animator = GetComponentInChildren<Animator>();
            if (Animator == null || Animator.runtimeAnimatorController == null) return;
            foreach (var p in Animator.parameters)
            {
                if (p.nameHash == SpeedId) _hasSpeed = true;
                else if (p.nameHash == GroundedId) _hasGrounded = true;
                else if (p.nameHash == JumpId) _hasJump = true;
                else if (p.nameHash == CrouchId) _hasCrouch = true;
                else if (p.nameHash == VerticalSpeedId) _hasVertical = true;
            }
        }

        private void LateUpdate()
        {
            if (Animator == null) return;
            if (_hasSpeed) Animator.SetFloat(SpeedId, _motor.NormalizedSpeed, SpeedDamping, Time.deltaTime);
            if (_hasGrounded) Animator.SetBool(GroundedId, _motor.IsGrounded);
            if (_hasCrouch) Animator.SetBool(CrouchId, _motor.IsCrouching);
            if (_hasVertical) Animator.SetFloat(VerticalSpeedId, _motor.Velocity.y);
            if (_hasJump && _motor.JumpedThisFrame) Animator.SetTrigger(JumpId);
        }
    }
}
