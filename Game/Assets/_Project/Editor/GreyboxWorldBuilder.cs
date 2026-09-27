using System.Collections.Generic;
using System.IO;
using HeroGame.Core.Foundation;
using HeroGame.Core.World;
using HeroGame.Persistence.Content;
using HeroGame.Runtime.Bootstrap;
using HeroGame.Runtime.Crime;
using HeroGame.Runtime.Emergency;
using HeroGame.Runtime.Building;
using HeroGame.Runtime.DevTools;
using HeroGame.Runtime.Interaction;
using HeroGame.Runtime.Player;
using HeroGame.Runtime.Population;
using HeroGame.Runtime.Powers;
using HeroGame.Runtime.Presentation;
using HeroGame.Runtime.UI;
using HeroGame.Runtime.Vehicles;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.UIElements;

namespace HeroGame.Editor
{
    /// <summary>
    /// Builds the playable greybox vertical slice directly from the same layout data the simulation
    /// uses (StreamingAssets/Data/layout_vertical_slice.json): roads, parcels, landmark buildings,
    /// place markers, for-sale signs, the player rig and all runtime systems. Re-run after editing the
    /// layout; the scene is generated, never hand-edited. Greybox materials are tracked placeholders.
    /// </summary>
    public static class GreyboxWorldBuilder
    {
        public const string SceneFolder = "Assets/_Project/Scenes";
        public const string GreyboxScene = SceneFolder + "/VerticalSlice_Greybox.unity";
        public const string MenuScene = SceneFolder + "/MainMenu.unity";
        public const string GeneratedFolder = "Assets/_Project/Generated";
        private static NpcAvatar _npcPrefab;
        private static Transform _policeStation;
        private static ContentSet _content;

        public const string MetroScene = SceneFolder + "/PortArden_Metro_Greybox.unity";
        public const string MetroLayout = "layout_port_arden.json";

        [MenuItem("HeroGame/Build Greybox Vertical Slice", priority = 1)]
        public static void BuildGreybox() => Build(ContentLoader.DefaultLayout, GreyboxScene);

        /// <summary>The whole 19-district metro (~7,500 places, ~50k residents). Heavy: expect a long build and a large scene.</summary>
        [MenuItem("HeroGame/Build Greybox Full Metro (heavy)", priority = 2)]
        public static void BuildMetroGreybox() => Build(MetroLayout, MetroScene);

