using HeroGame.Core.Vehicles;
using HeroGame.Runtime.Bootstrap;
using HeroGame.Runtime.Player;
using UnityEngine;

namespace HeroGame.Runtime.Vehicles
{
    /// <summary>
    /// Arcade-leaning simulation driving on WheelColliders (GDD §33, TDD §10.3): power-limited torque,
    /// speed-sensitive steering, ABS-free braking with reverse, handbrake, downforce and anti-roll bars.
    /// Stats come from the persistent <see cref="VehicleRecord"/> (model + mods + damage), and wear, fuel and
    /// collision damage are written back through the core <see cref="VehicleService"/>.
    /// </summary>
    [RequireComponent(typeof(Rigidbody))]
    public sealed class VehicleController : MonoBehaviour
    {
        public WheelCollider FrontLeft, FrontRight, RearLeft, RearRight;
        public Transform FrontLeftVisual, FrontRightVisual, RearLeftVisual, RearRightVisual;
        public Vector3 CenterOfMass = new Vector3(0f, -0.35f, 0.1f);
        public float AntiRoll = 6000f;
        public float Downforce = 3f;
        public float SteerSpeedFalloff = 0.012f;

        public IPlayerInputSource Driver { get; set; }
        public VehicleRecord Record { get; private set; }
        public VehicleModel Model { get; private set; }
        public float SpeedKph { get; private set; }

        private Rigidbody _body;
        private float _power = 110f, _brakes = 3000f, _grip = 1f, _topSpeed = 180f, _steer = 32f;
        private bool _frontDrive = true, _rearDrive;
        private float _distanceAccumulator;
        private float _speedAccumulator;
        private int _samples;

        private void Awake()
        {
            if (GetComponent<Audio.VehicleAudio>() == null) gameObject.AddComponent<Audio.VehicleAudio>();
            _body = GetComponent<Rigidbody>();
            _body.centerOfMass = CenterOfMass;
        }

        /// <summary>Applies model, mods and damage from the persistent record.</summary>
        public void Bind(VehicleRecord record, VehicleService service)
        {
            Record = record;
            Model = service.Model(record.ModelId);
            if (Model == null) return;
            service.Performance(record, out var power, out var brakes, out var grip);
            _power = Model.EnginePowerKw * power;
            _brakes = Model.BrakeTorque * brakes;
            _grip = grip;
            _topSpeed = Model.TopSpeedKph;
            _steer = Model.MaxSteerDegrees;
            _body.mass = Model.MassKg;
            _frontDrive = Model.Drivetrain == "FWD" || Model.Drivetrain == "AWD" || Model.Drivetrain == "4WD";
            _rearDrive = Model.Drivetrain != "FWD";
            foreach (var w in Wheels())
            {
                if (w == null) continue;
                var f = w.forwardFriction;
                f.stiffness = 1.6f * _grip;
                w.forwardFriction = f;
                var s = w.sidewaysFriction;
                s.stiffness = 1.8f * _grip;
                w.sidewaysFriction = s;
            }
        }

