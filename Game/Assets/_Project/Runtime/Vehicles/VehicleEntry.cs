using HeroGame.Runtime.Bootstrap;
using HeroGame.Runtime.Interaction;
using HeroGame.Runtime.Player;
using HeroGame.Runtime.UI;
using UnityEngine;

namespace HeroGame.Runtime.Vehicles
{
    /// <summary>
    /// Enter/exit a vehicle (GDD §93). Owners use their keys; anyone else is attempting theft, which is
    /// reported to the crime layer through <see cref="TheftAttempted"/> (Phase 7 wires witnesses & police).
    /// While driving, the player body is parked inside the car and the camera follows the vehicle.
    /// </summary>
    [RequireComponent(typeof(VehicleController))]
    public sealed class VehicleEntry : Interactable
    {
        public Transform DriverExit;
        public Vector3 CameraPivot = new Vector3(0f, 1.4f, 0f);
        public float CameraDistance = 6.5f;

        public static event System.Action<VehicleController, GameObject> TheftAttempted;

        private VehicleController _vehicle;
        private GameObject _driver;
        private IPlayerInputSource _input;

        public bool Occupied => _driver != null;

        private void Awake()
        {
            _vehicle = GetComponent<VehicleController>();
            MaxDistance = 3.5f;
        }

        public override InteractionCategory Categories => InteractionCategory.Enterable | InteractionCategory.Stealable;

        public override string GetPrompt(InteractionContext context)
        {
            var name = _vehicle.Model != null ? _vehicle.Model.DisplayName : "vehicle";
            return IsOwner() ? "Get in your " + name : "Steal the " + name;
        }

        public override bool CanInteract(InteractionContext context) => !Occupied && base.CanInteract(context);

        public override void Interact(InteractionContext context)
        {
            if (Occupied) return;
            if (!IsOwner()) TheftAttempted?.Invoke(_vehicle, context.Actor);
            Enter(context.Actor);
        }

        private void Update()
        {
            if (_driver == null || _input == null) return;
            if (_input.Read().EnterExitPressed) Exit();
        }

        private void Enter(GameObject player)
        {
            _driver = player;
            _input = PlayerInputRegistry.Create();
            SetPlayerActive(player, false);
            player.transform.SetParent(transform, true);
            player.transform.localPosition = new Vector3(-0.4f, 0.4f, 0.2f);
            _vehicle.Driver = _input;
            var cam = Camera.main != null ? Camera.main.GetComponent<ThirdPersonCamera>() : null;
            if (cam != null) cam.SetTarget(transform, CameraPivot, CameraDistance);
            if (_vehicle.Record != null && ServiceRegistry.TryGet<GameSession>(out var session))
                SubtitleFeed.Say("", _vehicle.Record.Plate + " · fuel " + _vehicle.Record.FuelLitres.ToString("0") + " L", 2.5f);
        }

        private void Exit()
        {
            var player = _driver;
            _driver = null;
            _vehicle.Driver = null;
            player.transform.SetParent(null, true);
            var exit = DriverExit != null ? DriverExit.position : transform.position - transform.right * 2f;
            var motor = player.GetComponent<PlayerMotor>();
            SetPlayerActive(player, true);
            if (motor != null) motor.Warp(exit, Quaternion.LookRotation(transform.forward));
            var cam = Camera.main != null ? Camera.main.GetComponent<ThirdPersonCamera>() : null;
            if (cam != null) cam.SetTarget(player.transform, new Vector3(0f, 1.6f, 0f), 3.6f);
        }

        private static void SetPlayerActive(GameObject player, bool active)
        {
            var cc = player.GetComponent<CharacterController>();
            if (cc != null) cc.enabled = active;
            var motor = player.GetComponent<PlayerMotor>();
            if (motor != null) motor.enabled = active;
            foreach (var r in player.GetComponentsInChildren<Renderer>()) r.enabled = active;
        }

        private bool IsOwner()
        {
            if (_vehicle.Record == null || !ServiceRegistry.TryGet<GameSession>(out var session) || session.LocalCharacter == null) return false;
            return session.World.Ownership.OwnerOf(_vehicle.Record.Id) == session.LocalCharacter.CharacterId;
        }
    }
}