        private static void Build(string layoutFile, string scenePath)
        {
            var dataDir = Path.Combine(Application.streamingAssetsPath, "Data");
            var content = ContentLoader.Load(dataDir, layoutFile);
            _content = content;
            Directory.CreateDirectory(SceneFolder);
            Directory.CreateDirectory(GeneratedFolder);

            var scene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
            var materials = new MaterialLibrary();

            var root = new GameObject("World");
            BuildGround(root.transform, content.Layout, materials);
            BuildRoads(root.transform, content.Layout, materials);
            _npcPrefab = BuildNpcPrefab(materials);
            var places = BuildPlaces(root.transform, content.Layout, materials);

            var systems = new GameObject("Systems");
            var bootstrap = systems.AddComponent<GameBootstrap>();
            bootstrap.LayoutFile = layoutFile;
            if (layoutFile != ContentLoader.DefaultLayout) bootstrap.SaveSlot = bootstrap.ServerId = "local-metro"; // never reopen a slice save on the metro
            var spawn = new GameObject("PlayerSpawn").transform;
            spawn.position = new Vector3(-200f, 0.1f, 40f); // Tidewater Avenue, outside Lupe's Corner Market
            bootstrap.PlayerSpawn = spawn;

            var (player, camera) = BuildPlayer(spawn.position);
            var lights = BuildLighting();
            var dayNight = systems.AddComponent<DayNightCycle>();
            dayNight.Sun = lights.sun;
            dayNight.Moon = lights.moon;
            if (IsHdrp())
            {
                // HDRP uses physical light units (lux) with exposure handled by the volume stack.
                dayNight.MaxSunIntensity = 100000f;
                dayNight.MoonIntensity = 0.3f;
            }
            var weather = systems.AddComponent<WeatherPresenter>();
            weather.Rain = BuildRain(camera.transform);

            var populationGo = new GameObject("Population");
            var population = populationGo.AddComponent<NpcPopulationPresenter>();
            population.AvatarPrefab = _npcPrefab;
            population.Observer = player.transform;

            if (_policeStation != null)
            {
                // Holding cell inside the station greybox and the steps outside for release.
                var custody = player.AddComponent<CustodyPresenter>();
                custody.Cell = new GameObject("Holding Cell").transform;
                custody.Cell.SetParent(_policeStation, false);
                custody.Cell.localPosition = new Vector3(0f, 0.1f, 0f);
                custody.ReleasePoint = new GameObject("Station Steps").transform;
                custody.ReleasePoint.SetParent(_policeStation, false);
                custody.ReleasePoint.localPosition = new Vector3(0f, 0.1f, -14f);
            }

            var story = systems.AddComponent<Runtime.Story.StoryPresenter>();
            story.Player = player.transform;

            var hud = systems.AddComponent<PrototypeHud>();
            systems.AddComponent<PhonePanel>();
            systems.AddComponent<SettingsPanel>();
            systems.AddComponent<ToastPresenter>();
            systems.AddComponent<RadioPresenter>();
            systems.AddComponent<Runtime.WorldProps.DestructiblePresenter>();
            systems.AddComponent<Runtime.Audio.AudioDirector>();
            hud.Interactor = player.GetComponent<PlayerInteractor>();
            var console = systems.AddComponent<DevConsole>();
            console.Player = player.transform;
            var inspector = systems.AddComponent<WorldInspectorOverlay>();
            inspector.Player = player.transform;
            inspector.Population = population;

            var vehicles = new GameObject("Vehicles").AddComponent<VehiclePresenter>();
            vehicles.Observer = player.transform;
            vehicles.CarPrefab = BuildCarPrefab(materials);
            var emergency = new GameObject("Emergency Services").AddComponent<EmergencyPresenter>();
            emergency.Observer = player.transform;
            emergency.PoliceCar = BuildServicePrefab(materials, "Police", new Color(0.1f, 0.12f, 0.2f), new Vector3(2f, 1.5f, 4.8f), new Color(0.2f, 0.4f, 1f));
            emergency.FireEngine = BuildServicePrefab(materials, "Engine", new Color(0.7f, 0.08f, 0.06f), new Vector3(2.5f, 3f, 9f), new Color(1f, 0.2f, 0.1f));
            emergency.Ambulance = BuildServicePrefab(materials, "Ambulance", new Color(0.92f, 0.92f, 0.9f), new Vector3(2.3f, 2.6f, 6f), new Color(1f, 0.15f, 0.15f));
            var fires = emergency.gameObject.AddComponent<FirePresenter>();
            fires.Observer = player.transform;
            fires.FirePrefab = BuildFirePrefab();
            player.AddComponent<PlayerVitals>();
            var powers = player.AddComponent<PowerController>();
            player.AddComponent<Runtime.Combat.CombatController>();
            powers.ImpactVfx = BuildImpactPrefab();

            var traffic = new GameObject("Traffic").AddComponent<TrafficPresenter>();
            traffic.Observer = player.transform;
            traffic.CarPrefab = BuildTrafficCarPrefab(materials);

            var streamer = systems.AddComponent<WorldStreamer>();
            streamer.Focus = player.transform;

            EditorSceneManager.SaveScene(scene, scenePath);
            AddToBuildSettings(scenePath);
            Debug.Log("[Greybox] Built " + places + " places from layout '" + content.Layout.Id + "' → " + scenePath);
        }

        [MenuItem("HeroGame/Build Main Menu Scene", priority = 2)]
        public static void BuildMenu()
        {
            Directory.CreateDirectory(SceneFolder);
            Directory.CreateDirectory(GeneratedFolder);
            var scene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
            var cam = new GameObject("Main Camera", typeof(Camera)) { tag = "MainCamera" };
            cam.GetComponent<Camera>().clearFlags = CameraClearFlags.SolidColor;
            cam.GetComponent<Camera>().backgroundColor = new Color(0.03f, 0.05f, 0.08f);

            var panelPath = GeneratedFolder + "/FrontEndPanelSettings.asset";
            var panel = AssetDatabase.LoadAssetAtPath<PanelSettings>(panelPath);
            if (panel == null)
            {
                panel = ScriptableObject.CreateInstance<PanelSettings>();
                panel.scaleMode = PanelScaleMode.ScaleWithScreenSize;
                panel.referenceResolution = new Vector2Int(1920, 1080);
                AssetDatabase.CreateAsset(panel, panelPath);
            }
            var ui = new GameObject("FrontEnd");
            var doc = ui.AddComponent<UIDocument>();
            doc.panelSettings = panel;
            doc.visualTreeAsset = Resources.Load<VisualTreeAsset>("UI/MainMenu");
            var menu = ui.AddComponent<MainMenuController>();
            menu.PanelSettings = panel;

            EditorSceneManager.SaveScene(scene, MenuScene);
            AddToBuildSettings(MenuScene, first: true);
            Debug.Log("[Greybox] Built " + MenuScene);
        }