        private void FixedUpdate()
        {
            SpeedKph = Velocity().magnitude * 3.6f;
            var input = Driver != null && Driver.GameplayEnabled ? Driver.Read() : default;
            var fuelled = Record == null || Record.FuelLitres > 0f;
            var drivable = Record == null || Record.Drivable;
            var forwardSpeed = Vector3.Dot(Velocity(), transform.forward);

            float throttle = 0f, brake = 0f;
            if (input.Move.y > 0.05f) throttle = input.Move.y;
            else if (input.Move.y < -0.05f)
            {
                // Brake while rolling forward; reverse once (nearly) stopped.
                if (forwardSpeed > 1f) brake = -input.Move.y;
                else throttle = input.Move.y * 0.5f;
            }
            if (!fuelled || !drivable) throttle = 0f;

            // Torque from power: T = P / ω, capped at low speed; cut at top speed.
            var wheelOmega = Mathf.Max(8f, Mathf.Abs(forwardSpeed) / Mathf.Max(0.3f, RearLeft != null ? RearLeft.radius : 0.35f));
            var driven = (_frontDrive ? 2 : 0) + (_rearDrive ? 2 : 0);
            var torque = Mathf.Min(_power * 1000f / wheelOmega, _power * 18f) * throttle / Mathf.Max(1, driven);
            if (SpeedKph >= _topSpeed && throttle > 0f) torque = 0f;

            var steerLimit = _steer / (1f + SpeedKph * SteerSpeedFalloff);
            var steer = input.Move.x * steerLimit;
            Apply(FrontLeft, _frontDrive ? torque : 0f, brake * _brakes, steer);
            Apply(FrontRight, _frontDrive ? torque : 0f, brake * _brakes, steer);
            var handbrake = input.JumpHeld ? _brakes * 2f : 0f;
            Apply(RearLeft, _rearDrive ? torque : 0f, brake * _brakes * 0.6f + handbrake, 0f);
            Apply(RearRight, _rearDrive ? torque : 0f, brake * _brakes * 0.6f + handbrake, 0f);

            AntiRollBar(FrontLeft, FrontRight);
            AntiRollBar(RearLeft, RearRight);
            _body.AddForce(-transform.up * Downforce * Velocity().sqrMagnitude);

            _distanceAccumulator += Velocity().magnitude * Time.fixedDeltaTime;
            _speedAccumulator += SpeedKph;
            _samples++;
        }

        private void Update()
        {
            SyncVisual(FrontLeft, FrontLeftVisual);
            SyncVisual(FrontRight, FrontRightVisual);
            SyncVisual(RearLeft, RearLeftVisual);
            SyncVisual(RearRight, RearRightVisual);

            // Report distance to the core once per ~100 m (fuel, tyres, odometer).
            if (Record == null || _distanceAccumulator < 100f || !ServiceRegistry.TryGet<GameSession>(out var session)) return;
            var avg = _samples > 0 ? _speedAccumulator / _samples : 0f;
            session.World.Vehicles.Drive(Record, _distanceAccumulator / 1000f, avg);
            Record.Position = transform.position.ToWorld();
            Record.Heading = transform.eulerAngles.y;
            _distanceAccumulator = 0f;
            _speedAccumulator = 0f;
            _samples = 0;
        }

        private void OnCollisionEnter(Collision collision)
        {
            if (Record == null || !ServiceRegistry.TryGet<GameSession>(out var session)) return;
            var impulse = collision.impulse.magnitude;
            if (impulse < _body.mass * 2f) return; // scrapes don't count
            var local = transform.InverseTransformDirection(collision.impulse.normalized);
            session.World.Vehicles.ApplyCollision(Record, impulse, frontal: Mathf.Abs(local.z) > 0.6f);
        }

        private Vector3 Velocity()
        {
#if UNITY_6000_0_OR_NEWER
            return _body.linearVelocity;
#else
            return _body.velocity;
#endif
        }

        private WheelCollider[] Wheels() => new[] { FrontLeft, FrontRight, RearLeft, RearRight };

        private static void Apply(WheelCollider wheel, float motor, float brake, float steer)
        {
            if (wheel == null) return;
            wheel.motorTorque = motor;
            wheel.brakeTorque = brake;
            wheel.steerAngle = steer;
        }

        private void AntiRollBar(WheelCollider left, WheelCollider right)
        {
            if (left == null || right == null) return;
            float travelL = 1f, travelR = 1f;
            var groundedL = left.GetGroundHit(out var hitL);
            if (groundedL) travelL = (-left.transform.InverseTransformPoint(hitL.point).y - left.radius) / left.suspensionDistance;
            var groundedR = right.GetGroundHit(out var hitR);
            if (groundedR) travelR = (-right.transform.InverseTransformPoint(hitR.point).y - right.radius) / right.suspensionDistance;
            var force = (travelL - travelR) * AntiRoll;
            if (groundedL) _body.AddForceAtPosition(left.transform.up * -force, left.transform.position);
            if (groundedR) _body.AddForceAtPosition(right.transform.up * force, right.transform.position);
        }

        private static void SyncVisual(WheelCollider wheel, Transform visual)
        {
            if (wheel == null || visual == null) return;
            wheel.GetWorldPose(out var pos, out var rot);
            visual.SetPositionAndRotation(pos, rot);
        }
    }
}
