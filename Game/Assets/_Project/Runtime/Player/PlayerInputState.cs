using System;
using UnityEngine;

namespace HeroGame.Runtime.Player
{
    /// <summary>One frame of player intent, independent of device. Also the unit the network layer will send to the server.</summary>
    public struct PlayerInputState
    {
        public Vector2 Move;
        public Vector2 Look;
        public float Zoom;
        public bool Sprint;
        public bool Walk;
        public bool JumpPressed;
        public bool JumpHeld;
        /// <summary>Enter/exit vehicle (F / gamepad Y).</summary>
        public bool EnterExitPressed;
        public bool Horn;
        /// <summary>Open the phone (Up arrow / gamepad D-pad up).</summary>
        public bool PhonePressed;
        public bool InteractPressed;
        public bool CrouchHeld;
        public bool ToggleConsolePressed;
        public bool ToggleInspectorPressed;
        public bool PausePressed;
        /// <summary>Toggle build mode on an owned property (B / gamepad Select).</summary>
        public bool BuildModePressed;
        /// <summary>Pointer position in screen pixels (build/placement tools).</summary>
        public Vector2 Pointer;
        public bool PrimaryPressed;
        public bool SecondaryPressed;
        public bool RotatePressed;
        /// <summary>Cycle the active build tool (Tab / gamepad right shoulder).</summary>
        public bool NextToolPressed;
        /// <summary>Confirm (Enter / gamepad A in build mode).</summary>
        public bool ConfirmPressed;
    }

    /// <summary>Source of player intent. Implemented by the Input System adapter, AI test drivers and replay.</summary>
    public interface IPlayerInputSource
    {
        PlayerInputState Read();
        /// <summary>Blocks gameplay input (menus, console) without disabling UI navigation.</summary>
        bool GameplayEnabled { get; set; }
    }

    /// <summary>
    /// Decouples gameplay from the Input System package: the Runtime.Input assembly registers a
    /// factory at start-up, so Runtime never references device APIs directly.
    /// </summary>
    public static class PlayerInputRegistry
    {
        public static Func<IPlayerInputSource> Factory;

        public static IPlayerInputSource Create()
        {
            if (Factory != null) return Factory();
            Debug.LogWarning("[Input] No input source registered; the player will not respond to devices.");
            return new NullInputSource();
        }

        private sealed class NullInputSource : IPlayerInputSource
        {
            public bool GameplayEnabled { get; set; } = true;
            public PlayerInputState Read() => default;
        }
    }
}