        private static void BuildGround(Transform parent, WorldLayout layout, MaterialLibrary m)
        {
            // Cover every district with a margin (a Unity plane is 10 m per unit of scale).
            float minX = -600f, maxX = 1000f, minZ = -900f, maxZ = 400f;
            foreach (var d in layout.Districts)
            {
                minX = Mathf.Min(minX, d.CenterX - d.Radius - 200f);
                maxX = Mathf.Max(maxX, d.CenterX + d.Radius + 200f);
                minZ = Mathf.Min(minZ, d.CenterZ - d.Radius - 200f);
                maxZ = Mathf.Max(maxZ, d.CenterZ + d.Radius + 200f);
            }
            var ground = GameObject.CreatePrimitive(PrimitiveType.Plane);
            ground.name = "Ground";
            ground.transform.SetParent(parent);
            ground.transform.position = new Vector3((minX + maxX) / 2f, 0f, (minZ + maxZ) / 2f);
            ground.transform.localScale = new Vector3((maxX - minX) / 10f, 1f, (maxZ - minZ) / 10f);
            ground.GetComponent<Renderer>().sharedMaterial = m.Get("ground", new Color(0.33f, 0.36f, 0.27f));
            GameObjectUtility.SetStaticEditorFlags(ground, StaticEditorFlags.BatchingStatic | StaticEditorFlags.NavigationStatic);

            var canal = GameObject.CreatePrimitive(PrimitiveType.Cube);
            canal.name = "Arden Ship Canal (water placeholder)";
            canal.transform.SetParent(parent);
            canal.transform.position = new Vector3(300f, -0.4f, -830f);
            canal.transform.localScale = new Vector3(1600f, 1f, 90f);
            canal.GetComponent<Renderer>().sharedMaterial = m.Get("water", new Color(0.12f, 0.22f, 0.26f));
        }

        private static void BuildRoads(Transform parent, WorldLayout layout, MaterialLibrary m)
        {
            var roads = new GameObject("Roads").transform;
            roads.SetParent(parent);
            foreach (var road in layout.Roads)
            {
                for (var i = 0; i + 3 < road.Points.Count; i += 2)
                {
                    var a = new Vector3(road.Points[i], 0.02f, road.Points[i + 1]);
                    var b = new Vector3(road.Points[i + 2], 0.02f, road.Points[i + 3]);
                    var seg = GameObject.CreatePrimitive(PrimitiveType.Cube);
                    seg.name = road.Name;
                    seg.transform.SetParent(roads);
                    seg.transform.position = (a + b) * 0.5f;
                    seg.transform.rotation = Quaternion.LookRotation(b - a);
                    seg.transform.localScale = new Vector3(road.Width, 0.04f, Vector3.Distance(a, b) + road.Width);
                    var color = road.Kind == "avenue" ? new Color(0.2f, 0.2f, 0.21f) : road.Kind == "bridge" ? new Color(0.35f, 0.35f, 0.36f) : new Color(0.25f, 0.25f, 0.26f);
                    seg.GetComponent<Renderer>().sharedMaterial = m.Get("road_" + road.Kind, color);
                    GameObjectUtility.SetStaticEditorFlags(seg, StaticEditorFlags.BatchingStatic | StaticEditorFlags.NavigationStatic);

                    // Sidewalks on both sides so pedestrians have somewhere to be.
                    foreach (var side in new[] { -1f, 1f })
                    {
                        var walk = GameObject.CreatePrimitive(PrimitiveType.Cube);
                        walk.name = road.Name + " sidewalk";
                        walk.transform.SetParent(seg.transform, false);
                        walk.transform.localPosition = new Vector3(side * (0.5f + 1.5f / road.Width), 1.5f, 0f);
                        walk.transform.localScale = new Vector3(3f / road.Width, 4f, 1f);
                        walk.GetComponent<Renderer>().sharedMaterial = m.Get("sidewalk", new Color(0.55f, 0.55f, 0.52f));
                    }
                }
            }
        }

