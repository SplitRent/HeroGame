using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.UIElements;

namespace HeroGame.Runtime.UI
{
    using HeroGame.Core.Characters;
    using HeroGame.Core.Foundation;
    using HeroGame.Core.Presentation;
    using HeroGame.Runtime.Bootstrap;
    using HeroGame.Runtime.Interaction;
    using HeroGame.Runtime.Player;

    /// <summary>
    /// The in-game interface on UI Toolkit (GDD Phase 24): HUD (clock, weather, cash, health, wanted level, phone badge,
    /// weapon, interaction prompt, subtitles), notification toasts, the pause/settings screen and the phone. Layout in
    /// Resources/UI/GameHud.uxml, style in Resources/UI/Game.uss; screens are built from the tested view-models
    /// (GameSettings, ToastQueue, PlayerViews). When this component is present the IMGUI placeholders stand aside.
    /// </summary>
    [DefaultExecutionOrder(1000)]
    [RequireComponent(typeof(UIDocument))]
    public sealed class GameUi : MonoBehaviour
    {
        public PanelSettings PanelSettings;
        public PlayerInteractor Interactor;
        public string MenuScene = "MainMenu";

        private enum Screen { None, Pause, Phone, Custody, Store }
        private enum SettingsTab { Audio, Controls, Display, Graphics, Accessibility }

        private static GameUi _instance;
        public static bool Active => _instance != null && _instance.isActiveAndEnabled && _instance._ready;

        private UIDocument _document;
        private VisualElement _root, _layer, _toasts, _subtitles, _healthFill;
        private Label _clock, _weather, _cash, _wanted, _badge, _prompt, _feedback, _weapon, _fps;
        private IPlayerInputSource _input;
        private Screen _screen;
        private bool _ready;
        private float _nextRefresh;
        private string _toastSignature = "";
        private float _fpsTime;
        private int _fpsFrames;
        private string _app = "";
        private VisualElement _corner;
        private string _mapQuery = "";
        private bool _custodyHidden;
        private string _custodyStatus = "";
        private Label _custodyCountdown;
        private Core.Crime.CustodyView _custodyView;
        private EntityId _store;
        private string _storeFilter = "Tops";
        private string _storeStatus = "";
        private readonly Dictionary<string, string> _storeColour = new Dictionary<string, string>();

        private void OnEnable()
        {
            _instance = this;
            _document = GetComponent<UIDocument>();
            // A runtime copy, so the interface-scale setting never edits the shared asset.
            var source = _document.panelSettings != null ? _document.panelSettings : PanelSettings;
            _document.panelSettings = source != null ? Instantiate(source) : ScriptableObject.CreateInstance<PanelSettings>();
            if (_document.visualTreeAsset == null) _document.visualTreeAsset = Resources.Load<VisualTreeAsset>("UI/GameHud");
            _root = _document.rootVisualElement;
            if (_root == null || _document.visualTreeAsset == null) return;
            if (_root.childCount == 0) _document.visualTreeAsset.CloneTree(_root);
            foreach (var sheet in new[] { "UI/Controls", "UI/Game" })
            {
                var style = Resources.Load<StyleSheet>(sheet);
                if (style != null && !_root.styleSheets.Contains(style)) _root.styleSheets.Add(style);
                else if (style == null) Debug.LogWarning("[UI] Missing style sheet Resources/" + sheet + ".uss");
            }
            _root.pickingMode = PickingMode.Ignore;

            _layer = _root.Q<VisualElement>("layer");
            _corner = _root.Q<VisualElement>("corner");
            _toasts = _root.Q<VisualElement>("toasts");
            _subtitles = _root.Q<VisualElement>("subtitles");
            _healthFill = _root.Q<VisualElement>("health-fill");
            _clock = _root.Q<Label>("clock");
            _weather = _root.Q<Label>("weather");
            _cash = _root.Q<Label>("cash");
            _wanted = _root.Q<Label>("wanted");
            _badge = _root.Q<Label>("phone-badge");
            _prompt = _root.Q<Label>("prompt");
            _feedback = _root.Q<Label>("feedback");
            _weapon = _root.Q<Label>("weapon");
            _fps = _root.Q<Label>("fps");
            _ready = _layer != null && _clock != null;
            SettingsService.Changed += OnSettingsChanged;
            OnSettingsChanged(SettingsService.Current);
        }

        private void OnDisable()
        {
            SettingsService.Changed -= OnSettingsChanged;
            if (_screen != Screen.None) UiFocus.Release();
            _screen = Screen.None;
            if (_instance == this) _instance = null;
        }

        private void OnSettingsChanged(GameSettings s)
        {
            if (_document != null && _document.panelSettings != null) _document.panelSettings.scale = s.UiScale;
        }

        // ------------------------------------------------------------------ frame

        private void Update()
        {
            if (!_ready) return;
            if (_input == null) _input = PlayerInputRegistry.Create();
            var input = _input.Read();

            if (input.PausePressed && !UiFocus.EscapeConsumedThisFrame)
            {
                if (_screen == Screen.Custody) Switch(Screen.Pause);
                else if (_screen != Screen.None) Close();
                else if (!UiFocus.Active) Open(Screen.Pause);
            }
            else if (input.PhonePressed && !PhonePanel.ClassicOpen)
            {
                if (_screen == Screen.Phone) Close();
                else if (_screen == Screen.Custody) Switch(Screen.Phone);
                else if (_screen == Screen.None && !UiFocus.Active) Open(Screen.Phone);
            }

            // Custody: the case screen stays up while the player is held (it returns after the pause menu or phone).
            if (!InCustody())
            {
                _custodyHidden = false;
                if (_screen == Screen.Custody) Close();
            }
            else if (_screen == Screen.None && !UiFocus.Active && (!_custodyHidden || input.InteractPressed))
            {
                _custodyHidden = false;
                Open(Screen.Custody);
            }

            _fpsFrames++;
            _fpsTime += Time.unscaledDeltaTime;
            if (Time.unscaledTime >= _nextRefresh)
            {
                _nextRefresh = Time.unscaledTime + 0.1f;
                RefreshHud();
            }
            RefreshToasts();
            if (_screen == Screen.Custody) RefreshCustody();
        }

