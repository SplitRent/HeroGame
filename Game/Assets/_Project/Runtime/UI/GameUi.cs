using System;
using System.Collections.Generic;
using HeroGame.Core.Foundation;
using HeroGame.Core.Presentation;
using HeroGame.Runtime.Bootstrap;
using HeroGame.Runtime.Interaction;
using HeroGame.Runtime.Player;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.UIElements;

namespace HeroGame.Runtime.UI
{
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

        private enum Screen { None, Pause, Phone }
        private enum SettingsTab { Audio, Controls, Display, Accessibility }

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
        private string _mapQuery = "";

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
            var style = Resources.Load<StyleSheet>("UI/Game");
            if (style != null && !_root.styleSheets.Contains(style)) _root.styleSheets.Add(style);
            _root.pickingMode = PickingMode.Ignore;

            _layer = _root.Q<VisualElement>("layer");
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
                if (_screen != Screen.None) Close();
                else if (!UiFocus.Active) Open(Screen.Pause);
            }
            else if (input.PhonePressed && !PhonePanel.ClassicOpen)
            {
                if (_screen == Screen.Phone) Close();
                else if (_screen == Screen.None && !UiFocus.Active) Open(Screen.Phone);
            }

            _fpsFrames++;
            _fpsTime += Time.unscaledDeltaTime;
            if (Time.unscaledTime >= _nextRefresh)
            {
                _nextRefresh = Time.unscaledTime + 0.1f;
                RefreshHud();
            }
            RefreshToasts();
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

            var prompt = Interactor != null ? Interactor.PromptText : "";
            _prompt.text = string.IsNullOrEmpty(prompt) ? "" : "[E]  " + prompt;
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

            _fps.style.display = s.ShowFps ? DisplayStyle.Flex : DisplayStyle.None;
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

        private void Close()
        {
            if (_screen == Screen.Pause) SettingsService.Commit();
            _screen = Screen.None;
            _layer.Clear();
            _layer.pickingMode = PickingMode.Ignore;
            UiFocus.Release();
        }

        private void Rebuild()
        {
            _layer.Clear();
            _layer.pickingMode = _screen == Screen.None ? PickingMode.Ignore : PickingMode.Position;
            if (_screen == Screen.Pause) BuildPause(SettingsTab.Audio);
            else if (_screen == Screen.Phone) BuildPhone();
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
                {
                    content.Add(Text("Display", "g-section"));
                    var quality = new DropdownField("Quality", new List<string>(Enum.GetNames(typeof(QualityPreset))), (int)s.Quality);
                    quality.AddToClassList("g-field");
                    quality.RegisterValueChangedCallback(e => { s.Quality = (QualityPreset)quality.index; SettingsService.Apply(); });
                    content.Add(quality);
                    content.Add(Check("VSync", () => s.VSync, v => s.VSync = v));
                    content.Add(Range("Frame cap (VSync off)", GameSettings.MinFps, GameSettings.MaxFps, () => s.TargetFps, v => s.TargetFps = Mathf.RoundToInt(v)));
                    content.Add(Range("Field of view", GameSettings.MinFov, GameSettings.MaxFov, () => s.FieldOfView, v => s.FieldOfView = v));
                    content.Add(Range("Interface scale", GameSettings.MinUiScale, GameSettings.MaxUiScale, () => s.UiScale, v => s.UiScale = v));
                    content.Add(Check("Show frame rate", () => s.ShowFps, v => s.ShowFps = v));
                    var fullscreen = new Toggle("Fullscreen") { value = UnityEngine.Screen.fullScreen };
                    fullscreen.AddToClassList("g-field");
                    fullscreen.RegisterValueChangedCallback(e => UnityEngine.Screen.fullScreen = e.newValue);
                    content.Add(fullscreen);
                    break;
                }
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
            if (Application.CanStreamedLevelBeLoaded(MenuScene)) SceneManager.LoadScene(MenuScene);
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
            if (_app == "Radio" || _app == "Ripple" || _app == "Loans" || _app == "Insurance" || _app == "Businesses")
            {
                switch (_app)
                {
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
            foreach (var app in new[] { "Messages", "News", "Bank", "Properties", "Inventory", "Map", "Radio", "Ripple", "Loans", "Insurance", "Businesses" })
            {
                var captured = app;
                var b = new Button(() => { _app = captured; _status = ""; BuildPhone(); }) { text = app };
                b.AddToClassList("g-app");
                if (app == "Messages" && unread > 0) b.Add(Text(unread.ToString(), "g-app-badge"));
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