        private static int BuildPlaces(Transform parent, WorldLayout layout, MaterialLibrary m)
        {
            // Expand exactly as the simulation does so names/positions match the persistent places.
            var geography = new Geography();
            var expanded = LayoutExpander.Expand(layout, 0, new IdAllocator(), geography);
            var buildings = new GameObject("Places").transform;
            buildings.SetParent(parent);
            foreach (var e in expanded)
            {
                var p = e.Place;
                var go = new GameObject(p.Name);
                go.transform.SetParent(buildings);
                go.transform.position = new Vector3(p.Position.X, 0f, p.Position.Z);
                go.transform.rotation = Quaternion.Euler(0f, e.RotationY, 0f);
                var marker = go.AddComponent<PlaceMarker>();
                marker.PlaceName = p.Name;
                marker.Kind = p.Kind;

                if (e.Height > 0.1f && p.Kind != PlaceKind.Vacant)
                {
                    var body = GameObject.CreatePrimitive(PrimitiveType.Cube);
                    body.name = "Greybox";
                    body.transform.SetParent(go.transform, false);
                    body.transform.localPosition = new Vector3(0f, e.Height * 0.5f, 0f);
                    body.transform.localScale = new Vector3(e.Width, e.Height, e.Depth);
                    body.GetComponent<Renderer>().sharedMaterial = m.Get("kind_" + p.Kind, ColorFor(p.Kind));
                    GameObjectUtility.SetStaticEditorFlags(body, StaticEditorFlags.BatchingStatic | StaticEditorFlags.NavigationStatic | StaticEditorFlags.OccluderStatic);
                }
                else
                {
                    var lot = GameObject.CreatePrimitive(PrimitiveType.Cube);
                    lot.name = "Lot";
                    lot.transform.SetParent(go.transform, false);
                    lot.transform.localPosition = new Vector3(0f, 0.03f, 0f);
                    lot.transform.localScale = new Vector3(e.Width, 0.06f, e.Depth);
                    lot.GetComponent<Renderer>().sharedMaterial = m.Get("lot_" + p.Kind, p.Kind == PlaceKind.Park ? new Color(0.25f, 0.45f, 0.2f) : new Color(0.42f, 0.38f, 0.3f));
                }

                // Front-facing interaction points: the entrance faces the nearest road (−Z by convention of the layout).
                var front = go.transform.position + go.transform.rotation * new Vector3(0f, 0f, -(e.Depth * 0.5f + 1.2f));
                if (e.Property != null || p.Kind == PlaceKind.Vacant || p.Kind == PlaceKind.Residence || p.Kind == PlaceKind.Warehouse)
                {
                    var sign = GameObject.CreatePrimitive(PrimitiveType.Cube);
                    sign.name = "Property Sign";
                    sign.transform.SetParent(go.transform);
                    sign.transform.position = front + go.transform.rotation * new Vector3(e.Width * 0.3f, 0.8f, 0f);
                    sign.transform.localScale = new Vector3(0.8f, 1.6f, 0.1f);
                    sign.GetComponent<Renderer>().sharedMaterial = m.Get("sign", new Color(0.9f, 0.75f, 0.2f));
                    var s = sign.AddComponent<PropertyForSaleSign>();
                    s.Place = marker;
                }
                if (e.Property != null)
                {
                    // Build mode: the layout's lot-local origin is the lot's min corner (footprint + 2 m setback).
                    var build = new GameObject("Build Zone");
                    build.transform.SetParent(go.transform, false);
                    build.transform.localPosition = new Vector3(-(e.Width * 0.5f + 2f), 0f, -(e.Depth * 0.5f + 2f));
                    var zone = build.AddComponent<BoxCollider>();
                    zone.isTrigger = true;
                    zone.center = new Vector3(e.Width * 0.5f + 2f, 2f, e.Depth * 0.5f + 2f);
                    zone.size = new Vector3(e.Width + 8f, 4f, e.Depth + 8f);
                    var renderer = build.AddComponent<LayoutRenderer>();
                    renderer.WallMaterial = m.Get("build_wall", new Color(0.86f, 0.84f, 0.8f));
                    renderer.ExteriorWallMaterial = m.Get("build_exterior", new Color(0.62f, 0.58f, 0.52f));
                    renderer.FloorMaterial = m.Get("build_floor", new Color(0.5f, 0.42f, 0.33f));
                    renderer.FurnitureMaterial = m.Get("build_item", new Color(0.3f, 0.45f, 0.6f));
                    renderer.PreviewMaterial = m.Get("build_invalid", new Color(0.85f, 0.25f, 0.2f));
                    var controller = build.AddComponent<BuildModeController>();
                    controller.Place = marker;
                    var body = go.transform.Find("Greybox");
                    if (body != null) controller.Exterior = body.gameObject;
                }
                if (e.Business != null || p.Kind == PlaceKind.School || p.Kind == PlaceKind.Hospital || p.Kind == PlaceKind.Church)
                {
                    // Greybox buildings are solid, so the "interior" is an open-front zone at the entrance where the
                    // people inside are shown. Replaced by real interior scenes in Phase 3 art (ASSET_TRACKER).
                    var interior = new GameObject("Interior (greybox stub)");
                    interior.transform.SetParent(go.transform);
                    interior.transform.position = front + go.transform.rotation * new Vector3(0f, 1f, -3f);
                    interior.transform.rotation = go.transform.rotation;
                    var box = interior.AddComponent<BoxCollider>();
                    box.isTrigger = true;
                    box.size = new Vector3(Mathf.Max(6f, e.Width), 3f, 10f);
                    var presenter = interior.AddComponent<InteriorPresenter>();
                    presenter.Place = marker;
                    presenter.AvatarPrefab = _npcPrefab;
                }
                if (e.Property != null && p.Kind != PlaceKind.PoliceStation)
                {
                    var door = new GameObject("Entry Point");
                    door.transform.SetParent(go.transform);
                    door.transform.position = front + go.transform.rotation * new Vector3(-e.Width * 0.3f, 1f, 0.6f);
                    door.AddComponent<BoxCollider>().isTrigger = true;
                    door.AddComponent<BreakInPoint>().Place = marker;
                }
                if (p.Kind == PlaceKind.PoliceStation)
                {
                    _policeStation = go.transform;
                    var desk = new GameObject("Front Desk");
                    desk.transform.SetParent(go.transform);
                    desk.transform.position = front + Vector3.up;
                    desk.AddComponent<BoxCollider>().isTrigger = true;
                    desk.AddComponent<PoliceDesk>().Place = marker;
                }
                if (p.Kind == PlaceKind.Government)
                {
                    var counter = new GameObject("City Hall Counter");
                    counter.transform.SetParent(go.transform);
                    counter.transform.position = front + Vector3.up;
                    counter.AddComponent<BoxCollider>().isTrigger = true;
                    counter.AddComponent<Runtime.Civic.CityHallDesk>().Place = marker;
                }
                if (p.Kind == PlaceKind.Dock || p.Kind == PlaceKind.Garage)
                {
                    // Placeholder underworld contacts (ASSET_TRACKER): a fence at the docks, a chop shop behind garages.
                    var contact = new GameObject(p.Kind == PlaceKind.Dock ? "Fence" : "Chop Shop");
                    contact.transform.SetParent(go.transform);
                    contact.transform.position = go.transform.position + go.transform.rotation * new Vector3(e.Width * 0.5f + 1.5f, 1f, e.Depth * 0.3f);
                    contact.AddComponent<BoxCollider>().isTrigger = true;
                    var f = contact.AddComponent<FenceContact>();
                    f.Place = marker;
                    f.ChopShop = p.Kind == PlaceKind.Garage;
                }
                if (e.Business != null)
                {
                    var shelf = new GameObject("Shelves");
                    shelf.transform.SetParent(go.transform);
                    shelf.transform.position = front + go.transform.rotation * new Vector3(2.5f, 1f, 0f);
                    shelf.AddComponent<BoxCollider>().isTrigger = true;
                    shelf.AddComponent<ShopShelf>().Place = marker;
                    var register = new GameObject("Register");
                    register.transform.SetParent(go.transform);
                    register.transform.position = front + go.transform.rotation * new Vector3(-1.5f, 1f, 0f);
                    register.AddComponent<BoxCollider>().isTrigger = true;
                    register.AddComponent<RegisterRobbery>().Place = marker;
                }
                if (e.Business != null)
                {
                    var counter = new GameObject("Counter");
                    counter.transform.SetParent(go.transform);
                    counter.transform.position = front + Vector3.up;
                    var col = counter.AddComponent<BoxCollider>();
                    col.isTrigger = true;
                    col.size = new Vector3(2f, 2f, 1f);
                    var c = counter.AddComponent<BusinessCounter>();
                    c.Place = marker;
                    if (_content != null && _content.Weapons.Exists(wpn => wpn.SoldBy.Contains(e.Business.TemplateId)))
                    {
                        var weapons = new GameObject("Weapons Counter");
                        weapons.transform.SetParent(go.transform);
                        weapons.transform.position = front + go.transform.rotation * new Vector3(0f, 1f, 2.5f);
                        weapons.AddComponent<BoxCollider>().isTrigger = true;
                        weapons.AddComponent<Runtime.Combat.WeaponCounter>().Place = marker;
                    }
                }
            }
            return expanded.Count;
        }

