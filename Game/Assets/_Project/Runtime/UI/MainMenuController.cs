using System;
using System.Collections.Generic;
using System.IO;
using HeroGame.Core.Characters;
using HeroGame.Core.Population;
using HeroGame.Core.Servers;
using HeroGame.Persistence.Json;
using HeroGame.Persistence.Storage;
using HeroGame.Runtime.Bootstrap;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.UIElements;

namespace HeroGame.Runtime.UI
{
    /// <summary>
    /// Front end (GDD §6–7): Continue, Story Mode, Servers, Character, Options, Settings, Exit.
    /// Built on UI Toolkit; layout in Resources/UI/MainMenu.uxml, style in MainMenu.uss. On first run
    /// the player is sent to CREATE YOUR CHARACTER before anything else.
    /// </summary>
    [RequireComponent(typeof(UIDocument))]
    public sealed class MainMenuController : MonoBehaviour
    {
        public string GameplayScene = "VerticalSlice_Greybox";
        public PanelSettings PanelSettings;

        private UIDocument _document;
        private VisualElement _panel;
        private AccountProfile _account;
        private readonly ServerFilter _filter = new ServerFilter();
        private List<ServerListing> _servers = new List<ServerListing>();

        private void OnEnable()
        {
            _document = GetComponent<UIDocument>();
            if (_document.panelSettings == null)
                _document.panelSettings = PanelSettings != null ? PanelSettings : ScriptableObject.CreateInstance<PanelSettings>();
            if (_document.visualTreeAsset == null) _document.visualTreeAsset = Resources.Load<VisualTreeAsset>("UI/MainMenu");
            var root = _document.rootVisualElement;
            if (root == null) return;
            if (root.childCount == 0 && _document.visualTreeAsset != null) _document.visualTreeAsset.CloneTree(root);
            var style = Resources.Load<StyleSheet>("UI/MainMenu");
            if (style != null && !root.styleSheets.Contains(style)) root.styleSheets.Add(style);

            _panel = root.Q<VisualElement>("panel");
            root.Q<Label>("title").text = GameInfo.WorkingTitle;
            root.Q<Label>("footer").text = "Build " + GameInfo.Version + " · Working title · Not for distribution";

            _account = GameBootstrap.Accounts.Load("local");
            Bind(root, "continue", () => Launch(SessionMode.LocalServer, "local-dev", "local-dev"));
            Bind(root, "story", ShowStory);
            Bind(root, "servers", ShowServers);
            Bind(root, "character", ShowCharacterCreator);
            Bind(root, "options", ShowOptions);
            Bind(root, "settings", ShowSettings);
            Bind(root, "exit", Quit);

            root.Q<Button>("continue").style.display = _account != null && _account.CharacterCreated ? DisplayStyle.Flex : DisplayStyle.None;
            if (_account == null || !_account.CharacterCreated) ShowCharacterCreator();
            else ShowServers();
        }

        private static void Bind(VisualElement root, string name, Action action)
        {
            var button = root.Q<Button>(name);
            if (button != null) button.clicked += action;
        }

        private VisualElement BeginPanel(string title)
        {
            _panel.Clear();
            var header = new Label(title);
            header.AddToClassList("hg-panel-title");
            _panel.Add(header);
            return _panel;
        }

        private void ShowStory()
        {
            var p = BeginPanel("STORY MODE");
            p.Add(Muted("Port Arden, Texas. Grow up in Eastwater, then live the life you choose four years later."));
            p.Add(Muted("Chapter structure is in development (Phase 15–17). This launches the story save slot on the vertical-slice world."));
            p.Add(new Button(() => Launch(SessionMode.Story, "story", "slot-1")) { text = "BEGIN — SLOT 1" });
        }

        private void ShowServers()
        {
            var p = BeginPanel("SERVERS");
            _servers = LoadServerList();
            var search = new TextField("Search") { value = _filter.Search };
            search.AddToClassList("hg-field");
            search.RegisterValueChangedCallback(e => { _filter.Search = e.newValue; RefreshServers(); });
            p.Add(search);

            var toggles = new VisualElement();
            toggles.AddToClassList("hg-row");
            toggles.Add(Toggle("Friends", _filter.FriendsOnly, v => _filter.FriendsOnly = v));
            toggles.Add(Toggle("Hide full", _filter.HideFull, v => _filter.HideFull = v));
            toggles.Add(Toggle("Roleplay only", _filter.Roleplay == true, v => _filter.Roleplay = v ? true : (bool?)null));
            toggles.Add(Toggle("No powers", _filter.PowersEnabled == false, v => _filter.PowersEnabled = v ? false : (bool?)null));
            toggles.Add(Toggle("Hide locked", _filter.HidePasswordProtected, v => _filter.HidePasswordProtected = v));
            p.Add(toggles);

            var list = new ScrollView { name = "server-list" };
            list.style.flexGrow = 1;
            p.Add(list);
            var host = new Button(() => Launch(SessionMode.LocalServer, "local-dev", "local-dev")) { text = "HOST LOCAL WORLD" };
            p.Add(host);
            RefreshServers();
        }

