using UnityEngine;

namespace HeroGame.Runtime.Player
{
    /// <summary>
    /// Orbit camera with shoulder offset, zoom, collision pull-in and smoothing. Written without
    /// Cinemachine so the gameplay camera has no package dependency; cinematic cameras use Timeline.
    /// </summary>
    public sealed class ThirdPersonCamera : MonoBehaviour
    {
        public Transform Target;
        public Vector3 PivotOffset = new Vector3(0f, 1.6f, 0f);
        public float ShoulderOffset = 0.45f;
        public float Distance = 3.6f;
        public float MinDistance = 1.2f;
        public float MaxDistance = 7f;
        public float Sensitivity = 0.12f;
        public float GamepadSensitivity = 160f;
        public float MinPitch = -35f;
        public float MaxPitch = 70f;
        public float CollisionRadius = 0.25f;
        public LayerMask CollisionMask = ~0;
        public float FollowSharpness = 18f;

        public IPlayerInputSource Input { get; set; }

        private float _yaw;
        private float _pitch = 12f;
        private float _currentDistance;

        /// <summary>Switch what the camera follows (on foot ↔ vehicle) with suitable framing.</summary>
        public void SetTarget(Transform target, Vector3 pivotOffset, float distance)
        {
            Target = target;
            PivotOffset = pivotOffset;
            Distance = Mathf.Clamp(distance, MinDistance, Mathf.Max(MaxDistance, distance));
            MaxDistance = Mathf.Max(MaxDistance, distance + 4f);
        }

        private void Start()
        {
            if (Input == null) Input = PlayerInputRegistry.Create();
            _currentDistance = Distance;
            if (Target != null) _yaw = Target.eulerAngles.y;
        }

        private void LateUpdate()
        {
            if (Target == null) return;
            var input = Input != null && Input.GameplayEnabled ? Input.Read() : default;
            _yaw += input.Look.x * Sensitivity;
            _pitch = Mathf.Clamp(_pitch - input.Look.y * Sensitivity, MinPitch, MaxPitch);
            Distance = Mathf.Clamp(Distance - input.Zoom * 0.01f, MinDistance, MaxDistance);

            var rotation = Quaternion.Euler(_pitch, _yaw, 0f);
            var pivot = Target.position + PivotOffset;
            var shoulder = rotation * new Vector3(ShoulderOffset, 0f, 0f);
            var desired = pivot + shoulder - rotation * Vector3.forward * Distance;

            // Pull in when geometry is between the pivot and the camera; ease back out.
            var dir = desired - pivot;
            var wanted = dir.magnitude;
            var allowed = wanted;
            if (Physics.SphereCast(pivot, CollisionRadius, dir.normalized, out var hit, wanted, CollisionMask, QueryTriggerInteraction.Ignore)
                && !hit.transform.IsChildOf(Target))
                allowed = Mathf.Max(0.3f, hit.distance - 0.05f);
            _currentDistance = allowed < _currentDistance ? allowed : Mathf.Lerp(_currentDistance, allowed, 1f - Mathf.Exp(-6f * Time.deltaTime));

            var position = pivot + dir.normalized * _currentDistance;
            transform.position = Vector3.Lerp(transform.position, position, 1f - Mathf.Exp(-FollowSharpness * Time.deltaTime));
            transform.rotation = rotation;
        }
    }
}