        private static (GameObject player, Camera camera) BuildPlayer(Vector3 spawn)
        {
            var player = new GameObject("Player");
            player.transform.position = spawn;
            var cc = player.AddComponent<CharacterController>();
            cc.height = 1.8f;
            cc.radius = 0.32f;
            cc.center = new Vector3(0f, 0.9f, 0f);
            cc.stepOffset = 0.35f;
            cc.slopeLimit = 50f;
            var body = GameObject.CreatePrimitive(PrimitiveType.Capsule);
            body.name = "Body (placeholder)";
            Object.DestroyImmediate(body.GetComponent<Collider>());
            body.transform.SetParent(player.transform, false);
            body.transform.localPosition = new Vector3(0f, 0.9f, 0f);
            body.transform.localScale = new Vector3(0.64f, 0.9f, 0.64f);

            player.tag = "Player";
            var motor = player.AddComponent<PlayerMotor>();
            player.AddComponent<PlayerCharacterSync>();
            player.AddComponent<PlayerAnimatorBridge>();
            var interactor = player.AddComponent<PlayerInteractor>();

            var camGo = new GameObject("Main Camera", typeof(Camera), typeof(AudioListener)) { tag = "MainCamera" };
            var cam = camGo.GetComponent<Camera>();
            cam.nearClipPlane = 0.1f;
            cam.farClipPlane = 3000f;
            camGo.transform.position = spawn + new Vector3(0f, 2.5f, -4f);
            var orbit = camGo.AddComponent<ThirdPersonCamera>();
            orbit.Target = player.transform;
            orbit.CollisionMask = ~0;
            motor.CameraTransform = camGo.transform;
            interactor.ViewOrigin = camGo.transform;
            return (player, cam);
        }

