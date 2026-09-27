using System.Collections.Generic;
using HeroGame.Core.Foundation;
using HeroGame.Core.Simulation;
using HeroGame.Core.Story;
using HeroGame.Runtime.Bootstrap;
using HeroGame.Runtime.Player;
using HeroGame.Runtime.UI;
using UnityEngine;

namespace HeroGame.Runtime.Story
{
    /// <summary>
    /// Story Mode presentation (GDD §55–60): the current objective and a marker toward it, the dialogue box with
    /// choices (number keys or click), letterboxed cutscenes, and interaction points for tagged objectives.
    /// Feeds the player's position to the <see cref="StoryService"/> every frame; the service decides everything.
    /// Placeholder IMGUI and camera moves (ASSET_TRACKER) until Timeline cutscenes and the UI Toolkit HUD.
    /// </summary>
    public sealed class StoryPresenter : MonoBehaviour
    {
        public Transform Player;
        public Camera CutsceneCamera;
        public StoryInteraction InteractionPrefab;

        private StoryService _story;
        private GUIStyle _objective;
        private GUIStyle _line;
        private GUIStyle _choice;
        private readonly Queue<CutsceneShot> _shots = new Queue<CutsceneShot>();
        private CutsceneShot _shot;
        private float _shotTimer;
        private StoryInteraction _marker;
        private Vector3 _cameraHome;
        private Quaternion _cameraHomeRotation;
        private bool _focused;
        private IPlayerInputSource _input;
        private PlayerInputState _frame;

        private void Update()
        {
            if (_story == null)
            {
                if (!ServiceRegistry.TryGet<GameSession>(out var session) || session.Story == null) return;
                Bind(session.Story);
            }
            // Read the device directly (menus disable gameplay input, not the device).
            if (_input == null) _input = PlayerInputRegistry.Create();
            _frame = _input.Read();
            if (Player != null && _shot == null) _story.Update(Player.position.ToWorld());
            UpdateCutscene();
            UpdateInteractionPoint();
            var wantsFocus = _story.InDialogue || _shot != null;
            if (wantsFocus != _focused)
            {
                _focused = wantsFocus;
                if (wantsFocus) UiFocus.Acquire();
                else UiFocus.Release();
            }
            if (_story.InDialogue && _shot == null) DialogueKeys();
        }

        private void Bind(StoryService story)
        {
            _story = story;
            story.CutsceneRequested += c => { foreach (var s in c.Shots) _shots.Enqueue(s); };
            story.MissionStarted += m => SubtitleFeed.Say("", m.Title.ToUpperInvariant() + " — " + m.Summary, 5f);
            story.MissionCompleted += m => SubtitleFeed.Say("", "Mission complete: " + m.Title, 3f);
            story.CheckpointRequested += _ =>
            {
                if (ServiceRegistry.TryGet<GameSession>(out var session)) session.Save();
            };
        }

        private void DialogueKeys()
        {
            var choices = _story.AvailableChoices();
            if (choices.Count == 0)
            {
                if (_frame.JumpPressed || _frame.ConfirmPressed || _frame.InteractPressed || _frame.PrimaryPressed) _story.Continue();
                return;
            }
            // Number keys 1–4 (the power-slot keys) pick a choice; more than four choices use the mouse.
            if (_frame.PowerSlotPressed > 0 && _frame.PowerSlotPressed <= choices.Count) _story.Choose(_frame.PowerSlotPressed - 1);
        }

        private void UpdateCutscene()
        {
            var cam = CutsceneCamera != null ? CutsceneCamera : Camera.main;
            if (_shot == null && _shots.Count > 0)
            {
                if (cam != null)
                {
                    _cameraHome = cam.transform.position;
                    _cameraHomeRotation = cam.transform.rotation;
                    var follow = cam.GetComponent<ThirdPersonCamera>();
                    if (follow != null) follow.enabled = false;
                }
                NextShot(cam);
            }
            if (_shot == null) return;
            _shotTimer -= Time.unscaledDeltaTime;
            if (_shotTimer <= 0f || _frame.PausePressed)
            {
                if (_shots.Count > 0) NextShot(cam);
                else
                {
                    _shot = null;
                    if (cam != null)
                    {
                        cam.transform.SetPositionAndRotation(_cameraHome, _cameraHomeRotation);
                        var follow = cam.GetComponent<ThirdPersonCamera>();
                        if (follow != null) follow.enabled = true;
                    }
                }
            }
        }

        private void NextShot(Camera cam)
        {
            _shot = _shots.Dequeue();
            _shotTimer = Mathf.Max(1f, _shot.Seconds);
            if (cam == null) return;
            if (!TryShotTarget(_shot.Camera, out var target))
            {
                cam.transform.rotation = Quaternion.Euler(-15f, cam.transform.eulerAngles.y, 0f); // sky
                return;
            }
            var eye = target + new Vector3(12f, 9f, -14f);
            cam.transform.SetPositionAndRotation(eye, Quaternion.LookRotation(target + Vector3.up * 1.5f - eye));
        }