        private void RefreshHud()
        {
            if (!ServiceRegistry.TryGet<GameSession>(out var session)) return;
            var w = session.World;
            var s = SettingsService.Current;
            var now = w.Clock.Now;
            var weather = w.Weather.State.Current;
            _clock.text = now.DayOfWeek.ToString().Substring(0, 3).ToUpperInvariant() + "  " + s.FormatTime(now.Hour, now.Minute);
            _weather.text = weather.Kind + " · " + s.FormatTemperature(weather.TemperatureC) + (w.Weather.State.ActiveSystem != null ? " · storm " + w.Weather.State.ActiveSystem.Name : "");

            var me = session.LocalCharacter;
            var online = Online.NetworkSession.Me;
            if (online != null)
            {
                // Online, the player's numbers come from the server; the local world is presentation only.
                _cash.text = new Money(online.CashCents).ToString();
                _healthFill.style.width = Length.Percent(Mathf.Clamp01(online.Health) * 100f);
                _healthFill.EnableInClassList("g-bar-fill--hurt", online.Health < 0.6f && online.Health >= 0.3f);
                _healthFill.EnableInClassList("g-bar-fill--critical", online.Health < 0.3f);
                _wanted.text = online.WantedLevel > 0 ? new string('★', online.WantedLevel) + new string('☆', Math.Max(0, 5 - online.WantedLevel)) + "  WANTED " + online.WantedLevel : "";
                _badge.text = online.Unread > 0 ? online.Unread + " new message" + (online.Unread == 1 ? "" : "s") : "";
                _badge.style.display = online.Unread > 0 ? DisplayStyle.Flex : DisplayStyle.None;
            }
            else if (me != null)
            {
                _cash.text = w.Ledger.BalanceOf(me.CheckingAccount).ToString();
                _healthFill.style.width = Length.Percent(Mathf.Clamp01(me.Health) * 100f);
                _healthFill.EnableInClassList("g-bar-fill--hurt", me.Health < 0.6f && me.Health >= 0.3f);
                _healthFill.EnableInClassList("g-bar-fill--critical", me.Health < 0.3f);
                var wanted = w.Wanted.Get(me.CharacterId);
                var level = wanted != null ? wanted.Level : 0;
                // Stars plus a number: never colour alone (accessibility).
                _wanted.text = level > 0 ? new string('★', level) + new string('☆', Math.Max(0, 5 - level)) + "  WANTED " + level : "";
                var unread = w.Phone.UnreadCount(me);
                _badge.text = unread > 0 ? unread + " new message" + (unread == 1 ? "" : "s") : "";
                _badge.style.display = unread > 0 ? DisplayStyle.Flex : DisplayStyle.None;
            }

            var prompt = Crime.StreetEncounterPresenter.Active ? Crime.StreetEncounterPresenter.Card : _custodyHidden && InCustody() ? "Your case, bail and options" : Interactor != null ? Interactor.PromptText : "";
            _prompt.text = string.IsNullOrEmpty(prompt) ? "" : Crime.StreetEncounterPresenter.Active ? "▲  " + prompt : "[E]  " + prompt;
            _prompt.style.display = string.IsNullOrEmpty(prompt) || _screen != Screen.None ? DisplayStyle.None : DisplayStyle.Flex;

            var combat = Combat.CombatController.Current;
            if (combat != null && me != null)
            {
                var weapon = w.Combat.Weapon(combat.EquippedWeapon);
                var ammo = weapon != null && weapon.UsesAmmo ? "   " + Core.Simulation.CombatService.Count(me, weapon.AmmoItemId) + " rds" : "";
                _weapon.text = weapon != null ? "[X] " + weapon.DisplayName + ammo : "";
                _feedback.text = combat.Feedback;
            }

            _subtitles.Clear();
            foreach (var line in SubtitleFeed.Current)
            {
                var row = new Label((string.IsNullOrEmpty(line.Speaker) ? "" : line.Speaker + ":  ") + line.Text);
                row.AddToClassList("g-subtitle");
                row.style.fontSize = 19f * s.SubtitleScale;
                row.pickingMode = PickingMode.Ignore;
                _subtitles.Add(row);
            }

            _fps.style.display = s.ShowFps && _screen != Screen.Pause ? DisplayStyle.Flex : DisplayStyle.None;
            if (s.ShowFps && _fpsTime > 0.5f)
            {
                _fps.text = Mathf.RoundToInt(_fpsFrames / _fpsTime) + " fps";
                _fpsFrames = 0;
                _fpsTime = 0f;
            }
        }

        private void RefreshToasts()
        {
            var queue = ToastPresenter.Queue;
            if (queue == null) return;
            var signature = "";
            foreach (var t in queue.Visible) signature += t.Id + ":" + t.Count + ";";
            if (signature == _toastSignature) return;
            _toastSignature = signature;
            _toasts.Clear();
            foreach (var t in queue.Visible)
            {
                var card = new VisualElement();
                card.AddToClassList("g-toast");
                card.AddToClassList("g-toast--" + t.Kind);
                var symbol = new Label(ToastPresenter.Symbol(t.Kind));
                symbol.AddToClassList("g-toast-symbol");
                var body = new VisualElement();
                body.AddToClassList("g-toast-body");
                var title = new Label(t.Title);
                title.AddToClassList("g-toast-title");
                var text = new Label(t.Text);
                text.AddToClassList("g-toast-text");
                body.Add(title);
                body.Add(text);
                card.Add(symbol);
                card.Add(body);
                var id = t.Id;
                card.RegisterCallback<ClickEvent>(_ => queue.Dismiss(id, Time.unscaledTimeAsDouble));
                _toasts.Add(card);
            }
        }

        // ------------------------------------------------------------------ screens

        private void Open(Screen screen)
        {
            _screen = screen;
            UiFocus.Acquire();
            _app = "";
            Rebuild();
        }

        /// <summary>Changes screen without giving up focus (custody → pause or phone and back).</summary>
        private void Switch(Screen screen)
        {
            _screen = screen;
            _app = "";
            Rebuild();
        }

        private void Close()
        {
            if (_screen == Screen.Pause) SettingsService.Commit();
            _screen = Screen.None;
            _layer.Clear();
            _layer.pickingMode = PickingMode.Ignore;
            ShowHud(true);
            UiFocus.Release();
        }

        /// <summary>The pause screen covers the whole view, so the HUD steps out of its way (the phone keeps it).</summary>
        private void ShowHud(bool show)
        {
            var display = show ? DisplayStyle.Flex : DisplayStyle.None;
            if (_corner != null) _corner.style.display = display;
            if (_weapon != null) _weapon.style.display = display;
        }

        private void Rebuild()
        {
            _layer.Clear();
            _layer.pickingMode = _screen == Screen.None ? PickingMode.Ignore : PickingMode.Position;
            ShowHud(_screen != Screen.Pause && _screen != Screen.Custody && _screen != Screen.Store);
            if (_screen == Screen.Pause) BuildPause(SettingsTab.Audio);
            else if (_screen == Screen.Phone) BuildPhone();
            else if (_screen == Screen.Custody) BuildCustody();
            else if (_screen == Screen.Store) BuildStore();
        }

        private static Button Nav(string text, Action onClick, bool active = false)
        {
            var b = new Button(onClick) { text = text };
            b.AddToClassList("g-nav-button");
            if (active) b.AddToClassList("g-nav-button--active");
            return b;
        }

        private static Button ActionButton(string text, Action onClick, bool quiet = false)
        {
            var b = new Button(onClick) { text = text };
            b.AddToClassList("g-action");
            if (quiet) b.AddToClassList("g-action--quiet");
            return b;
        }

        private static Label Text(string text, string cls)
        {
            var l = new Label(text);
            l.AddToClassList(cls);
            return l;
        }

        // ------------------------------------------------------------------ pause and settings

        private void BuildPause(SettingsTab tab)
        {
            _layer.Clear();
            var overlay = new VisualElement();
            overlay.AddToClassList("g-overlay");
            var nav = new VisualElement();
            nav.AddToClassList("g-overlay-nav");
            nav.Add(Text("PAUSED", "g-overlay-title"));
            nav.Add(Nav("RESUME", Close));
            foreach (SettingsTab t in Enum.GetValues(typeof(SettingsTab)))
            {
                var captured = t;
                nav.Add(Nav(t.ToString().ToUpperInvariant(), () => BuildPause(captured), t == tab));
            }
            nav.Add(Nav("SAVE & QUIT TO MENU", QuitToMenu));
            nav.Add(Nav("QUIT TO DESKTOP", () => { SettingsService.Commit(); Application.Quit(); }));
            overlay.Add(nav);

            var content = new ScrollView();
            content.AddToClassList("g-content");
            var s = SettingsService.Current;
            switch (tab)
            {
                case SettingsTab.Audio:
                    content.Add(Text("Audio", "g-section"));
                    content.Add(Volume("Master", () => s.MasterVolume, v => s.MasterVolume = v));
                    content.Add(Volume("Music & radio", () => s.MusicVolume, v => s.MusicVolume = v));
                    content.Add(Volume("Effects", () => s.EffectsVolume, v => s.EffectsVolume = v));
                    content.Add(Volume("Ambience", () => s.AmbienceVolume, v => s.AmbienceVolume = v));
                    content.Add(Volume("Voices", () => s.VoiceVolume, v => s.VoiceVolume = v));
                    content.Add(Volume("Interface", () => s.InterfaceVolume, v => s.InterfaceVolume = v));
                    break;
                case SettingsTab.Controls:
                    content.Add(Text("Controls", "g-section"));
                    content.Add(Range("Mouse sensitivity", GameSettings.MinSensitivity, GameSettings.MaxSensitivity, () => s.MouseSensitivity, v => s.MouseSensitivity = v));
                    content.Add(Range("Gamepad sensitivity", GameSettings.MinSensitivity, GameSettings.MaxSensitivity, () => s.GamepadSensitivity, v => s.GamepadSensitivity = v));
                    content.Add(Check("Invert vertical look", () => s.InvertY, v => s.InvertY = v));
                    content.Add(Text("Keys", "g-section"));
                    foreach (var line in new[]
                             {
                                 "Move  W A S D · Look  mouse · Sprint  Shift · Jump  Space · Crouch  Ctrl",
                                 "Interact  E · Phone  ↑ · Enter/exit vehicle  F · Horn  H",
                                 "Attack  left mouse · Next weapon  X · Powers  1–4, hold Q",
                                 "Build mode  B · Pause  Esc · Console  ` · Inspector  F3",
                             })
                        content.Add(Text(line, "g-note"));
                    break;
                case SettingsTab.Display:
                    content.Add(Text("Display", "g-section"));
                    GraphicsSettingsPage.Display(content, "g-field", () => BuildPause(tab));
                    break;
                case SettingsTab.Graphics:
                    content.Add(Text("Graphics", "g-section"));
                    GraphicsSettingsPage.Graphics(content, "g-field", () => BuildPause(tab));
                    break;
                case SettingsTab.Accessibility:
                {
                    content.Add(Text("Accessibility & comfort", "g-section"));
                    content.Add(Check("Subtitles", () => s.Subtitles, v => s.Subtitles = v));
                    content.Add(Range("Subtitle size", 0.75f, 2.5f, () => s.SubtitleScale, v => s.SubtitleScale = v));
                    content.Add(Range("Camera shake", 0f, 1f, () => s.CameraShake, v => s.CameraShake = v));
                    content.Add(Check("Motion blur", () => s.MotionBlur, v => s.MotionBlur = v));
                    content.Add(Check("Shape-coded markers (colour-vision friendly)", () => s.ShapeCodedMarkers, v => s.ShapeCodedMarkers = v));
                    content.Add(Check("24-hour clock", () => s.Clock24h, v => s.Clock24h = v));
                    content.Add(Check("Metric units", () => s.Units == UnitSystem.Metric, v => s.Units = v ? UnitSystem.Metric : UnitSystem.Imperial));
                    content.Add(Range("Notifications stay for (seconds)", 2f, 30f, () => s.NotificationSeconds, v => s.NotificationSeconds = v));
                    break;
                }
            }
            var row = new VisualElement();
            row.AddToClassList("hg-row");
            row.style.flexDirection = FlexDirection.Row;
            row.style.marginTop = 18;
            row.Add(ActionButton("Reset to defaults", () => { SettingsService.ResetToDefaults(); BuildPause(tab); }, quiet: true));
            row.Add(ActionButton("Resume", Close));
            content.Add(row);
            overlay.Add(content);
            _layer.Add(overlay);
        }