        private static (Light sun, Light moon) BuildLighting()
        {
            var sunGo = new GameObject("Sun", typeof(Light));
            var sun = sunGo.GetComponent<Light>();
            sun.type = LightType.Directional;
            sun.shadows = LightShadows.Soft;
            var moonGo = new GameObject("Moon", typeof(Light));
            var moon = moonGo.GetComponent<Light>();
            moon.type = LightType.Directional;
            moon.color = new Color(0.6f, 0.7f, 1f);
            moon.shadows = LightShadows.None;
            RenderSettings.ambientMode = UnityEngine.Rendering.AmbientMode.Trilight;
            RenderSettings.ambientSkyColor = new Color(0.45f, 0.5f, 0.58f);
            RenderSettings.ambientEquatorColor = new Color(0.35f, 0.36f, 0.34f);
            RenderSettings.ambientGroundColor = new Color(0.2f, 0.19f, 0.16f);
            return (sun, moon);
        }

        private static ParticleSystem BuildRain(Transform follow)
        {
            var go = new GameObject("Rain");
            go.transform.SetParent(follow, false);
            go.transform.localPosition = new Vector3(0f, 12f, 6f);
            var ps = go.AddComponent<ParticleSystem>();
            var main = ps.main;
            main.startSpeed = 18f;
            main.startLifetime = 1.2f;
            main.startSize = 0.03f;
            main.maxParticles = 12000;
            main.simulationSpace = ParticleSystemSimulationSpace.World;
            var shape = ps.shape;
            shape.shapeType = ParticleSystemShapeType.Box;
            shape.scale = new Vector3(40f, 1f, 40f);
            shape.rotation = new Vector3(90f, 0f, 0f);
            var emission = ps.emission;
            emission.rateOverTime = 0f;
            var renderer = go.GetComponent<ParticleSystemRenderer>();
            renderer.renderMode = ParticleSystemRenderMode.Stretch;
            renderer.lengthScale = 6f;
            ps.Stop();
            return ps;
        }

        private static NpcAvatar BuildNpcPrefab(MaterialLibrary m)
        {
            var go = GameObject.CreatePrimitive(PrimitiveType.Capsule);
            go.name = "NPC Avatar (placeholder)";
            go.transform.localScale = new Vector3(0.6f, 0.9f, 0.6f);
            go.GetComponent<Renderer>().sharedMaterial = m.Get("npc", new Color(0.7f, 0.6f, 0.5f));
            var avatar = go.AddComponent<NpcAvatar>();
            go.AddComponent<NpcTalkInteractable>();
            var path = GeneratedFolder + "/NpcAvatar_Placeholder.prefab";
            var prefab = PrefabUtility.SaveAsPrefabAsset(go, path);
            Object.DestroyImmediate(go);
            return prefab.GetComponent<NpcAvatar>();
        }

