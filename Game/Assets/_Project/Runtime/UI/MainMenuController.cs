using System;
using System.Collections.Generic;
using System.IO;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.UIElements;

namespace HeroGame.Runtime.UI
{
    using HeroGame.Core.Characters;
    using HeroGame.Core.Population;
    using HeroGame.Core.Servers;
    using HeroGame.Persistence.Json;
    using HeroGame.Persistence.Saves;
    using HeroGame.Persistence.Storage;
    using HeroGame.Runtime.Bootstrap;

    /// <summary>
    /// Front end (GDD §6–7): Continue, Story Mode, Servers, Character, Options, Settings, Exit.
    /// Built on UI Toolkit; layout in Resources/UI/MainMenu.uxml, style in FrontEnd.uss (named apart from the UXML:
    /// Resources.Load cannot tell two assets with one name apart). The player keeps up to three characters
    /// (<see cref="CharacterSlots"/>), each with its own worlds: the first run opens the character creator as a
    /// popup, and every later run starts on CHOOSE YOUR CHARACTER.
    /// </summary>
    [RequireComponent(typeof(UIDocument))]
    public sealed class MainMenuController : MonoBehaviour
    {
        public string GameplayScene = "VerticalSlice_Greybox";
        public PanelSettings PanelSettings;

        private UIDocument _document;
        private VisualElement _panel;
        private VisualElement _root;
        private VisualElement _modal;
        private CharacterSlots _slots;
        private int _slot = 1;
        private int _confirmDelete;
        private AccountProfile _account => _slots?.Get(_slot);
        private readonly ServerFilter _filter = new ServerFilter();
        private List<ServerListing> _servers = new List<ServerListing>();

        private void OnEnable()
        {
            // Whatever the last scene did with the cursor (gameplay locks it), the menu needs the mouse.
            UiFocus.Reset();
            _document = GetComponent<UIDocument>();
            if (_document.panelSettings == null)
                _document.panelSettings = PanelSettings != null ? PanelSettings : ScriptableObject.CreateInstance<PanelSettings>();
            if (_document.visualTreeAsset == null) _document.visualTreeAsset = Resources.Load<VisualTreeAsset>("UI/MainMenu");
            var root = _root = _document.rootVisualElement;
            if (root == null) return;
            if (root.childCount == 0 && _document.visualTreeAsset != null) _document.visualTreeAsset.CloneTree(root);
            foreach (var sheet in new[] { "UI/Controls", "UI/FrontEnd" })
            {
                var style = Resources.Load<StyleSheet>(sheet);
                if (style != null && !root.styleSheets.Contains(style)) root.styleSheets.Add(style);
                else if (style == null) Debug.LogWarning("[UI] Missing style sheet Resources/" + sheet + ".uss");
            }

            _panel = root.Q<VisualElement>("panel");
            root.Q<Label>("title").text = GameInfo.WorkingTitle;
            root.Q<Label>("footer").text = "Build " + GameInfo.Version + " · Working title · Not for distribution";

            _slots = GameBootstrap.Slots;
            _slot = _slots.Active;
            Bind(root, "continue", () => Launch(SessionMode.LocalServer, "local-dev", "local-dev"));
            Bind(root, "story", ShowStory);
            Bind(root, "servers", ShowServers);
            Bind(root, "character", ShowCharacters);
            Bind(root, "options", ShowOptions);
            Bind(root, "settings", ShowSettings);
            Bind(root, "exit", Quit);

            RefreshContinue();
            ShowCharacters();
            // First run: make a character before anything else.
            if (!_slots.Any) ShowCreator(1, firstRun: true);
        }

        private void Update()
        {
            // Nothing in the front end may hide or trap the mouse.
            if (UnityEngine.Cursor.lockState != CursorLockMode.None) UnityEngine.Cursor.lockState = CursorLockMode.None;
            if (!UnityEngine.Cursor.visible) UnityEngine.Cursor.visible = true;
        }

        private void RefreshContinue()
        {
            var button = _root?.Q<Button>("continue");
            if (button == null) return;
            var account = _account;
            button.style.display = account != null ? DisplayStyle.Flex : DisplayStyle.None;
            if (account != null) button.text = "CONTINUE · " + account.Character.FullName.ToUpperInvariant();
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
            p.Add(Muted(_account != null ? "Playing as " + _account.Character.FullName + ". Each character has their own story." : "Create a character first."));
            p.Add(new Button(() => Launch(SessionMode.Story, "story", "slot-1")) { text = "PLAY STORY" });
        }