        private void RefreshServers()
        {
            var list = _panel.Q<ScrollView>("server-list");
            if (list == null) return;
            list.Clear();
            var shown = ServerBrowser.Apply(_servers, _filter, ServerSort.Population, GameInfo.Version.Split('-')[0]);
            if (shown.Count == 0) list.Add(Muted("No servers match these filters."));
            foreach (var s in shown)
            {
                var row = new VisualElement();
                row.AddToClassList("hg-server");
                var left = new VisualElement();
                var name = new Label(s.Name + (s.PasswordProtected ? "  [locked]" : "") + (s.Whitelisted ? "  [whitelist]" : ""));
                name.AddToClassList("hg-server-name");
                left.Add(name);
                var meta = new Label(s.CityName + " · " + s.Region + " · " + (s.Roleplay ? "Roleplay · " : "") + (s.Pvp ? "PvP · " : "PvE · ") +
                                     "Powers: " + s.Powers + " · Economy: " + s.Economy + (s.FriendsOnline.Count > 0 ? " · Friends: " + string.Join(", ", s.FriendsOnline) : ""));
                meta.AddToClassList("hg-server-meta");
                left.Add(meta);
                row.Add(left);
                var right = new Label(s.Population + "/" + s.MaxPopulation + "   " + s.PingMs + " ms");
                right.AddToClassList("hg-server-meta");
                row.Add(right);
                row.RegisterCallback<ClickEvent>(_ => ShowServerDetails(s));
                list.Add(row);
            }
        }

        private void ShowServerDetails(ServerListing s)
        {
            var p = BeginPanel(s.Name.ToUpperInvariant());
            p.Add(Muted(s.Description));
            p.Add(Muted("City: " + s.CityName + " · Owner: " + s.Owner + " · Region " + s.Region + " · Version " + s.Version));
            p.Add(Muted("Rules: " + (s.Rules.Count > 0 ? string.Join(" · ", s.Rules) : "none listed")));
            p.Add(Muted("Online multiplayer connects in Phase 9. Joining opens a local world configured like this server."));
            p.Add(new Button(() => Launch(SessionMode.LocalServer, s.ServerId, s.ServerId)) { text = "JOIN (LOCAL PREVIEW)" });
            p.Add(new Button(ShowServers) { text = "BACK" });
        }

        private void ShowCharacterCreator()
        {
            var p = BeginPanel("CREATE YOUR CHARACTER");
            var profile = _account ?? new AccountProfile { AccountId = Core.Foundation.EntityId.Create(Core.Foundation.EntityKind.UserAccount, 1) };
            var c = profile.Character;
            p.Add(Muted("Your name and appearance follow you to every server. Everything else — money, home, job, powers — is lived separately on each one."));
            var first = Field("First name", c.FirstName);
            var last = Field("Surname", c.LastName);
            var age = new SliderInt("Age", 18, 70) { value = Mathf.Clamp(c.Age, 18, 70) };
            age.AddToClassList("hg-field");
            var presentation = new DropdownField("Presentation", new List<string> { "Feminine", "Masculine", "Androgynous" }, (int)c.Presentation);
            presentation.AddToClassList("hg-field");
            var height = new Slider("Height (cm)", 150f, 205f) { value = c.Appearance.HeightCm };
            height.AddToClassList("hg-field");
            var skin = new SliderInt("Skin tone", 0, 9) { value = c.Appearance.SkinTone };
            skin.AddToClassList("hg-field");
            var build = new Slider("Build", 0f, 1f) { value = c.Appearance.BodyWeight };
            build.AddToClassList("hg-field");
            var jaw = new Slider("Jaw width", 0f, 1f) { value = c.Appearance.GetMorph("jaw_width") };
            jaw.AddToClassList("hg-field");
            var nose = new Slider("Nose length", 0f, 1f) { value = c.Appearance.GetMorph("nose_length") };
            nose.AddToClassList("hg-field");
            foreach (var e in new VisualElement[] { first, last, age, presentation, height, skin, build, jaw, nose }) p.Add(e);
            var status = Muted("");
            p.Add(new Button(() =>
            {
                if (string.IsNullOrWhiteSpace(first.value) || string.IsNullOrWhiteSpace(last.value))
                {
                    status.text = "Enter a first name and surname.";
                    return;
                }
                c.FirstName = first.value.Trim();
                c.LastName = last.value.Trim();
                c.Age = age.value;
                c.Presentation = (GenderPresentation)Mathf.Max(0, presentation.index);
                c.Appearance.HeightCm = height.value;
                c.Appearance.SkinTone = skin.value;
                c.Appearance.BodyWeight = build.value;
                c.Appearance.SetMorph("jaw_width", jaw.value);
                c.Appearance.SetMorph("nose_length", nose.value);
                profile.CharacterCreated = true;
                GameBootstrap.Accounts.Save("local", profile);
                _account = profile;
                _document.rootVisualElement.Q<Button>("continue").style.display = DisplayStyle.Flex;
                status.text = "Saved. " + c.FullName + " is ready.";
            }) { text = "SAVE CHARACTER" });
            p.Add(status);
        }

