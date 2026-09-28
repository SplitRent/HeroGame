using UnityEngine;

namespace HeroGame.Runtime.UI
{
    using HeroGame.Runtime.Player;

    /// <summary>
    /// Reference-counted "a menu is open" state: while any panel holds focus the cursor is free and the
    /// player/camera ignore input. Panels call <see cref="Acquire"/> / <see cref="Release"/> in pairs.
    /// </summary>
    public static class UiFocus
    {
        private static int _holders;
        private static IPlayerInputSource _input;

        public static bool Active => _holders > 0;

        private static int _escapeFrame = -1;

        /// <summary>A panel used Esc this frame (closed itself, skipped a shot), so the pause menu must not also open.</summary>
        public static void ConsumeEscape() => _escapeFrame = Time.frameCount;

        public static bool EscapeConsumedThisFrame => _escapeFrame == Time.frameCount;

        public static void Acquire()
        {
            if (_holders++ > 0) return;
            Audio.AudioDirector.Ui(Core.Audio.Synth.UiSound.Click);
            if (_input == null) _input = PlayerInputRegistry.Create();
            _input.GameplayEnabled = false;
            Cursor.lockState = CursorLockMode.None;
            Cursor.visible = true;
        }

        /// <summary>
        /// Front end: no panel holds focus any more (the scene that held it is gone) and the mouse belongs to the menu.
        /// Called when the main menu opens, so a cursor locked by gameplay (or by quitting from the pause screen)
        /// never carries over.
        /// </summary>
        public static void Reset()
        {
            _holders = 0;
            // The shared input source outlives scenes: leave it ready for the next time gameplay starts.
            if (PlayerInputRegistry.Factory != null) PlayerInputRegistry.Factory().GameplayEnabled = true;
            _input = null;
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