        private bool TryShotTarget(string camera, out Vector3 target)
        {
            target = default;
            if (camera == "player" && Player != null)
            {
                target = Player.position;
                return true;
            }
            if (!ServiceRegistry.TryGet<GameSession>(out var session)) return false;
            if (camera.StartsWith("place:"))
            {
                var place = session.World.Geography.FindPlaceByName(camera.Substring(6));
                if (place == null) return false;
                target = place.Position.ToVector3();
                return true;
            }
            if (camera.StartsWith("cast:"))
            {
                var npc = _story.Npc(camera.Substring(5));
                if (npc == null || !session.World.Director.Index.TryGet(npc.Id, session.World.Clock.Now, out _, out var p)) return false;
                target = p.ToVector3();
                return true;
            }
            return false;
        }

        /// <summary>Interact objectives get a physical point in the world (e.g. the sandbags by the Navarros' door).</summary>
        private void UpdateInteractionPoint()
        {
            var o = _story.CurrentObjective;
            var needed = o != null && o.Kind == ObjectiveKind.Interact && _story.TryObjectivePosition(o, out _);
            if (!needed)
            {
                if (_marker != null) Destroy(_marker.gameObject);
                _marker = null;
                return;
            }
            if (_marker != null && _marker.Tag == o.Target) return;
            if (_marker != null) Destroy(_marker.gameObject);
            _story.TryObjectivePosition(o, out var at);
            _marker = InteractionPrefab != null ? Instantiate(InteractionPrefab) : new GameObject("Story interaction").AddComponent<StoryInteraction>();
            _marker.transform.position = at.ToVector3() + Vector3.up;
            _marker.Tag = o.Target;
            _marker.Prompt = o.Text;
        }

        private void OnGUI()
        {
            if (_story == null) return;
            if (_objective == null)
            {
                _objective = new GUIStyle(GUI.skin.label) { fontSize = 18, fontStyle = FontStyle.Bold, alignment = TextAnchor.UpperCenter };
                _line = new GUIStyle(GUI.skin.label) { fontSize = 20, wordWrap = true };
                _choice = new GUIStyle(GUI.skin.button) { fontSize = 17, alignment = TextAnchor.MiddleLeft, wordWrap = true };
            }
            if (_shot != null)
            {
                // Letterbox.
                var bar = Screen.height * 0.12f;
                GUI.color = Color.black;
                GUI.DrawTexture(new Rect(0, 0, Screen.width, bar), Texture2D.whiteTexture);
                GUI.DrawTexture(new Rect(0, Screen.height - bar, Screen.width, bar), Texture2D.whiteTexture);
                GUI.color = Color.white;
                var speaker = _story.NameOf(_shot.Speaker);
                GUI.Label(new Rect(Screen.width * 0.15f, Screen.height - bar + 12, Screen.width * 0.7f, bar - 16),
                    (string.IsNullOrEmpty(speaker) ? "" : speaker + ": ") + _shot.Text, _line);
                return;
            }
            var o = _story.CurrentObjective;
            var mission = _story.ActiveDefinition;
            if (o != null && mission != null)
            {
                var text = mission.Title + ": " + o.Text;
                if (Player != null && _story.TryObjectivePosition(o, out var pos))
                    text += "  (" + Vector3.Distance(Player.position, pos.ToVector3()).ToString("0") + " m)";
                GUI.Label(new Rect(0, 14, Screen.width, 30), text, _objective);
            }
            if (!_story.InDialogue) return;
            var node = _story.CurrentNode;
            if (node == null) return;
            var box = new Rect(Screen.width * 0.15f, Screen.height * 0.62f, Screen.width * 0.7f, Screen.height * 0.34f);
            GUI.Box(box, GUIContent.none);
            GUILayout.BeginArea(new Rect(box.x + 16, box.y + 12, box.width - 32, box.height - 24));
            var who = _story.NameOf(node.Speaker);
            GUILayout.Label((string.IsNullOrEmpty(who) ? "" : who + ": ") + node.Text, _line);
            var choices = _story.AvailableChoices();
            if (choices.Count == 0)
            {
                if (GUILayout.Button("Continue  [Space]", _choice)) _story.Continue();
            }
            else
                for (var i = 0; i < choices.Count; i++)
                    if (GUILayout.Button((i + 1) + ".  " + choices[i].Text, _choice)) _story.Choose(i);
            GUILayout.EndArea();
        }

        private void OnDestroy()
        {
            if (_focused) UiFocus.Release();
        }
    }
}
