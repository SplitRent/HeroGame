#if UNITY_EDITOR || DEVELOPMENT_BUILD
using System.Collections.Generic;
using HeroGame.Runtime.Player;
using UnityEngine;

namespace HeroGame.Runtime.DevTools
{
    /// <summary>
    /// In-game developer console (toggle with `). Compiled only into the editor and development
    /// builds, so it can never ship as player functionality (GDD §150).
    /// </summary>
    public sealed class DevConsole : MonoBehaviour
    {
        public Transform Player;
        public int MaxLines = 200;

        private readonly DevCommands _commands = new DevCommands();
        private readonly List<string> _lines = new List<string>();
        private readonly List<string> _history = new List<string>();
        private IPlayerInputSource _input;
        private string _entry = "";
        private bool _open;
        private Vector2 _scroll;
        private int _historyIndex;

        private void Start()
        {
            _input = PlayerInputRegistry.Create();
            _commands.PlayerPosition = () => Player != null ? Player.position : Vector3.zero;
            Print("Developer console. Type 'help'.");
        }

        private void Update()
        {
            if (_input != null && _input.Read().ToggleConsolePressed)
            {
                _open = !_open;
                _input.GameplayEnabled = !_open;
                Cursor.lockState = _open ? CursorLockMode.None : CursorLockMode.Locked;
                Cursor.visible = _open;
            }
        }

        private void OnGUI()
        {
            if (!_open) return;
            var height = Screen.height * 0.45f;
            GUI.Box(new Rect(0, 0, Screen.width, height), GUIContent.none);
            GUILayout.BeginArea(new Rect(8, 8, Screen.width - 16, height - 16));
            _scroll = GUILayout.BeginScrollView(_scroll);
            foreach (var line in _lines) GUILayout.Label(line);
            GUILayout.EndScrollView();

            var e = Event.current;
            if (e.type == EventType.KeyDown)
            {
                if (e.keyCode == KeyCode.Return || e.keyCode == KeyCode.KeypadEnter)
                {
                    Submit();
                    e.Use();
                }
                else if (e.keyCode == KeyCode.UpArrow && _history.Count > 0)
                {
                    _historyIndex = Mathf.Clamp(_historyIndex - 1, 0, _history.Count - 1);
                    _entry = _history[_historyIndex];
                    e.Use();
                }
                else if (e.keyCode == KeyCode.BackQuote)
                {
                    e.Use();
                }
            }
            GUI.SetNextControlName("dev-console-entry");
            _entry = GUILayout.TextField(_entry);
            GUI.FocusControl("dev-console-entry");
            GUILayout.EndArea();
        }

        private void Submit()
        {
            var line = _entry.Trim();
            _entry = "";
            if (line.Length == 0) return;
            _history.Add(line);
            _historyIndex = _history.Count;
            Print("> " + line);
            Print(_commands.Execute(line));
        }

        private void Print(string text)
        {
            if (string.IsNullOrEmpty(text)) return;
            foreach (var l in text.Split('\n')) _lines.Add(l);
            while (_lines.Count > MaxLines) _lines.RemoveAt(0);
            _scroll.y = float.MaxValue;
        }
    }
}
#endif