        private static VisualElement Volume(string label, Func<float> get, Action<float> set) => Range(label, 0f, 1f, get, set);

        private static VisualElement Range(string label, float min, float max, Func<float> get, Action<float> set)
        {
            var slider = new Slider(label, min, max) { value = get(), showInputField = true };
            slider.AddToClassList("g-field");
            slider.RegisterValueChangedCallback(e => { set(e.newValue); SettingsService.Apply(); });
            return slider;
        }

        private static VisualElement Check(string label, Func<bool> get, Action<bool> set)
        {
            var toggle = new Toggle(label) { value = get() };
            toggle.AddToClassList("g-field");
            toggle.RegisterValueChangedCallback(e => { set(e.newValue); SettingsService.Apply(); });
            return toggle;
        }

        private void QuitToMenu()
        {
            SettingsService.Commit();
            if (ServiceRegistry.TryGet<GameSession>(out var session)) session.Save();
            Close();
            // The menu needs the mouse: closing the pause screen re-locked it for gameplay.
            UiFocus.Reset();
            if (Application.CanStreamedLevelBeLoaded(MenuScene)) SceneManager.LoadScene(MenuScene);
        }

        // ------------------------------------------------------------------ custody

        private static bool InCustody()
        {
            var online = Online.NetworkSession.Me;
            if (online != null) return online.InCustody;
            return ServiceRegistry.TryGet<GameSession>(out var s) && s.LocalCharacter != null && s.LocalCharacter.Record.InCustody;
        }

        /// <summary>What the custody screen shows: the local justice service offline, the server's view online.</summary>
        private static Core.Crime.CustodyView CurrentCustody()
        {
            var online = Online.NetworkSession.Me;
            if (online != null) return online.Custody ?? new Core.Crime.CustodyView { InCustody = online.InCustody };
            return ServiceRegistry.TryGet<GameSession>(out var s) && s.LocalCharacter != null ? s.World.Courts.Custody(s.LocalCharacter) : new Core.Crime.CustodyView();
        }

        private static string Signature(Core.Crime.CustodyView v) =>
            v.InCustody + "|" + v.ServingSentence + "|" + v.HearingDay + "|" + v.ReleaseDay + "|" + v.BailCents + "|" + v.CanPostBail + "|" + v.Counsel + "|" + v.PleaOffered + "|" + v.PleadedGuilty + "|" + v.Charges.Count;

        private float _nextCustodyCheck;

        private void RefreshCustody()
        {
            if (Time.unscaledTime < _nextCustodyCheck) return;
            _nextCustodyCheck = Time.unscaledTime + 0.5f;
            var view = CurrentCustody();
            if (_custodyView == null || Signature(view) != Signature(_custodyView)) BuildCustody();
            else UpdateCountdown();
        }

        private void UpdateCountdown()
        {
            if (_custodyCountdown == null || _custodyView == null || !ServiceRegistry.TryGet<GameSession>(out var session)) return;
            var clock = session.World.Clock;
            var left = _custodyView.NextEventSecond - clock.Now.TotalSeconds;
            if (_custodyView.NextEventSecond < 0 || left <= 0)
            {
                _custodyCountdown.text = _custodyView.NextStep;
                return;
            }
            var realMinutes = Math.Max(1, (int)Math.Ceiling(left / Math.Max(1.0, clock.TimeScale) / 60.0));
            var gameHours = (int)Math.Ceiling(left / 3600.0);
            _custodyCountdown.text = _custodyView.NextStep + "  That is " + (gameHours >= 48 ? gameHours / 24 + " days" : gameHours + " hours") +
                                     " of game time, about " + realMinutes + " real minute" + (realMinutes == 1 ? "" : "s") + ".";
        }

        private void BuildCustody()
        {
            _layer.Clear();
            var view = _custodyView = CurrentCustody();
            ServiceRegistry.TryGet<GameSession>(out var session);
            var online = Online.NetworkSession.Me != null;
            var overlay = new VisualElement();
            overlay.AddToClassList("g-custody");
            var card = new ScrollView();
            card.AddToClassList("g-custody-card");
            card.Add(Text("IN CUSTODY", "g-overlay-title"));
            card.Add(Text(view.Headline, "g-custody-sub"));
            _custodyCountdown = Text(view.NextStep, "g-custody-line");
            card.Add(_custodyCountdown);
            UpdateCountdown();
            if (view.Charges.Count > 0) card.Add(Text("Charges: " + string.Join(", ", view.Charges) + ".", "g-custody-line"));
            if (view.BailCents > 0) card.Add(Text("Bail: " + new Money(view.BailCents) + (view.CanPostBail ? "" : " (not available now)"), "g-custody-line"));
            else if (view.BailCents < 0) card.Add(Text("Bail: denied. You are held until the hearing.", "g-custody-line"));
            if (!string.IsNullOrEmpty(view.Counsel))
                card.Add(Text("Counsel: " + view.Counsel + " · case strength " + (int)(view.EvidenceStrength * 100) + "%", "g-custody-line"));
            if (view.PleadedGuilty) card.Add(Text("Guilty plea entered: the court will take a third off the sentence.", "g-custody-line"));
            else if (view.PleaOffered) card.Add(Text("The prosecutor offers a plea deal: plead guilty for a third off the sentence.", "g-custody-line"));

            var actions = new VisualElement();
            actions.AddToClassList("g-custody-actions");
            var w = session?.World;
            var me = session?.LocalCharacter;
            if (view.CanPostBail)
                actions.Add(ActionButton("Post bail " + new Money(view.BailCents), () => CustodyAct("justice.bail", () => w.Courts.PostBail(me, session.NextRequestKey("bail")), "Bail posted. You are free until your hearing.")));
            if (view.CanHireAttorney && !view.ServingSentence)
                actions.Add(ActionButton("Hire an attorney " + new Money(view.AttorneyFeeCents), () => CustodyAct("justice.attorney", () => w.Courts.HireAttorney(me, session.NextRequestKey("attorney")), "Your attorney will argue the case.")));
            if (view.PleaOffered && !view.PleadedGuilty && !view.ServingSentence)
                actions.Add(ActionButton("Accept the plea deal", () => CustodyAct("justice.plea", () => w.Courts.AcceptPlea(me), "Plea entered.")));
            if (!online && session != null && me != null && view.InCustody)
                actions.Add(ActionButton(view.ServingSentence ? "Serve the sentence" : "Wait for the hearing", () =>
                {
                    var days = session.Simulation.WaitInCustody(me);
                    _custodyStatus = days + " day" + (days == 1 ? "" : "s") + " passed. " + (me.Record.InCustody ? "Check your phone for the verdict." : "");
                    if (_screen == Screen.Custody) BuildCustody();
                }));
            actions.Add(ActionButton("Look around", () => { _custodyHidden = true; Close(); }, quiet: true));
            actions.Add(ActionButton("Pause menu", () => Switch(Screen.Pause), quiet: true));
            card.Add(actions);
            if (!string.IsNullOrEmpty(_custodyStatus)) card.Add(Text(_custodyStatus, "g-note"));
            card.Add(Text(online
                ? "Online, time runs on the server for everyone, so the wait is real. Post bail to leave now, or stay and wait; the case goes on while you are offline too."
                : "Waiting skips time: the city keeps living while you are held, so bills, rent and news still happen.", "g-note"));
            card.Add(Text("\"Look around\" hides this screen; press E to bring it back.", "g-note"));
            overlay.Add(card);
            _layer.Add(overlay);
        }

