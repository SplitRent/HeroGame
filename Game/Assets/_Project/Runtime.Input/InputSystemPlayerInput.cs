using HeroGame.Runtime.Player;
using UnityEngine;
using UnityEngine.InputSystem;

namespace HeroGame.Runtime.Input
{
    /// <summary>
    /// Input System adapter (keyboard/mouse + gamepad). Actions are defined in code so bindings are
    /// reviewable in diffs; a rebinding UI can later load overrides from JSON.
    /// </summary>
    public sealed class InputSystemPlayerInput : IPlayerInputSource
    {
        private readonly InputAction _move;
        private readonly InputAction _look;
        private readonly InputAction _zoom;
        private readonly InputAction _sprint;
        private readonly InputAction _walk;
        private readonly InputAction _jump;
        private readonly InputAction _interact;
        private readonly InputAction _crouch;
        private readonly InputAction _console;
        private readonly InputAction _inspector;
        private readonly InputAction _pause;
        private readonly InputAction _enterExit;
        private readonly InputAction _horn;
        private readonly InputAction _phone;
        private readonly InputAction _build;
        private readonly InputAction _pointer;
        private readonly InputAction _primary;
        private readonly InputAction _secondary;
        private readonly InputAction _rotate;
        private readonly InputAction _nextTool;
        private readonly InputAction _confirm;
        private readonly InputAction _power;
        private readonly InputAction[] _powerSlots = new InputAction[4];

        public bool GameplayEnabled { get; set; } = true;

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        private static void Register()
        {
            InputSystemPlayerInput shared = null;
            PlayerInputRegistry.Factory = () => shared ?? (shared = new InputSystemPlayerInput());
        }

        public InputSystemPlayerInput()
        {
            _move = new InputAction("Move", InputActionType.Value);
            _move.AddCompositeBinding("2DVector")
                .With("Up", "<Keyboard>/w").With("Down", "<Keyboard>/s")
                .With("Left", "<Keyboard>/a").With("Right", "<Keyboard>/d");
            _move.AddBinding("<Gamepad>/leftStick");

            _look = new InputAction("Look", InputActionType.Value);
            _look.AddBinding("<Mouse>/delta");
            _look.AddBinding("<Gamepad>/rightStick").WithProcessor("scaleVector2(x=12,y=12)");

            _zoom = new InputAction("Zoom", InputActionType.Value, "<Mouse>/scroll/y");
            _sprint = new InputAction("Sprint", InputActionType.Button, "<Keyboard>/leftShift");
            _sprint.AddBinding("<Gamepad>/leftStickPress");
            _walk = new InputAction("Walk", InputActionType.Button, "<Keyboard>/leftAlt");
            _jump = new InputAction("Jump", InputActionType.Button, "<Keyboard>/space");
            _jump.AddBinding("<Gamepad>/buttonSouth");
            _interact = new InputAction("Interact", InputActionType.Button, "<Keyboard>/e");
            _interact.AddBinding("<Gamepad>/buttonWest");
            _crouch = new InputAction("Crouch", InputActionType.Button, "<Keyboard>/leftCtrl");
            _crouch.AddBinding("<Gamepad>/buttonEast");
            _console = new InputAction("Console", InputActionType.Button, "<Keyboard>/backquote");
            _inspector = new InputAction("Inspector", InputActionType.Button, "<Keyboard>/f3");
            _pause = new InputAction("Pause", InputActionType.Button, "<Keyboard>/escape");
            _pause.AddBinding("<Gamepad>/start");

            _enterExit = new InputAction("EnterExit", InputActionType.Button, "<Keyboard>/f");
            _enterExit.AddBinding("<Gamepad>/buttonNorth");
            _horn = new InputAction("Horn", InputActionType.Button, "<Keyboard>/h");
            _horn.AddBinding("<Gamepad>/leftStickPress");
            _phone = new InputAction("Phone", InputActionType.Button, "<Keyboard>/upArrow");
            _phone.AddBinding("<Gamepad>/dpad/up");

            _build = new InputAction("BuildMode", InputActionType.Button, "<Keyboard>/b");
            _build.AddBinding("<Gamepad>/select");
            _pointer = new InputAction("Pointer", InputActionType.Value, "<Pointer>/position");
            _primary = new InputAction("Primary", InputActionType.Button, "<Mouse>/leftButton");
            _primary.AddBinding("<Gamepad>/rightTrigger");
            _secondary = new InputAction("Secondary", InputActionType.Button, "<Mouse>/rightButton");
            _secondary.AddBinding("<Gamepad>/leftTrigger");
            _rotate = new InputAction("Rotate", InputActionType.Button, "<Keyboard>/r");
            _rotate.AddBinding("<Gamepad>/leftShoulder");
            _nextTool = new InputAction("NextTool", InputActionType.Button, "<Keyboard>/tab");
            _nextTool.AddBinding("<Gamepad>/rightShoulder");
            _confirm = new InputAction("Confirm", InputActionType.Button, "<Keyboard>/enter");

            _power = new InputAction("Power", InputActionType.Button, "<Keyboard>/q");
            _power.AddBinding("<Gamepad>/rightTrigger");
            for (var i = 0; i < 4; i++)
            {
                _powerSlots[i] = new InputAction("PowerSlot" + (i + 1), InputActionType.Button, "<Keyboard>/" + (i + 1));
                _powerSlots[i].Enable();
            }
            _powerSlots[0].AddBinding("<Gamepad>/dpad/left");
            _powerSlots[1].AddBinding("<Gamepad>/dpad/right");

            foreach (var a in new[] { _move, _look, _zoom, _sprint, _walk, _jump, _interact, _crouch, _console, _inspector, _pause, _enterExit, _horn, _phone,
                         _build, _pointer, _primary, _secondary, _rotate, _nextTool, _confirm, _power }) a.Enable();
        }

        public PlayerInputState Read()
        {
            return new PlayerInputState
            {
                Move = _move.ReadValue<Vector2>(),
                Look = _look.ReadValue<Vector2>(),
                Zoom = _zoom.ReadValue<float>(),
                Sprint = _sprint.IsPressed(),
                Walk = _walk.IsPressed(),
                JumpPressed = _jump.WasPressedThisFrame(),
                JumpHeld = _jump.IsPressed(),
                EnterExitPressed = _enterExit.WasPressedThisFrame(),
                Horn = _horn.IsPressed(),
                PhonePressed = _phone.WasPressedThisFrame(),
                InteractPressed = _interact.WasPressedThisFrame(),
                CrouchHeld = _crouch.IsPressed(),
                ToggleConsolePressed = _console.WasPressedThisFrame(),
                ToggleInspectorPressed = _inspector.WasPressedThisFrame(),
                PausePressed = _pause.WasPressedThisFrame(),
                BuildModePressed = _build.WasPressedThisFrame(),
                Pointer = _pointer.ReadValue<Vector2>(),
                PrimaryPressed = _primary.WasPressedThisFrame(),
                SecondaryPressed = _secondary.WasPressedThisFrame(),
                RotatePressed = _rotate.WasPressedThisFrame(),
                NextToolPressed = _nextTool.WasPressedThisFrame(),
                ConfirmPressed = _confirm.WasPressedThisFrame(),
                PowerHeld = _power.IsPressed(),
                PowerSlotPressed = SlotPressed(),
            };
        }

        private int SlotPressed()
        {
            for (var i = 0; i < _powerSlots.Length; i++) if (_powerSlots[i].WasPressedThisFrame()) return i + 1;
            return 0;
        }
    }
}