        private void ShowOptions()
        {
            var p = BeginPanel("OPTIONS");
            var sens = new Slider("Mouse sensitivity", 0.02f, 0.4f) { value = PlayerPrefs.GetFloat("hg.sensitivity", 0.12f) };
            sens.AddToClassList("hg-field");
            sens.RegisterValueChangedCallback(e => PlayerPrefs.SetFloat("hg.sensitivity", e.newValue));
            var invert = new Toggle("Invert look") { value = PlayerPrefs.GetInt("hg.invert", 0) == 1 };
            invert.RegisterValueChangedCallback(e => PlayerPrefs.SetInt("hg.invert", e.newValue ? 1 : 0));
            var subtitles = new Toggle("Subtitles") { value = PlayerPrefs.GetInt("hg.subtitles", 1) == 1 };
            subtitles.RegisterValueChangedCallback(e => PlayerPrefs.SetInt("hg.subtitles", e.newValue ? 1 : 0));
            p.Add(sens);
            p.Add(invert);
            p.Add(subtitles);
        }

        private void ShowSettings()
        {
            // The same settings file the in-game pause screen edits (GameSettings via SettingsService).
            var p = BeginPanel("SETTINGS");
            var s = SettingsService.Current;
            void Slider01(string label, Func<float> get, Action<float> set)
            {
                var slider = new Slider(label, 0f, 1f) { value = get() };
                slider.AddToClassList("hg-field");
                slider.RegisterValueChangedCallback(e => { set(e.newValue); SettingsService.Apply(); });
                p.Add(slider);
            }
            void Check(string label, Func<bool> get, Action<bool> set)
            {
                var toggle = new Toggle(label) { value = get() };
                toggle.AddToClassList("hg-field");
                toggle.RegisterValueChangedCallback(e => { set(e.newValue); SettingsService.Apply(); });
                p.Add(toggle);
            }
            Slider01("Master volume", () => s.MasterVolume, v => s.MasterVolume = v);
            Slider01("Music & radio", () => s.MusicVolume, v => s.MusicVolume = v);
            Slider01("Effects", () => s.EffectsVolume, v => s.EffectsVolume = v);
            var quality = new DropdownField("Quality", new List<string>(Enum.GetNames(typeof(Core.Presentation.QualityPreset))), (int)s.Quality);
            quality.AddToClassList("hg-field");
            quality.RegisterValueChangedCallback(e => { s.Quality = (Core.Presentation.QualityPreset)quality.index; SettingsService.Apply(); });
            p.Add(quality);
            var fullscreen = new Toggle("Fullscreen") { value = Screen.fullScreen };
            fullscreen.AddToClassList("hg-field");
            fullscreen.RegisterValueChangedCallback(e => Screen.fullScreen = e.newValue);
            p.Add(fullscreen);
            Check("V-Sync", () => s.VSync, v => s.VSync = v);
            Check("Subtitles", () => s.Subtitles, v => s.Subtitles = v);
            Check("24-hour clock", () => s.Clock24h, v => s.Clock24h = v);
            Check("Metric units", () => s.Units == Core.Presentation.UnitSystem.Metric, v => s.Units = v ? Core.Presentation.UnitSystem.Metric : Core.Presentation.UnitSystem.Imperial);
            var save = new Button(() => SettingsService.Commit()) { text = "SAVE" };
            save.AddToClassList("hg-menu-button");
            save.AddToClassList("hg-menu-button--accent");
            p.Add(save);
            var more = new Label("More options (controls, accessibility, interface scale) are in the pause menu in game.");
            more.AddToClassList("hg-muted");
            p.Add(more);
        }

        private void Launch(SessionMode mode, string serverId, string slot)
        {
            if (_account == null || !_account.CharacterCreated)
            {
                ShowCharacterCreator();
                return;
            }
            LaunchRequest.Set(mode, serverId, slot);
            SceneManager.LoadScene(GameplayScene);
        }

        private static void Quit()
        {
#if UNITY_EDITOR
            UnityEditor.EditorApplication.isPlaying = false;
#else
            Application.Quit();
#endif
        }

        private static List<ServerListing> LoadServerList()
        {
            // Phase 9 replaces this with the master-server query; the browser logic is already final.
            var path = Path.Combine(Application.streamingAssetsPath, "Data", "server_list_mock.json");
            if (!File.Exists(path)) return new List<ServerListing>();
            return JsonSetup.Deserialize<List<ServerListing>>(AtomicFile.ReadAllText(path)) ?? new List<ServerListing>();
        }

        private static Label Muted(string text)
        {
            var l = new Label(text);
            l.AddToClassList("hg-muted");
            l.style.whiteSpace = WhiteSpace.Normal;
            return l;
        }

        private static TextField Field(string label, string value)
        {
            var f = new TextField(label) { value = value ?? "" };
            f.AddToClassList("hg-field");
            return f;
        }

        private Toggle Toggle(string label, bool value, Action<bool> set)
        {
            var t = new Toggle(label) { value = value };
            t.style.marginRight = 16;
            t.RegisterValueChangedCallback(e => { set(e.newValue); RefreshServers(); });
            return t;
        }
    }
}