        private void CustodyAct(string op, Func<OpResult> offline, string success)
        {
            var net = Online.NetworkSession.Current;
            if (Online.NetworkSession.Replica != null && net != null)
            {
                _custodyStatus = "…";
                BuildCustody();
                _ = net.Request(op, new Dictionary<string, string>()).ContinueWith(t =>
                {
                    _custodyStatus = t.Result.Success ? success : t.Result.Error;
                    if (_screen == Screen.Custody) BuildCustody();
                }, System.Threading.Tasks.TaskScheduler.FromCurrentSynchronizationContext());
                return;
            }
            var r = offline();
            _custodyStatus = r.Success ? success : r.Error;
            BuildCustody();
        }

        // ------------------------------------------------------------------ clothes store and wardrobe

        private static readonly (string name, ClothingSlot[] slots)[] StoreSections =
        {
            ("Tops", new[] { ClothingSlot.Top }),
            ("Outerwear", new[] { ClothingSlot.Outer }),
            ("Dresses", new[] { ClothingSlot.FullBody }),
            ("Bottoms", new[] { ClothingSlot.Bottom, ClothingSlot.Belt }),
            ("Shoes", new[] { ClothingSlot.Shoes, ClothingSlot.Socks }),
            ("Hats & glasses", new[] { ClothingSlot.Hat, ClothingSlot.Glasses, ClothingSlot.Mask }),
            ("Jewellery", new[] { ClothingSlot.Necklace, ClothingSlot.Earrings, ClothingSlot.FacePiercing, ClothingSlot.Rings, ClothingSlot.Bracelet, ClothingSlot.Teeth }),
            ("Watches", new[] { ClothingSlot.Watch }),
            ("Bags & gloves", new[] { ClothingSlot.Bag, ClothingSlot.Gloves }),
        };

        /// <summary>Opens a clothes store's racks (called by the store's interactable).</summary>
        public static void OpenStore(EntityId business)
        {
            if (!Active || _instance._screen != Screen.None || UiFocus.Active) return;
            _instance._store = business;
            _instance._storeStatus = "";
            _instance.Open(Screen.Store);
        }

        private static bool OwnsClothes(GameSession session, string item, string variant)
        {
            var online = Online.NetworkSession.Me;
            if (online != null) return online.Clothes.Exists(p => p.ItemId == item && p.VariantId == variant);
            return session.LocalCharacter != null && session.World.Wardrobe.Owns(session.LocalCharacter, item, variant);
        }

        private void BuildStore()
        {
            _layer.Clear();
            if (!ServiceRegistry.TryGet<GameSession>(out var session) || session.LocalCharacter == null) return;
            var w = session.World;
            if (!w.Businesses.TryGetValue(_store, out var shop)) { Close(); return; }
            var stock = w.Wardrobe.Stock(shop);
            var overlay = new VisualElement();
            overlay.AddToClassList("g-overlay");
            var nav = new VisualElement();
            nav.AddToClassList("g-overlay-nav");
            nav.Add(Text(shop.Name.ToUpperInvariant(), "g-overlay-title"));
            var online = Online.NetworkSession.Me;
            nav.Add(Text("Cash " + (online != null ? new Money(online.CashCents) : w.Ledger.BalanceOf(session.LocalCharacter.CheckingAccount)), "g-note"));
            foreach (var section in StoreSections)
            {
                var count = stock.FindAll(i => Array.IndexOf(section.slots, i.Slot) >= 0).Count;
                if (count == 0) continue;
                var name = section.name;
                nav.Add(Nav(name.ToUpperInvariant() + "  " + count, () => { _storeFilter = name; BuildStore(); }, name == _storeFilter));
            }
            nav.Add(Nav("LEAVE", Close));
            overlay.Add(nav);

            var content = new ScrollView();
            content.AddToClassList("g-content");
            if (!string.IsNullOrEmpty(_storeStatus)) content.Add(Text(_storeStatus, "g-custody-sub"));
            var slots = Array.Find(StoreSections, x => x.name == _storeFilter).slots ?? StoreSections[0].slots;
            foreach (var item in stock)
            {
                if (Array.IndexOf(slots, item.Slot) < 0) continue;
                var row = new VisualElement();
                row.AddToClassList("g-store-row");
                var info = new VisualElement();
                info.style.flexGrow = 1;
                if (!_storeColour.TryGetValue(item.Id, out var variantId) || item.Variant(variantId) == null) variantId = item.Variants[0].Id;
                var variant = item.Variant(variantId);
                info.Add(Text(item.Label, "g-store-name"));
                info.Add(Text(item.Category + " · " + variant.Label + " · " + w.Wardrobe.PriceOf(item, variantId), "g-note"));
                var swatches = new VisualElement();
                swatches.AddToClassList("g-swatches");
                foreach (var v in item.Variants)
                {
                    var vid = v.Id;
                    var swatch = new Button(() => { _storeColour[item.Id] = vid; BuildStore(); }) { tooltip = v.Label };
                    swatch.AddToClassList("g-swatch");
                    if (ColorUtility.TryParseHtmlString(v.Hex, out var col)) swatch.style.backgroundColor = col;
                    if (vid == variantId) swatch.AddToClassList("g-swatch--selected");
                    swatches.Add(swatch);
                }
                info.Add(swatches);
                row.Add(info);
                var owned = OwnsClothes(session, item.Id, variantId);
                var buy = ActionButton(owned ? "Owned" : "Buy", () =>
                {
                    var me = session.LocalCharacter;
                    StoreAct("wardrobe.buy", new Dictionary<string, string> { ["business"] = shop.Id.ToString(), ["item"] = item.Id, ["variant"] = variantId },
                        () => w.Wardrobe.Buy(me, shop, item.Id, variantId, session.NextRequestKey("clothes")), "Bought: " + item.Label + " (" + variant.Label + "). Put it on in the Wardrobe app.");
                }, quiet: owned);
                buy.SetEnabled(!owned);
                row.Add(buy);
                content.Add(row);
            }
            overlay.Add(content);
            _layer.Add(overlay);
        }

        private void StoreAct(string op, Dictionary<string, string> args, Func<OpResult> offline, string success)
        {
            var net = Online.NetworkSession.Current;
            if (Online.NetworkSession.Replica != null && net != null)
            {
                _storeStatus = "…";
                BuildStore();
                _ = net.Request(op, args).ContinueWith(t =>
                {
                    _storeStatus = t.Result.Success ? success : t.Result.Error;
                    if (_screen == Screen.Store) BuildStore();
                }, System.Threading.Tasks.TaskScheduler.FromCurrentSynchronizationContext());
                return;
            }
            var r = offline();
            _storeStatus = r.Success ? success : r.Error;
            BuildStore();
        }