        /// <summary>Drivable placeholder car: rigidbody, 4 WheelColliders with visual wheels, enter/exit.</summary>
        private static VehicleController BuildCarPrefab(MaterialLibrary m)
        {
            var root = new GameObject("Car (placeholder)");
            var rb = root.AddComponent<Rigidbody>();
            rb.mass = 1400f;
            rb.interpolation = RigidbodyInterpolation.Interpolate;
            rb.collisionDetectionMode = CollisionDetectionMode.ContinuousDynamic;
            var body = GameObject.CreatePrimitive(PrimitiveType.Cube);
            body.name = "Body";
            body.transform.SetParent(root.transform, false);
            body.transform.localPosition = new Vector3(0f, 0.55f, 0f);
            body.transform.localScale = new Vector3(1.8f, 0.7f, 4.4f);
            body.GetComponent<Renderer>().sharedMaterial = m.Get("car_paint", new Color(0.5f, 0.5f, 0.52f));
            var cabin = GameObject.CreatePrimitive(PrimitiveType.Cube);
            cabin.name = "Cabin";
            cabin.transform.SetParent(root.transform, false);
            cabin.transform.localPosition = new Vector3(0f, 1.1f, -0.2f);
            cabin.transform.localScale = new Vector3(1.6f, 0.55f, 2.2f);
            cabin.GetComponent<Renderer>().sharedMaterial = m.Get("car_glass", new Color(0.1f, 0.12f, 0.15f));
            Object.DestroyImmediate(cabin.GetComponent<Collider>());

            var controller = root.AddComponent<VehicleController>();
            WheelCollider Wheel(string name, float x, float z, out Transform visual)
            {
                var go = new GameObject(name);
                go.transform.SetParent(root.transform, false);
                go.transform.localPosition = new Vector3(x, 0.35f, z);
                var wc = go.AddComponent<WheelCollider>();
                wc.radius = 0.34f;
                wc.suspensionDistance = 0.2f;
                wc.mass = 20f;
                var spring = wc.suspensionSpring;
                spring.spring = 35000f;
                spring.damper = 4500f;
                spring.targetPosition = 0.5f;
                wc.suspensionSpring = spring;
                var v = GameObject.CreatePrimitive(PrimitiveType.Cylinder);
                v.name = name + " Visual";
                Object.DestroyImmediate(v.GetComponent<Collider>());
                v.transform.SetParent(root.transform, false);
                v.transform.localScale = new Vector3(0.68f, 0.12f, 0.68f);
                v.transform.localRotation = Quaternion.Euler(0f, 0f, 90f);
                v.GetComponent<Renderer>().sharedMaterial = m.Get("tire", new Color(0.05f, 0.05f, 0.05f));
                visual = v.transform;
                return wc;
            }
            controller.FrontLeft = Wheel("FL", -0.8f, 1.4f, out controller.FrontLeftVisual);
            controller.FrontRight = Wheel("FR", 0.8f, 1.4f, out controller.FrontRightVisual);
            controller.RearLeft = Wheel("RL", -0.8f, -1.35f, out controller.RearLeftVisual);
            controller.RearRight = Wheel("RR", 0.8f, -1.35f, out controller.RearRightVisual);
            var exit = new GameObject("DriverExit").transform;
            exit.SetParent(root.transform, false);
            exit.localPosition = new Vector3(-2f, 0.2f, 0.2f);
            var entry = root.AddComponent<VehicleEntry>();
            entry.DriverExit = exit;

            var prefab = PrefabUtility.SaveAsPrefabAsset(root, GeneratedFolder + "/Car_Placeholder.prefab");
            Object.DestroyImmediate(root);
            return prefab.GetComponent<VehicleController>();
        }

        /// <summary>Ambient traffic body: kinematic, collidable, no wheel physics (moved by TrafficPresenter).</summary>
        /// <summary>Greybox emergency vehicle: a tinted box with a light bar (ASSET_TRACKER placeholder).</summary>
        private static GameObject BuildServicePrefab(MaterialLibrary m, string name, Color body, Vector3 size, Color light)
        {
            var root = new GameObject(name + " (greybox)");
            var box = root.transform;
            var shell = GameObject.CreatePrimitive(PrimitiveType.Cube);
            shell.name = "Body";
            shell.transform.SetParent(box, false);
            shell.transform.localPosition = new Vector3(0f, size.y * 0.5f, 0f);
            shell.transform.localScale = size;
            shell.GetComponent<Renderer>().sharedMaterial = m.Get("service_" + name, body);
            var bar = new GameObject("Light Bar").AddComponent<Light>();
            bar.transform.SetParent(box, false);
            bar.transform.localPosition = new Vector3(0f, size.y + 0.3f, 0f);
            bar.type = LightType.Point;
            bar.color = light;
            bar.range = 18f;
            bar.intensity = IsHdrp() ? 4000f : 4f;
            var path = GeneratedFolder + "/" + name + "_Greybox.prefab";
            var prefab = PrefabUtility.SaveAsPrefabAsset(root, path);
            Object.DestroyImmediate(root);
            return prefab;
        }

        private static ParticleSystem BuildImpactPrefab()
        {
            var go = new GameObject("Power Impact (placeholder)");
            var ps = go.AddComponent<ParticleSystem>();
            var main = ps.main;
            main.duration = 0.4f;
            main.loop = false;
            main.startLifetime = 0.6f;
            main.startSpeed = 8f;
            main.startSize = 0.35f;
            main.maxParticles = 200;
            var emission = ps.emission;
            emission.rateOverTime = 0f;
            emission.SetBursts(new[] { new ParticleSystem.Burst(0f, 120) });
            var shape = ps.shape;
            shape.shapeType = ParticleSystemShapeType.Sphere;
            shape.radius = 0.3f;
            var path = GeneratedFolder + "/PowerImpact_Placeholder.prefab";
            var prefab = PrefabUtility.SaveAsPrefabAsset(go, path);
            Object.DestroyImmediate(go);
            return prefab.GetComponent<ParticleSystem>();
        }

