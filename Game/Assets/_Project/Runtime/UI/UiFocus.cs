using HeroGame.Runtime.Player;
using UnityEngine;

namespace HeroGame.Runtime.UI
{
    /// <summary>
    /// Reference-counted "a menu is open" state: while any panel holds focus the cursor is free and the
    /// player/camera ignore input. Panels call <see cref="Acquire"/> / <see cref="Release"/> in pairs.
    /// </summary>
    public static class UiFocus
    {
        private static int _holders;
        private static IPlayerInputSource _input;

        public static bool Active => _holders > 0;

        public static void Acquire()
        {
            if (_holders++ > 0) return;
            if (_input == null) _input = PlayerInputRegistry.Create();
            _input.GameplayEnabled = false;
            Cursor.lockState = CursorLockMode.None;
            Cursor.visible = true;
        }

        public static void Release()
        {
            if (_holders == 0 || --_holders > 0) return;
            if (_input == null) _input = PlayerInputRegistry.Create();
            _input.GameplayEnabled = true;
            Cursor.lockState = CursorLockMode.Locked;
            Cursor.visible = false;
        }
    }
}