        private Outfit _draftOutfit;

        /// <summary>Phone app: saved outfits (wear, delete) and a builder that combines the clothes you own.</summary>
        private void BuildWardrobe(VisualElement body, GameSession session, Networking.Protocol.PlayerViewData online)
        {
            var w = session.World;
            var me = session.LocalCharacter;
            var outfits = online != null ? online.Outfits : me.Outfits;
            var current = online != null ? online.CurrentOutfit : me.CurrentOutfit;
            var owned = online != null ? online.Clothes : w.Wardrobe.Owned(me);
            body.Add(Text("Outfits", "g-section"));
            foreach (var o in outfits)
            {
                var outfit = o;
                var names = new List<string>();
                foreach (var p in o.Pieces) names.Add(w.Content.FindClothing(p.ItemId)?.Label ?? p.ItemId);
                body.Add(Item(o.Name + (o.Id == current ? "  (wearing)" : ""), string.Join(", ", names), o.Id == current));
                var row = new VisualElement();
                row.style.flexDirection = FlexDirection.Row;
                if (o.Id != current)
                {
                    row.Add(ActionButton("Wear", () => Act("wardrobe.wear", new Dictionary<string, string> { ["pieces"] = WardrobeRules.EncodePieces(outfit), ["id"] = outfit.Id, ["name"] = outfit.Name },
                        () => w.Wardrobe.Wear(me, outfit), "Changed into " + outfit.Name + ".")));
                    row.Add(ActionButton("Delete", () => Act("wardrobe.delete", new Dictionary<string, string> { ["id"] = outfit.Id },
                        () => w.Wardrobe.DeleteOutfit(me, outfit.Id), "Deleted."), quiet: true));
                }
                row.Add(ActionButton("Edit", () => { _draftOutfit = outfit.Copy(); BuildPhone(); }, quiet: true));
                body.Add(row);
            }
            body.Add(Text(_draftOutfit != null && !string.IsNullOrEmpty(_draftOutfit.Id) ? "Edit " + _draftOutfit.Name : "New outfit", "g-section"));
            if (_draftOutfit == null) _draftOutfit = new Outfit { Name = "Outfit " + (outfits.Count + 1) };
            var draft = _draftOutfit;
            var nameField = new TextField("Name") { value = draft.Name, maxLength = 24 };
            nameField.AddToClassList("g-field");
            nameField.RegisterValueChangedCallback(e => draft.Name = e.newValue);
            body.Add(nameField);
            foreach (ClothingSlot slot in Enum.GetValues(typeof(ClothingSlot)))
            {
                var mine = owned.FindAll(p => w.Content.FindClothing(p.ItemId)?.Slot == slot);
                if (mine.Count == 0) continue;
                var labels = new List<string> { "None" };
                foreach (var p in mine)
                {
                    var item = w.Content.FindClothing(p.ItemId);
                    labels.Add(item.Label + " (" + (item.Variant(p.VariantId)?.Label ?? p.VariantId) + ")");
                }
                var chosen = draft.Pieces.Find(p => w.Content.FindClothing(p.ItemId)?.Slot == slot);
                var index = chosen == null ? 0 : mine.FindIndex(p => p.ItemId == chosen.ItemId && p.VariantId == chosen.VariantId) + 1;
                var s = slot;
                var dropdown = new DropdownField(WardrobeRules.SlotName(slot), labels, Math.Max(0, index));
                dropdown.AddToClassList("g-field");
                dropdown.RegisterValueChangedCallback(e =>
                {
                    draft.Pieces.RemoveAll(p => w.Content.FindClothing(p.ItemId)?.Slot == s);
                    if (dropdown.index > 0) draft.Pieces.Add(mine[dropdown.index - 1].Copy());
                });
                body.Add(dropdown);
            }
            var actions = new VisualElement();
            actions.style.flexDirection = FlexDirection.Row;
            actions.Add(ActionButton("Save and wear", () =>
            {
                var rules = WardrobeRules.Validate(draft, w.Content.FindClothing);
                if (!rules.Success)
                {
                    _status = rules.Error;
                    BuildPhone();
                    return;
                }
                _draftOutfit = null;
                Act("wardrobe.wear", new Dictionary<string, string> { ["pieces"] = WardrobeRules.EncodePieces(draft), ["id"] = draft.Id, ["name"] = draft.Name },
                    () => w.Wardrobe.Wear(me, draft), "Changed into " + draft.Name + ".");
            }));
            actions.Add(ActionButton("Clear", () => { _draftOutfit = null; BuildPhone(); }, quiet: true));
            body.Add(actions);
            body.Add(Text("Buy more at clothes stores: boutiques, sporting goods, hardware stores and pharmacies each stock their own range.", "g-note"));
        }

        // ------------------------------------------------------------------ phone

        private void BuildPhone()
        {
            _layer.Clear();
            if (!ServiceRegistry.TryGet<GameSession>(out var session) || session.LocalCharacter == null) return;
            var w = session.World;
            var me = session.LocalCharacter;
            var phone = new VisualElement();
            phone.AddToClassList("g-phone");

            var status = new VisualElement();
            status.AddToClassList("g-phone-status");
            status.Add(new Label(SettingsService.Current.FormatTime(w.Clock.Now.Hour, w.Clock.Now.Minute)));
            status.Add(new Label(w.Config.Identity.CityName));
            phone.Add(status);

            var header = new VisualElement();
            header.AddToClassList("g-phone-header");
            if (_app != "")
            {
                var back = new Button(() => { _app = ""; BuildPhone(); }) { text = "‹" };
                back.AddToClassList("g-phone-back");
                header.Add(back);
            }
            header.Add(Text(_app == "" ? "Home" : _app, "g-phone-title"));
            phone.Add(header);

            var body = new ScrollView();
            body.style.flexGrow = 1;
            if (!string.IsNullOrEmpty(_status))
            {
                var status2 = Text(_status, "g-note");
                status2.style.color = new Color(0.94f, 0.7f, 0.16f);
                phone.Add(status2);
            }
            var online = Online.NetworkSession.Me;
            if (_app == "Radio" || _app == "Ripple" || _app == "Loans" || _app == "Insurance" || _app == "Businesses" || _app == "Wardrobe")
            {
                switch (_app)
                {
                    case "Wardrobe": BuildWardrobe(body, session, online); break;
                    case "Radio": BuildRadio(body, w); break;
                    case "Ripple": BuildRipple(body, session); break;
                    case "Loans": BuildLoans(body, session, online); break;
                    case "Insurance": BuildInsurance(body, session, online); break;
                    case "Businesses": BuildBusinesses(body, session, online); break;
                }
                phone.Add(body);
                _layer.Add(phone);
                return;
            }
            if (online != null && _app != "" && _app != "News" && _app != "Map")
            {
                BuildOnline(body, online);
                phone.Add(body);
                _layer.Add(phone);
                return;
            }
            switch (_app)
            {
                case "": BuildHome(body, w, me); break;
                case "Messages": BuildMessages(body, w, me); break;
                case "News": BuildNews(body, w); break;
                case "Bank": BuildBank(body, w, me); break;
                case "Properties": BuildProperties(body, w, me); break;
                case "Inventory": BuildInventory(body, w, me); break;
                case "Map": BuildMap(body, w, me); break;
            }
            phone.Add(body);
            _layer.Add(phone);
        }

        private void BuildHome(VisualElement body, Core.Simulation.World w, Core.Characters.ServerCharacter me)
        {
            var grid = new VisualElement();
            grid.AddToClassList("g-apps");
            var unread = w.Phone.UnreadCount(me);
            var online = Online.NetworkSession.Me;
            if (online != null) unread = online.Unread;
            foreach (var app in new[] { "Messages", "News", "Bank", "Properties", "Inventory", "Wardrobe", "Map", "Radio", "Ripple", "Loans", "Insurance", "Businesses" })
            {
                var captured = app;
                var b = new Button(() => { _app = captured; _status = ""; BuildPhone(); });
                b.AddToClassList("g-app");
                var icon = new VisualElement();
                icon.AddToClassList("g-app-icon");
                icon.style.backgroundColor = PhoneIcons.TileColor(app);
                var glyph = PhoneIcons.For(app);
                if (glyph != null) icon.style.backgroundImage = new StyleBackground(glyph);
                if (app == "Messages" && unread > 0) icon.Add(Text(unread.ToString(), "g-app-badge"));
                b.Add(icon);
                b.Add(Text(app, "g-app-label"));
                grid.Add(b);
            }
            body.Add(grid);
        }