        private static ParticleSystem BuildFirePrefab()
        {
            var go = new GameObject("Fire (placeholder)");
            var ps = go.AddComponent<ParticleSystem>();
            var main = ps.main;
            main.startLifetime = 1.6f;
            main.startSpeed = 3.5f;
            main.startSize = 1.8f;
            main.startColor = new Color(1f, 0.45f, 0.1f, 0.8f);
            main.maxParticles = 600;
            var shape = ps.shape;
            shape.shapeType = ParticleSystemShapeType.Box;
            shape.scale = new Vector3(6f, 1f, 6f);
            var path = GeneratedFolder + "/Fire_Placeholder.prefab";
            var prefab = PrefabUtility.SaveAsPrefabAsset(go, path);
            Object.DestroyImmediate(go);
            return prefab.GetComponent<ParticleSystem>();
        }

        private static GameObject BuildTrafficCarPrefab(MaterialLibrary m)
        {
            var root = GameObject.CreatePrimitive(PrimitiveType.Cube);
            root.name = "Traffic Car (placeholder)";
            root.transform.localScale = new Vector3(1.8f, 1.2f, 4.3f);
            root.GetComponent<Renderer>().sharedMaterial = m.Get("traffic_paint", new Color(0.35f, 0.38f, 0.45f));
            var rb = root.AddComponent<Rigidbody>();
            rb.isKinematic = true;
            var prefab = PrefabUtility.SaveAsPrefabAsset(root, GeneratedFolder + "/TrafficCar_Placeholder.prefab");
            Object.DestroyImmediate(root);
            return prefab;
        }

        private static Color ColorFor(PlaceKind kind)
        {
            switch (kind)
            {
                case PlaceKind.Residence: return new Color(0.72f, 0.66f, 0.56f);
                case PlaceKind.ApartmentBuilding: return new Color(0.62f, 0.5f, 0.42f);
                case PlaceKind.Shop: return new Color(0.35f, 0.55f, 0.7f);
                case PlaceKind.Restaurant: return new Color(0.75f, 0.45f, 0.3f);
                case PlaceKind.Nightlife: return new Color(0.45f, 0.3f, 0.6f);
                case PlaceKind.Office: return new Color(0.55f, 0.6f, 0.65f);
                case PlaceKind.Warehouse: return new Color(0.5f, 0.5f, 0.48f);
                case PlaceKind.Factory: return new Color(0.45f, 0.42f, 0.38f);
                case PlaceKind.School: return new Color(0.7f, 0.5f, 0.35f);
                case PlaceKind.Hospital: return new Color(0.9f, 0.9f, 0.92f);
                case PlaceKind.PoliceStation: return new Color(0.2f, 0.3f, 0.55f);
                case PlaceKind.FireStation: return new Color(0.7f, 0.2f, 0.2f);
                case PlaceKind.Church: return new Color(0.85f, 0.82f, 0.75f);
                case PlaceKind.Government: return new Color(0.6f, 0.62f, 0.55f);
                case PlaceKind.GasStation: return new Color(0.8f, 0.8f, 0.3f);
                case PlaceKind.Garage: return new Color(0.4f, 0.45f, 0.5f);
                case PlaceKind.Gym: return new Color(0.3f, 0.6f, 0.45f);
                case PlaceKind.Dock: return new Color(0.4f, 0.35f, 0.3f);
                default: return new Color(0.6f, 0.6f, 0.6f);
            }
        }

        private static bool IsHdrp()
        {
            var pipeline = UnityEngine.Rendering.GraphicsSettings.currentRenderPipeline;
            return pipeline != null && pipeline.GetType().Name.Contains("HDRenderPipeline");
        }

        private static void AddToBuildSettings(string path, bool first = false)
        {
            var scenes = new List<EditorBuildSettingsScene>(EditorBuildSettings.scenes);
            scenes.RemoveAll(s => s.path == path);
            var entry = new EditorBuildSettingsScene(path, true);
            if (first) scenes.Insert(0, entry);
            else scenes.Add(entry);
            EditorBuildSettings.scenes = scenes.ToArray();
        }

        /// <summary>Creates/reuses flat-colour placeholder materials for whichever render pipeline is active.</summary>
        private sealed class MaterialLibrary
        {
            private readonly Dictionary<string, Material> _cache = new Dictionary<string, Material>();

            public Material Get(string key, Color color)
            {
                if (_cache.TryGetValue(key, out var m)) return m;
                var path = GeneratedFolder + "/M_Greybox_" + key + ".mat";
                m = AssetDatabase.LoadAssetAtPath<Material>(path);
                if (m == null)
                {
                    var shader = Shader.Find("HDRP/Lit") ?? Shader.Find("Universal Render Pipeline/Lit") ?? Shader.Find("Standard");
                    m = new Material(shader);
                    AssetDatabase.CreateAsset(m, path);
                }
                if (m.HasProperty("_BaseColor")) m.SetColor("_BaseColor", color);
                if (m.HasProperty("_Color")) m.SetColor("_Color", color);
                EditorUtility.SetDirty(m);
                _cache[key] = m;
                return m;
            }
        }
    }
}
