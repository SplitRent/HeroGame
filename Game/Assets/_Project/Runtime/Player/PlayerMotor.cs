using UnityEngine;

namespace HeroGame.Runtime.Player
{
    /// <summary>
    /// Third-person locomotion on a CharacterController (GDD §118): walk, jog, sprint, crouch, jump,
    /// air control, slope and step handling, and camera-relative movement. Tuned for responsiveness
    /// first; animation reads the resulting velocity rather than driving it.
    /// </summary>
    [RequireComponent(typeof(CharacterController))]
    public sealed class PlayerMotor : MonoBehaviour
    {
        [Header("Speeds (m/s)")]
        public float WalkSpeed = 1.8f;
        public float JogSpeed = 4.2f;
        public float SprintSpeed = 7.0f;
        public float CrouchSpeed = 1.5f;

        [Header("Responsiveness")]
        public float Acceleration = 22f;
        public float Deceleration = 28f;
        public float AirControl = 0.35f;
        public float TurnSpeedDegrees = 720f;

        [Header("Jumping & gravity")]
        public float JumpHeight = 1.1f;
        public float Gravity = -22f;
        public float CoyoteTime = 0.12f;
        public float JumpBuffer = 0.12f;
        public float TerminalVelocity = -55f;

        [Header("Stamina")]
        public float MaxStamina = 8f;
        public float StaminaRecovery = 1.5f;

        public Transform CameraTransform;

        public Vector3 Velocity { get; private set; }
        public bool IsGrounded { get; private set; }
        public bool IsSprinting { get; private set; }
        public bool IsCrouching { get; private set; }
        public float Stamina { get; private set; }
        /// <summary>Speed as a fraction of sprint speed (animation blend input).</summary>
        public float NormalizedSpeed => new Vector2(Velocity.x, Velocity.z).magnitude / SprintSpeed;

        public IPlayerInputSource Input { get; set; }

        private CharacterController _controller;
        private float _verticalVelocity;
        private float _lastGroundedTime = -10f;
        private float _lastJumpPressedTime = -10f;
        private float _standingHeight;
        private bool _jumpedThisFrame;

        private void Awake()
        {
            _controller = GetComponent<CharacterController>();
            _standingHeight = _controller.height;
            Stamina = MaxStamina;
        }

        private void Start()
        {
            if (Input == null) Input = PlayerInputRegistry.Create();
            if (CameraTransform == null && Camera.main != null) CameraTransform = Camera.main.transform;
        }

        private void Update()
        {
            var input = Input != null && Input.GameplayEnabled ? Input.Read() : default;
            Step(input, Time.deltaTime);
        }

        /// <summary>Deterministic step used by Update, tests and (later) server reconciliation.</summary>
        public void Step(PlayerInputState input, float dt)
        {
            if (dt <= 0f) return;
            _jumpedThisFrame = false;
            IsGrounded = _controller.isGrounded;
            if (IsGrounded) _lastGroundedTime = Time.time;
            if (input.JumpPressed) _lastJumpPressedTime = Time.time;

            // Camera-relative desired direction on the ground plane.
            var move = Vector2.ClampMagnitude(input.Move, 1f);
            var forward = CameraTransform != null ? Vector3.ProjectOnPlane(CameraTransform.forward, Vector3.up).normalized : Vector3.forward;
            if (forward.sqrMagnitude < 0.01f) forward = transform.forward;
            var right = Vector3.Cross(Vector3.up, forward);
            var desiredDir = forward * move.y + right * move.x;

            IsCrouching = input.CrouchHeld && IsGrounded;
            IsSprinting = input.Sprint && !IsCrouching && move.sqrMagnitude > 0.25f && Stamina > 0.05f;
            var targetSpeed = IsCrouching ? CrouchSpeed : input.Walk ? WalkSpeed : IsSprinting ? SprintSpeed : JogSpeed;

            Stamina = IsSprinting ? Mathf.Max(0f, Stamina - dt) : Mathf.Min(MaxStamina, Stamina + StaminaRecovery * dt);

            var horizontal = new Vector3(Velocity.x, 0f, Velocity.z);
            var target = desiredDir * targetSpeed * move.magnitude;
            var rate = (target.sqrMagnitude > horizontal.sqrMagnitude ? Acceleration : Deceleration) * (IsGrounded ? 1f : AirControl);
            horizontal = Vector3.MoveTowards(horizontal, target, rate * dt);

            // Jump with coyote time and input buffering.
            var canJump = Time.time - _lastGroundedTime <= CoyoteTime && !IsCrouching;
            if (canJump && Time.time - _lastJumpPressedTime <= JumpBuffer)
            {
                _verticalVelocity = Mathf.Sqrt(2f * JumpHeight * -Gravity);
                _lastJumpPressedTime = -10f;
                _lastGroundedTime = -10f;
                _jumpedThisFrame = true;
            }
            else if (IsGrounded && _verticalVelocity < 0f)
            {
                _verticalVelocity = -2f; // keep snapped to slopes and steps
            }
            _verticalVelocity = Mathf.Max(TerminalVelocity, _verticalVelocity + Gravity * dt);

            Velocity = new Vector3(horizontal.x, _verticalVelocity, horizontal.z);
            _controller.Move(Velocity * dt);

            if (horizontal.sqrMagnitude > 0.04f)
            {
                var look = Quaternion.LookRotation(new Vector3(horizontal.x, 0f, horizontal.z));
                transform.rotation = Quaternion.RotateTowards(transform.rotation, look, TurnSpeedDegrees * dt);
            }

            var height = IsCrouching ? _standingHeight * 0.6f : _standingHeight;
            _controller.height = Mathf.MoveTowards(_controller.height, height, dt * 4f);
            _controller.center = new Vector3(0f, _controller.height * 0.5f, 0f);
        }

        public bool JumpedThisFrame => _jumpedThisFrame;

        /// <summary>Teleport safely (respawn, debug, cutscenes).</summary>
        public void Warp(Vector3 position, Quaternion rotation)
        {
            _controller.enabled = false;
            transform.SetPositionAndRotation(position, rotation);
            _controller.enabled = true;
            Velocity = Vector3.zero;
            _verticalVelocity = 0f;
        }
    }
}