        // ------------------------------------------------------------------ actions (offline: local service; online: server)

        private string _status = "";
        private string _draft = "";
        private string _loanAmount = "5000";
        private string _deductible = "1000";
        private List<string[]> _feed;

        private void Act(string op, Dictionary<string, string> args, Func<OpResult> offline, string success)
        {
            var net = Online.NetworkSession.Current;
            if (Online.NetworkSession.Replica != null && net != null)
            {
                _status = "…";
                BuildPhone();
                _ = net.Request(op, args).ContinueWith(t =>
                {
                    _status = t.Result.Success ? success : t.Result.Error;
                    if (_screen == Screen.Phone) BuildPhone();
                }, System.Threading.Tasks.TaskScheduler.FromCurrentSynchronizationContext());
                return;
            }
            var r = offline();
            _status = r.Success ? success : r.Error;
            BuildPhone();
        }

        private static long Dollars(string text) => long.TryParse(text, out var d) && d > 0 ? d * 100 : 0;

        private void BuildRadio(VisualElement body, Core.Simulation.World w)
        {
            body.Add(Text(RadioPresenter.Tuned == "" ? "Radio off" : "Tuned to " + (w.Radio.Station(RadioPresenter.Tuned)?.Name ?? RadioPresenter.Tuned), "g-note"));
            body.Add(ActionButton("Off", () => { RadioPresenter.Tune(""); BuildPhone(); }, quiet: true));
            foreach (var st in w.Radio.Stations)
            {
                var id = st.Id;
                var seg = w.Radio.OnAir(st.Id, w.Clock.Now);
                var item = Item(st.Frequency + "  " + st.Name, st.Genre + (seg != null ? "\nNow: " + seg.Title : ""), RadioPresenter.Tuned == st.Id);
                item.RegisterCallback<ClickEvent>(_ => { RadioPresenter.Tune(id); BuildPhone(); });
                body.Add(item);
            }
        }

        private void BuildRipple(VisualElement body, GameSession session)
        {
            var w = session.World;
            var me = session.LocalCharacter;
            var draft = new TextField { value = _draft, maxLength = Core.Simulation.RippleService.MaxPostLength, multiline = true };
            draft.AddToClassList("g-field");
            draft.RegisterValueChangedCallback(e => _draft = e.newValue);
            body.Add(draft);
            body.Add(ActionButton("Post", () =>
            {
                var text = _draft;
                _draft = "";
                _feed = null;
                Act("ripple.post", new Dictionary<string, string> { ["text"] = text }, () => w.Feed.Post(me, ServiceRegistry.TryGet<Core.Characters.AccountProfile>(out var profile) && profile != null ? profile.Character.FullName : "You", text, out _), "Posted.");
            }));
            var net = Online.NetworkSession.Current;
            if (Online.NetworkSession.Replica != null && net != null)
            {
                if (_feed == null)
                {
                    _feed = new List<string[]>();
                    _ = net.Request("ripple.feed", new Dictionary<string, string> { ["count"] = "20" }).ContinueWith(t =>
                    {
                        if (!t.Result.Success) return;
                        var n = int.TryParse(t.Result.Data.TryGetValue("count", out var c) ? c : "0", out var k) ? k : 0;
                        for (var i = 0; i < n; i++)
                            if (t.Result.Data.TryGetValue("post" + i, out var line)) _feed.Add(line.Split(new[] { '|' }, 6));
                        if (_screen == Screen.Phone && _app == "Ripple") BuildPhone();
                    }, System.Threading.Tasks.TaskScheduler.FromCurrentSynchronizationContext());
                }
                foreach (var p in _feed)
                {
                    if (p.Length < 6) continue;
                    var postId = p[0];
                    var item = Item(p[2], p[5]);
                    item.Add(ActionButton("♥ " + p[3], () => { _feed = null; Act("ripple.like", new Dictionary<string, string> { ["post"] = postId }, () => OpResult.Ok(), "Liked."); }, quiet: true));
                    body.Add(item);
                }
                return;
            }
            body.Add(Text("Trending: " + string.Join("  ", w.Feed.Trending().ConvertAll(t => "#" + t.Item1)), "g-note"));
            foreach (var p in w.Feed.Feed(me.CharacterId, 25, ""))
            {
                var post = p;
                var item = Item(p.AuthorName, p.Text);
                item.Add(ActionButton("♥ " + p.Likes, () => Act("ripple.like", null, () => w.Feed.Like(me, post.Id), "Liked."), quiet: true));
                body.Add(item);
            }
        }

        private void BuildLoans(VisualElement body, GameSession session, Networking.Protocol.PlayerViewData online)
        {
            var w = session.World;
            var me = session.LocalCharacter;
            if (online != null)
            {
                body.Add(Text("Credit score " + online.CreditScore + " · income " + new Money(online.VerifiedIncomeCents) + "/month · debt " + new Money(online.MonthlyDebtCents) + "/month", "g-note"));
                foreach (var l in online.Loans)
                {
                    var id = l.Id;
                    var outstanding = l.OutstandingCents;
                    var item = Item(l.Kind + " · " + l.Status, new Money(l.OutstandingCents) + " left · " + new Money(l.MonthlyCents) + "/month @ " + (l.Rate * 100).ToString("0.00") + "%");
                    item.Add(ActionButton("Pay $500", () => Act("finance.repay", new Dictionary<string, string> { ["loan"] = id, ["amount"] = "50000" }, () => OpResult.Ok(), "Payment made."), quiet: true));
                    item.Add(ActionButton("Pay off", () => Act("finance.repay", new Dictionary<string, string> { ["loan"] = id, ["amount"] = outstanding.ToString(System.Globalization.CultureInfo.InvariantCulture) }, () => OpResult.Ok(), "Loan paid off.")));
                    body.Add(item);
                }
            }
            else
            {
                var credit = w.Finance.Credit(me);
                body.Add(Text("Credit score " + credit.Score + " · income " + w.Finance.VerifiedMonthlyIncome(me) + "/month · debt " + new Money(credit.MonthlyDebtPaymentsCents) + "/month", "g-note"));
                foreach (var loan in w.Loans.Loans)
                {
                    if (loan.Borrower != me.CharacterId || loan.Status == Core.Economy.LoanStatus.PaidOff) continue;
                    var l = loan;
                    var item = Item(loan.Kind + " · " + loan.Status, new Money(loan.OutstandingCents) + " left · " + new Money(loan.MonthlyPaymentCents) + "/month @ " + (loan.AnnualRate * 100).ToString("0.00") + "%");
                    item.Add(ActionButton("Pay $500", () => Act("finance.repay", null, () => w.Finance.Repay(me, l, Money.FromDollars(500), session.NextRequestKey("repay")), "Payment made."), quiet: true));
                    item.Add(ActionButton("Pay off", () => Act("finance.repay", null, () => w.Finance.Repay(me, l, new Money(l.OutstandingCents), session.NextRequestKey("payoff")), "Loan paid off.")));
                    body.Add(item);
                }
            }
            body.Add(Text("Personal loan (36 months)", "g-section"));
            var amount = new TextField("Amount $") { value = _loanAmount };
            amount.AddToClassList("g-field");
            amount.RegisterValueChangedCallback(e => _loanAmount = e.newValue);
            body.Add(amount);
            if (online == null)
            {
                var offer = w.Finance.QuoteLoan(me, Core.Economy.LoanKind.Personal, new Money(Dollars(_loanAmount)), 36, EntityId.None);
                body.Add(Text(offer.Approved ? (offer.AnnualRate * 100).ToString("0.00") + "% · " + new Money(offer.MonthlyPaymentCents) + "/month" : offer.Reason, "g-note"));
            }
            body.Add(ActionButton("Apply", () =>
            {
                var allowed = session.CheckAllowed("finance.loan");
                if (!allowed.Success) { _status = allowed.Error; BuildPhone(); return; }
                Act("finance.loan", new Dictionary<string, string> { ["kind"] = "Personal", ["amount"] = Dollars(_loanAmount).ToString(System.Globalization.CultureInfo.InvariantCulture), ["term"] = "36" },
                    () => w.Finance.TakeLoan(me, Core.Economy.LoanKind.Personal, new Money(Dollars(_loanAmount)), 36, EntityId.None, session.NextRequestKey("loan")), "Funds deposited.");
            }));
        }