        private void ShowServers()
        {
            var p = BeginPanel("SERVERS");
            if (_account != null) p.Add(Muted("Playing as " + _account.Character.FullName + " (character " + _slot + "). Each of your characters is a different person on every server."));
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

        // ------------------------------------------------------------------ characters

        private void ShowCharacters()
        {
            var p = BeginPanel("CHOOSE YOUR CHARACTER");
            p.Add(Muted("Up to " + CharacterSlots.Count + " characters. Each one has their own worlds: their own story, their own homes, money and record on every server."));
            var row = new VisualElement();
            row.AddToClassList("hg-slots");
            for (var slot = 1; slot <= CharacterSlots.Count; slot++) row.Add(SlotCard(slot));
            p.Add(row);
        }

        private VisualElement SlotCard(int slot)
        {
            var card = new VisualElement();
            card.AddToClassList("hg-slot");
            var profile = _slots.Get(slot);
            var top = new VisualElement();
            var number = new Label("CHARACTER " + slot + (profile != null && slot == _slot ? "  ·  SELECTED" : ""));
            number.AddToClassList("hg-slot-number");
            top.Add(number);
            var actions = new VisualElement();
            if (profile == null)
            {
                card.AddToClassList("hg-slot--empty");
                var empty = new Label("Empty");
                empty.AddToClassList("hg-slot-name");
                top.Add(empty);
                top.Add(SlotMeta("A new life in Port Arden."));
                actions.Add(SlotButton("CREATE CHARACTER", () => ShowCreator(slot, firstRun: false), accent: true));
            }
            else
            {
                if (slot == _slot) card.AddToClassList("hg-slot--active");
                var c = profile.Character;
                var name = new Label(c.FullName);
                name.AddToClassList("hg-slot-name");
                top.Add(name);
                top.Add(SlotMeta("Age " + c.Age + " · " + c.Presentation + " · " + Mathf.RoundToInt(c.Appearance.HeightCm) + " cm"));
                if (_confirmDelete == slot)
                {
                    top.Add(SlotMeta("Delete " + c.FirstName + " and every world they live in? This cannot be undone."));
                    actions.Add(SlotButton("YES, DELETE", () => DeleteCharacter(slot), danger: true));
                    actions.Add(SlotButton("KEEP", () => { _confirmDelete = 0; ShowCharacters(); }));
                }
                else
                {
                    actions.Add(SlotButton("PLAY", () => { Select(slot); Launch(SessionMode.LocalServer, "local-dev", "local-dev"); }, accent: true));
                    actions.Add(SlotButton("STORY", () => { Select(slot); Launch(SessionMode.Story, "story", "slot-1"); }));
                    actions.Add(SlotButton("SERVERS", () => { Select(slot); ShowServers(); }));
                    actions.Add(SlotButton("DELETE", () => { _confirmDelete = slot; ShowCharacters(); }, quiet: true));
                }
                card.RegisterCallback<ClickEvent>(e =>
                {
                    if (e.target is Button || _slot == slot) return;
                    Select(slot);
                    ShowCharacters();
                });
            }
            card.Add(top);
            card.Add(actions);
            return card;
        }

        private void Select(int slot)
        {
            if (_slots.IsEmpty(slot)) return;
            _slot = slot;
            _slots.Active = slot;
            RefreshContinue();
        }

        private void DeleteCharacter(int slot)
        {
            _confirmDelete = 0;
            DeleteWorlds(slot);
            _slots.Delete(slot);
            _slot = _slots.Active;
            RefreshContinue();
            ShowCharacters();
            if (!_slots.Any) ShowCreator(1, firstRun: true);
        }

        /// <summary>A character's worlds go with them (a new character in the slot starts fresh).</summary>
        private static void DeleteWorlds(int slot)
        {
            foreach (var folder in CharacterSlots.WorldFolders(GameSession.SaveRoot, slot))
            {
                try { Directory.Delete(folder, true); }
                catch (IOException ex) { Debug.LogWarning("[Characters] Could not delete " + folder + ": " + ex.Message); }
                catch (UnauthorizedAccessException ex) { Debug.LogWarning("[Characters] Could not delete " + folder + ": " + ex.Message); }
            }
        }

        private static Label SlotMeta(string text)
        {
            var l = new Label(text);
            l.AddToClassList("hg-slot-meta");
            return l;
        }

        private static Button SlotButton(string text, Action onClick, bool accent = false, bool quiet = false, bool danger = false)
        {
            var b = new Button(onClick) { text = text };
            b.AddToClassList("hg-button");
            if (accent) b.AddToClassList("hg-button--accent");
            if (quiet) b.AddToClassList("hg-button--quiet");
            if (danger) b.AddToClassList("hg-button--danger");
            return b;
        }

        private void CloseModal()
        {
            _modal?.RemoveFromHierarchy();
            _modal = null;
        }

        /// <summary>The character creator, as a popup over the menu. On the first run it cannot be dismissed.</summary>
        private void ShowCreator(int slot, bool firstRun)
        {
            CloseModal();
            _modal = new VisualElement();
            _modal.AddToClassList("hg-modal");
            var card = new VisualElement();
            card.AddToClassList("hg-modal-card");
            _modal.Add(card);
            _root.Add(_modal);

            var title = new Label(firstRun ? "WELCOME TO PORT ARDEN" : "CREATE CHARACTER " + slot);
            title.AddToClassList("hg-panel-title");
            card.Add(title);
            card.Add(Muted(firstRun
                ? "Create your first character. You can keep up to " + CharacterSlots.Count + ", each with their own worlds, and play any of them from the main menu."
                : "A new person with their own worlds. Name and appearance are theirs on every server; money, home, job and powers are lived separately on each."));
            var p = new ScrollView();
            p.AddToClassList("hg-modal-body");
            card.Add(p);

            var profile = CharacterSlots.NewProfile(slot);
            var c = profile.Character;
            var first = Field("First name", c.FirstName);
            first.maxLength = IdentityRules.MaxNameLength;
            var last = Field("Surname", c.LastName);
            last.maxLength = IdentityRules.MaxNameLength;
            var age = new SliderInt("Age", IdentityRules.MinAge, IdentityRules.MaxAge) { value = Mathf.Clamp(c.Age, IdentityRules.MinAge, IdentityRules.MaxAge), showInputField = true };
            age.AddToClassList("hg-field");
            var presentation = new DropdownField("Presentation", new List<string> { "Feminine", "Masculine", "Androgynous" }, (int)c.Presentation);
            presentation.AddToClassList("hg-field");
            var height = new Slider("Height (cm)", IdentityRules.MinHeightCm, IdentityRules.MaxHeightCm) { value = c.Appearance.HeightCm, showInputField = true };
            height.AddToClassList("hg-field");
            var skin = new SliderInt("Skin tone", 0, IdentityRules.SkinTones - 1) { value = c.Appearance.SkinTone };
            skin.AddToClassList("hg-field");
            var build = new Slider("Build", 0f, 1f) { value = c.Appearance.BodyWeight };
            build.AddToClassList("hg-field");
            var jaw = new Slider("Jaw width", 0f, 1f) { value = c.Appearance.GetMorph("jaw_width") };
            jaw.AddToClassList("hg-field");
            var nose = new Slider("Nose length", 0f, 1f) { value = c.Appearance.GetMorph("nose_length") };
            nose.AddToClassList("hg-field");
            foreach (var e in new VisualElement[] { first, last, age, presentation, height, skin, build, jaw, nose }) p.Add(e);

            var status = Muted("");
            var buttons = new VisualElement();
            buttons.AddToClassList("hg-row");
            buttons.Add(SlotButton("CREATE " + (firstRun ? "AND CONTINUE" : "CHARACTER"), () =>
            {
                c.FirstName = first.value;
                c.LastName = last.value;
                if (!IdentityRules.IsValid(c))
                {
                    status.text = "Enter a first name and a surname (letters, spaces, hyphens and apostrophes).";
                    return;
                }
                c.Age = age.value;
                c.Presentation = (GenderPresentation)Mathf.Max(0, presentation.index);
                c.Appearance.HeightCm = height.value;
                c.Appearance.SkinTone = skin.value;
                c.Appearance.BodyWeight = build.value;
                c.Appearance.SetMorph("jaw_width", jaw.value);
                c.Appearance.SetMorph("nose_length", nose.value);
                // A new person starts in fresh worlds, never in whatever an earlier occupant of the slot left.
                DeleteWorlds(slot);
                _slots.Save(slot, profile);
                _slot = slot;
                CloseModal();
                RefreshContinue();
                ShowCharacters();
            }, accent: true));
            if (!firstRun) buttons.Add(SlotButton("CANCEL", CloseModal, quiet: true));
            card.Add(buttons);
            card.Add(status);
            first.Focus();
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
            var panel = BeginPanel("SETTINGS");
            var p = new ScrollView();
            p.style.flexGrow = 1;
            panel.Add(p);
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
            Check("Subtitles", () => s.Subtitles, v => s.Subtitles = v);
            Check("24-hour clock", () => s.Clock24h, v => s.Clock24h = v);
            Check("Metric units", () => s.Units == Core.Presentation.UnitSystem.Metric, v => s.Units = v ? Core.Presentation.UnitSystem.Metric : Core.Presentation.UnitSystem.Imperial);
            var display = new Label("DISPLAY");
            display.AddToClassList("hg-panel-title");
            display.style.fontSize = 20;
            display.style.marginTop = 18;
            p.Add(display);
            GraphicsSettingsPage.Display(p, "hg-field", ShowSettings);
            var graphics = new Label("GRAPHICS");
            graphics.AddToClassList("hg-panel-title");
            graphics.style.fontSize = 20;
            graphics.style.marginTop = 18;
            p.Add(graphics);
            GraphicsSettingsPage.Graphics(p, "hg-field", ShowSettings);
            var save = new Button(() => SettingsService.Commit()) { text = "SAVE" };
            save.AddToClassList("hg-menu-button");
            save.AddToClassList("hg-menu-button--accent");
            p.Add(save);
            var more = new Label("Controls and accessibility options are in the pause menu in game.");
            more.AddToClassList("hg-muted");
            p.Add(more);
        }

        private void Launch(SessionMode mode, string serverId, string world)
        {
            if (_account == null)
            {
                var empty = _slots.FirstEmpty;
                if (empty > 0) ShowCreator(empty, firstRun: !_slots.Any);
                else ShowCharacters();
                return;
            }
            _slots.Active = _slot;
            LaunchRequest.Set(mode, serverId, world, _slot);
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
