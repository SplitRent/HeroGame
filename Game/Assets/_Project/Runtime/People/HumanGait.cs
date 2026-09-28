using System.Collections.Generic;
using UnityEngine;

namespace HeroGame.Runtime.People
{
    using HeroGame.Core.Characters;

    /// <summary>
    /// Procedural walking for the human body until the authored animation set exists (animations.json lists it as
    /// planned): lowers the arms from the model's A-pose, then swings legs and arms, bends knees, bobs the hips and
    /// sways the torso at a cadence set by speed and stride. Each walk style (animations.json) shapes it: stride, arm
    /// swing, hip sway, bounce, lean, shoulder roll, head drop. Speed is measured from how the object actually moves.
    /// </summary>
    public sealed class HumanGait : MonoBehaviour
    {
        public WalkStyle Style;

        private Transform _hips, _spine, _chest, _neck, _head;
        private Transform _lUpLeg, _lLeg, _lFoot, _rUpLeg, _rLeg, _rFoot, _lArm, _lForearm, _rArm, _rForearm;
        private readonly Dictionary<Transform, Quaternion> _rest = new Dictionary<Transform, Quaternion>();
        private Vector3 _hipsRest;
        private Vector3 _lastPosition;
        private float _phase;
        private float _speed;
        private bool _ready;

        private void Start()
        {
            var bones = new Dictionary<string, Transform>();
            foreach (var t in GetComponentsInChildren<Transform>(true)) bones[t.name] = t;
            Transform B(string n) => bones.TryGetValue(n, out var t) ? t : null;
            _hips = B("Hips");
            _spine = B("Spine");
            _chest = B("UpperChest") ?? B("Chest");
            _neck = B("Neck");
            _head = B("Head");
            _lUpLeg = B("LeftUpperLeg");
            _lLeg = B("LeftLowerLeg");
            _lFoot = B("LeftFoot");
            _rUpLeg = B("RightUpperLeg");
            _rLeg = B("RightLowerLeg");
            _rFoot = B("RightFoot");
            _lArm = B("LeftUpperArm");
            _lForearm = B("LeftLowerArm");
            _rArm = B("RightUpperArm");
            _rForearm = B("RightLowerArm");
            if (_hips == null || _lUpLeg == null || _rUpLeg == null) return;
            // Arms down at the sides (the model is exported in an A-pose).
            LowerArm(_lArm, _lForearm, -1f);
            LowerArm(_rArm, _rForearm, 1f);
            foreach (var t in new[] { _hips, _spine, _chest, _neck, _head, _lUpLeg, _lLeg, _lFoot, _rUpLeg, _rLeg, _rFoot, _lArm, _lForearm, _rArm, _rForearm })
                if (t != null) _rest[t] = Quaternion.Inverse(transform.rotation) * t.rotation;
            _hipsRest = _hips.localPosition;
            _lastPosition = transform.position;
            _ready = true;
        }

        private void LowerArm(Transform upper, Transform lower, float side)
        {
            if (upper == null || lower == null) return;
            var dir = (lower.position - upper.position).normalized;
            var target = (transform.TransformDirection(new Vector3(0.12f * -side, -1f, 0.04f))).normalized;
            upper.rotation = Quaternion.FromToRotation(dir, target) * upper.rotation;
        }

        private void LateUpdate()
        {
            if (!_ready) return;
            var dt = Mathf.Max(Time.deltaTime, 1e-4f);
            var moved = transform.position - _lastPosition;
            moved.y = 0f;
            _lastPosition = transform.position;
            _speed = Mathf.Lerp(_speed, moved.magnitude / dt, 1f - Mathf.Exp(-8f * dt));
            var s = Style;
            var stride = s != null ? s.Stride : 1f;
            var walking = Mathf.Clamp01(_speed / 0.6f);
            var run = Mathf.Clamp01((_speed - 2.2f) / 2.5f);
            var stepLength = 0.75f * stride * transform.lossyScale.y * (1f + run * 0.6f);
            _phase += _speed / Mathf.Max(0.2f, stepLength) * Mathf.PI * dt;
            var sin = Mathf.Sin(_phase);
            var cos = Mathf.Cos(_phase);

            var legSwing = (26f + 14f * run) * walking * stride;
            var armSwing = (12f + 30f * (s != null ? s.ArmSwing : 0.5f) + 20f * run) * walking;
            var bounce = (s != null ? s.Bounce : 0.3f) * 0.025f * walking;
            var sway = (s != null ? s.HipSway : 0.3f) * 7f * walking;
            var roll = (s != null ? s.ShoulderRoll : 0.2f) * 10f * walking;
            var lean = (s != null ? s.Lean : 2f) * walking + run * 10f;
            var headDown = s != null ? s.HeadDown : 0f;

            var right = Vector3.right;
            var up = Vector3.up;
            var fwd = Vector3.forward;
            Pose(_hips, Quaternion.AngleAxis(sin * sway, fwd) * Quaternion.AngleAxis(sin * sway * 0.6f, up));
            _hips.localPosition = _hipsRest + Vector3.up * (Mathf.Abs(cos) * bounce / Mathf.Max(0.01f, _hips.lossyScale.y));
            Pose(_spine, Quaternion.AngleAxis(lean * 0.5f, right) * Quaternion.AngleAxis(-sin * sway * 0.5f, fwd));
            Pose(_chest, Quaternion.AngleAxis(lean, right) * Quaternion.AngleAxis(-sin * roll, up));
            Pose(_neck, Quaternion.AngleAxis(headDown * 0.4f + lean * 0.5f, right));
            Pose(_head, Quaternion.AngleAxis(headDown + lean * 0.3f, right) * Quaternion.AngleAxis(sin * roll * 0.3f, up));
            // Legs swing opposite each other; the knee bends as the leg passes under the body.
            Pose(_lUpLeg, Quaternion.AngleAxis(-sin * legSwing, right));
            Pose(_rUpLeg, Quaternion.AngleAxis(sin * legSwing, right));
            Pose(_lLeg, Quaternion.AngleAxis(-sin * legSwing + Mathf.Max(0f, cos) * (35f + 40f * run) * walking, right));
            Pose(_rLeg, Quaternion.AngleAxis(sin * legSwing + Mathf.Max(0f, -cos) * (35f + 40f * run) * walking, right));
            Pose(_lFoot, Quaternion.AngleAxis(-sin * legSwing * 0.3f, right));
            Pose(_rFoot, Quaternion.AngleAxis(sin * legSwing * 0.3f, right));
            // Arms swing against the legs, forearms bend a little more when running.
            Pose(_lArm, Quaternion.AngleAxis(sin * armSwing, right));
            Pose(_rArm, Quaternion.AngleAxis(-sin * armSwing, right));
            Pose(_lForearm, Quaternion.AngleAxis(sin * armSwing - (10f + 60f * run) * walking, right));
            Pose(_rForearm, Quaternion.AngleAxis(-sin * armSwing - (10f + 60f * run) * walking, right));
        }

        /// <summary>Rest pose rotated by <paramref name="delta"/> about the character's own axes.</summary>
        private void Pose(Transform bone, Quaternion delta)
        {
            if (bone == null || !_rest.TryGetValue(bone, out var rest)) return;
            bone.rotation = transform.rotation * (delta * rest);
        }
    }
}