        private void BuildInsurance(VisualElement body, GameSession session, Networking.Protocol.PlayerViewData online)
        {
            var w = session.World;
            var me = session.LocalCharacter;
            if (online != null)
            {
                foreach (var p in online.Policies)
                {
                    var id = p.Id;
                    var item = Item(p.Kind + " · " + p.Status, new Money(p.PremiumCents) + "/month · cover " + new Money(p.CoverCents) + " · claims " + p.Claims);
                    if (p.Active && p.AssessedLossCents > 0) item.Add(ActionButton("Claim " + new Money(p.AssessedLossCents), () => Act("insurance.claim", new Dictionary<string, string> { ["policy"] = id }, () => OpResult.Ok(), "Claim paid.")));
                    if (p.Active) item.Add(ActionButton("Cancel", () => Act("insurance.cancel", new Dictionary<string, string> { ["policy"] = id }, () => OpResult.Ok(), "Policy cancelled."), quiet: true));
                    body.Add(item);
                }
            }
            else
            {
                foreach (var p in w.Insurance.ForHolder(me.CharacterId))
                {
                    var policy = p;
                    var item = Item(p.Kind + " · " + p.Status, new Money(p.MonthlyPremiumCents) + "/month · cover " + new Money(p.RemainingCoverageCents) + " · claims " + p.Claims);
                    var loss = p.Active ? w.Finance.AssessedLoss(p) : Money.Zero;
                    if (loss.Cents > 0) item.Add(ActionButton("Claim " + loss, () => Act("insurance.claim", null, () => w.Finance.Claim(me, policy, session.NextRequestKey("claim")), "Claim paid.")));
                    if (p.Active) item.Add(ActionButton("Cancel", () => Act("insurance.cancel", null, () => w.Finance.CancelInsurance(me, policy), "Policy cancelled."), quiet: true));
                    body.Add(item);
                }
            }
            body.Add(Text("Get covered", "g-section"));
            var deductible = new TextField("Deductible $") { value = _deductible };
            deductible.AddToClassList("g-field");
            deductible.RegisterValueChangedCallback(e => _deductible = e.newValue);
            body.Add(deductible);
            var targets = new List<(string kind, string asset, string label)>();
            if (online != null) foreach (var i in online.Insurables) targets.Add((i.Kind, i.Asset, i.Label));
            else
            {
                targets.Add(("Health", "", "Health"));
                var assets = new List<EntityId>(w.Ownership.AssetsOf(me.CharacterId));
                assets.Sort();
                foreach (var a in assets)
                {
                    if (a.Kind == EntityKind.Property) targets.Add(("Property", a.ToString(), w.Properties.Get(a)?.Address ?? a.ToString()));
                    else if (a.Kind == EntityKind.Vehicle) targets.Add(("Vehicle", a.ToString(), w.Vehicles.Get(a)?.Plate ?? a.ToString()));
                    else if (a.Kind == EntityKind.Business && w.Businesses.TryGetValue(a, out var biz)) targets.Add(("BusinessInterruption", a.ToString(), biz.Name));
                }
            }
            foreach (var (kind, asset, label) in targets)
            {
                var k = kind;
                var a = asset;
                var line = kind + " · " + label;
                if (online == null && Enum.TryParse(k, out Core.Economy.InsuranceKind ik))
                {
                    EntityId.TryParse(a, out var assetId);
                    var q = w.Finance.QuoteInsurance(me, ik, assetId, new Money(Dollars(_deductible)));
                    if (!q.Available) continue;
                    line += " — " + new Money(q.MonthlyPremiumCents) + "/month";
                }
                var item = Item(line, "");
                item.Add(ActionButton("Insure", () =>
                {
                    var args = new Dictionary<string, string> { ["kind"] = k, ["deductible"] = Dollars(_deductible).ToString(System.Globalization.CultureInfo.InvariantCulture) };
                    if (a.Length > 0) args["asset"] = a;
                    Act("insurance.buy", args, () =>
                    {
                        if (!Enum.TryParse(k, out Core.Economy.InsuranceKind kindValue)) return OpResult.Fail("Unknown cover.");
                        EntityId.TryParse(a, out var id);
                        return w.Finance.BuyInsurance(me, kindValue, id, new Money(Dollars(_deductible)), session.NextRequestKey("insure"));
                    }, "Covered.");
                }));
                body.Add(item);
            }
        }

        private void BuildBusinesses(VisualElement body, GameSession session, Networking.Protocol.PlayerViewData online)
        {
            var w = session.World;
            if (online != null)
            {
                if (online.Businesses.Count == 0) body.Add(Text("You don't own a business yet.", "g-note"));
                foreach (var b in online.Businesses)
                    body.Add(Item(b.Name, "Account " + new Money(b.BalanceCents) + " · yesterday " + (b.LastDayClosed ? "closed" : b.LastDayCustomers + " customers, " + new Money(b.LastDayProfitCents))));
                body.Add(Text("Manage staff, prices and stock at the business's office counter.", "g-note"));
                return;
            }
            var owned = w.BusinessOps.OwnedBy(session.LocalCharacter.CharacterId);
            if (owned.Count == 0) body.Add(Text("You don't own a business yet.", "g-note"));
            foreach (var b in owned)
            {
                var biz = b;
                var last = b.Reports.Count > 0 ? b.Reports[b.Reports.Count - 1] : null;
                var item = Item(b.Name, "Account " + w.Ledger.BalanceOf(b.Account) + (last != null ? " · yesterday " + (last.WasClosed ? "closed" : last.Customers + " customers, " + new Money(last.ProfitCents)) : ""));
                item.Add(ActionButton("Manage", () => { Close(); BusinessPanel.Open(biz); }));
                body.Add(item);
            }
            body.Add(Text("For sale", "g-section"));
            foreach (var b in w.BusinessOps.ForSale())
            {
                var biz = b;
                var item = Item(b.Name, w.BusinessOps.PriceOf(b).Total.ToString());
                item.Add(ActionButton("View", () => { Close(); BusinessPanel.Open(biz); }, quiet: true));
                body.Add(item);
            }
        }

        /// <summary>Messages, bank, property and inventory from the server's view of this player (online).</summary>
        private static void BuildOnline(VisualElement body, Networking.Protocol.PlayerViewData v)
        {
            var settings = SettingsService.Current;
            switch (_instance != null ? _instance._app : "")
            {
                case "Messages":
                    if (v.Messages.Count == 0) body.Add(Text("No messages yet.", "g-note"));
                    foreach (var m in v.Messages) body.Add(Item(m.From + " · " + m.Category + " · day " + m.Day + " " + settings.FormatTime(m.Hour, m.Minute), m.Body, !m.Read));
                    if (v.Unread > 0) _ = Online.NetworkSession.Current?.Request("phone.read");
                    break;
                case "Bank":
                    body.Add(Text("Checking", "g-note"));
                    body.Add(Text(new Money(v.CashCents).ToString(), "g-big-number"));
                    if (v.SavingsCents >= 0) body.Add(Text("Savings  " + new Money(v.SavingsCents), "g-list-title"));
                    if (v.MedicalDebtCents > 0) body.Add(Text("Medical debt  " + new Money(v.MedicalDebtCents), "g-list-alert"));
                    if (v.FinesOwedCents > 0) body.Add(Text("Fines owed  " + new Money(v.FinesOwedCents), "g-list-alert"));
                    foreach (var b in v.Businesses)
                        body.Add(Item(b.Name, "Account " + new Money(b.BalanceCents) + " · yesterday " + (b.LastDayClosed ? "closed" : b.LastDayCustomers + " customers, " + new Money(b.LastDayProfitCents))));
                    body.Add(Text("Statement", "g-section"));
                    foreach (var s in v.Statement) body.Add(Item(new Money(s.AmountCents).ToString(), s.Reason + (string.IsNullOrEmpty(s.Memo) ? "" : " · " + s.Memo)));
                    break;
                case "Properties":
                    if (v.Properties.Count == 0) body.Add(Text("You don't own any property yet.", "g-note"));
                    else
                    {
                        body.Add(Text("Equity", "g-note"));
                        body.Add(Text(new Money(v.PropertyValueCents - v.PropertyOwedCents).ToString(), "g-big-number"));
                        body.Add(Text("Net " + new Money(v.PropertyMonthlyNetCents) + "/month", "g-note"));
                    }
                    foreach (var p in v.Properties)
                    {
                        var item = Item(p.Address + (p.ForSale ? "  (listed)" : ""), p.Kind + (p.District.Length > 0 ? " · " + p.District : "") + "\nWorth " + new Money(p.ValueCents) +
                            " · mortgage " + new Money(p.OwedCents) + "\nOccupied " + p.Occupied + "/" + p.Units + " · rent " + new Money(p.RentCents) + " · net " + new Money(p.NetCents) + "/month", p.Alerts.Count > 0);
                        foreach (var alert in p.Alerts) item.Add(Text("▲ " + alert, "g-list-alert"));
                        body.Add(item);
                    }
                    break;
                case "Inventory":
                    if (v.Inventory.Count == 0) body.Add(Text("Your pockets are empty.", "g-note"));
                    foreach (var i in v.Inventory)
                        body.Add(Item((i.Quantity > 1 ? i.Quantity + " × " : "") + i.Name, new Money(i.TotalValueCents) + (i.Stolen ? " · stolen" : "") + (i.Illegal ? " · illegal" : ""), i.Stolen || i.Illegal));
                    break;
            }
        }

        private static VisualElement Item(string title, string text, bool highlight = false)
        {
            var item = new VisualElement();
            item.AddToClassList("g-list-item");
            if (highlight) item.AddToClassList("g-list-item--unread");
            if (!string.IsNullOrEmpty(title)) item.Add(Text(title, "g-list-title"));
            if (!string.IsNullOrEmpty(text)) item.Add(Text(text, "g-list-text"));
            return item;
        }

        private static void BuildMessages(VisualElement body, Core.Simulation.World w, Core.Characters.ServerCharacter me)
        {
            if (me.Inbox.Count == 0) body.Add(Text("No messages yet.", "g-note"));
            var settings = SettingsService.Current;
            for (var i = me.Inbox.Count - 1; i >= 0 && i >= me.Inbox.Count - 50; i--)
            {
                var m = me.Inbox[i];
                body.Add(Item(m.FromName + " · " + m.Category + " · day " + m.At.DayIndex + " " + settings.FormatTime(m.At.Hour, m.At.Minute), m.Body, !m.Read));
            }
            foreach (var m in me.Inbox) if (!m.Read) w.Phone.MarkRead(me, m.Id);
        }

        private static void BuildNews(VisualElement body, Core.Simulation.World w)
        {
            foreach (var e in w.Calendar.ActiveOn(w.Clock.Now)) body.Add(Item("Today: " + e.Name, e.Announcement, true));
            foreach (var a in w.Phone.News()) body.Add(Item(a.Headline, a.Outlet + " · day " + a.Day + (a.Body != a.Headline ? "\n" + a.Body : "")));
        }

        private static void BuildBank(VisualElement body, Core.Simulation.World w, Core.Characters.ServerCharacter me)
        {
            body.Add(Text("Checking", "g-note"));
            body.Add(Text(w.Ledger.BalanceOf(me.CheckingAccount).ToString(), "g-big-number"));
            var savings = w.Finance.SavingsOf(me);
            if (savings.IsValid) body.Add(Text("Savings  " + w.Ledger.BalanceOf(savings), "g-list-title"));
            if (me.MedicalDebtCents > 0) body.Add(Text("Medical debt  " + new Money(me.MedicalDebtCents), "g-list-alert"));
            if (me.Record.FinesOwedCents > 0) body.Add(Text("Fines owed  " + new Money(me.Record.FinesOwedCents), "g-list-alert"));
            body.Add(Text("Statement", "g-section"));
            for (var i = me.Statement.Count - 1; i >= 0 && i >= me.Statement.Count - 30; i--)
            {
                var s = me.Statement[i];
                body.Add(Item(new Money(s.AmountCents).ToString(), s.Reason + (string.IsNullOrEmpty(s.Memo) ? "" : " · " + s.Memo)));
            }
            body.Add(ActionButton("Transfers and savings…", () => { _instance?.Close(); PhonePanel.OpenClassic("Bank"); }, quiet: true));
        }

        private static void BuildProperties(VisualElement body, Core.Simulation.World w, Core.Characters.ServerCharacter me)
        {
            var p = PlayerViews.PortfolioOf(w, me);
            if (p.Entries.Count == 0)
            {
                body.Add(Text("You don't own any property yet. Look for For Sale signs, or search the Map.", "g-note"));
                return;
            }
            body.Add(Text("Equity", "g-note"));
            body.Add(Text(p.NetWorthInProperty.ToString(), "g-big-number"));
            body.Add(Text("Worth " + p.TotalValue + " · owed " + p.TotalOwed + " · net " + p.MonthlyNet + "/month", "g-note"));
            foreach (var e in p.Entries)
            {
                var item = Item(e.Address + (e.ForSale ? "  (listed)" : ""),
                    e.Kind + (e.District.Length > 0 ? " · " + e.District : "") + "\nWorth " + e.MarketValue + " · mortgage " + e.MortgageOwed +
                    "\nOccupied " + e.OccupiedUnits + "/" + e.Units + " · rent " + e.MonthlyRentIncome + " · net " + e.MonthlyNet + "/month" + (e.Insured ? " · insured" : ""),
                    e.Alerts.Count > 0);
                foreach (var alert in e.Alerts) item.Add(Text("▲ " + alert, "g-list-alert"));
                body.Add(item);
            }
        }

        private static void BuildInventory(VisualElement body, Core.Simulation.World w, Core.Characters.ServerCharacter me)
        {
            var lines = PlayerViews.InventoryOf(w.Content, me);
            if (lines.Count == 0) body.Add(Text("Your pockets are empty.", "g-note"));
            var total = Money.Zero;
            var category = "";
            foreach (var l in lines)
            {
                if (l.Category != category)
                {
                    category = l.Category;
                    body.Add(Text(category.ToUpperInvariant(), "g-section"));
                }
                body.Add(Item((l.Quantity > 1 ? l.Quantity + " × " : "") + l.Name, l.TotalValue + (l.Stolen ? " · stolen" : "") + (l.Illegal ? " · illegal" : ""), l.Confiscatable));
                total += l.TotalValue;
            }
            if (lines.Count > 0)
                body.Add(Text("Estimated value " + total + (lines.Exists(l => l.Confiscatable) ? ". Police seize stolen or illegal items on arrest." : "."), "g-note"));
        }

        private void BuildMap(VisualElement body, Core.Simulation.World w, Core.Characters.ServerCharacter me)
        {
            var search = new TextField { value = _mapQuery };
            search.AddToClassList("g-field");
            var results = new VisualElement();
            void Fill()
            {
                results.Clear();
                var settings = SettingsService.Current;
                var shown = 0;
                foreach (var p in w.Phone.SearchMap(_mapQuery))
                {
                    var d = w.Geography.GetDistrict(p.District);
                    var dist = WorldPosition.DistanceXZ(p.Position, me.LastPosition);
                    var open = p.OpenMinute == p.CloseMinute ? "" : p.IsOpenAt(w.Clock.Now.MinuteOfDay) ? " · open" : " · closed";
                    results.Add(Item(p.Name, p.Kind + (d != null ? " · " + d.Name : "") + " · " + settings.FormatDistance(dist) + open));
                    if (++shown >= 40) break;
                }
            }
            search.RegisterValueChangedCallback(e => { _mapQuery = e.newValue; Fill(); });
            body.Add(search);
            body.Add(results);
            Fill();
        }
    }
}
