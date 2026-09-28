using System.Collections.Generic;
using System.Linq;
using UnityEngine;

namespace HeroGame.Runtime.Building
{
    using HeroGame.Core.Building;
    using HeroGame.Core.Property;
    using HeroGame.Runtime.Bootstrap;
    using HeroGame.Runtime.Player;
    using HeroGame.Runtime.Presentation;

    public enum BuildTool
    {
        Wall,
        Room,
        Door,
        Window,
        Archway,
        Furniture,
        Delete,
    }

    /// <summary>
    /// Build mode for one property (GDD §21–22). Stand inside a building you own and press B: the camera goes
    /// overhead, edits accumulate as <see cref="BuildOp"/>s, and every edit re-runs the core preview so the
    /// cost and any validation errors are shown live. Enter commits through <see cref="GameSession.Build"/>,
    /// which charges the owner and saves; Escape or B discards. The layout itself is only ever changed by
    /// the core, so the same ops can later be sent to an authoritative server unchanged.
    /// </summary>
    [RequireComponent(typeof(LayoutRenderer), typeof(BoxCollider))]
    public sealed class BuildModeController : MonoBehaviour
    {
        public PlaceMarker Place;
        public float CameraHeight = 28f;
        /// <summary>Solid exterior (greybox or art) hidden while editing so the plan is visible.</summary>
        public GameObject Exterior;
        /// <summary>Draw the committed layout outside build mode (off while the exterior is a solid greybox).</summary>
        public bool ShowLayoutWhenIdle;

        private LayoutRenderer _renderer;
        private IPlayerInputSource _input;
        private bool _playerInside;
        private bool _active;
        private PropertyRecord _property;
        private readonly List<BuildOp> _ops = new List<BuildOp>();
        private BuildPreview _preview;
        private BuildTool _tool = BuildTool.Wall;
        private RoomType _roomType = RoomType.Living;
        private int _furnitureIndex;
        private float _rotation;
        private int _floor;
        private Vector2? _dragStart;
        private string _status = "";
        private ThirdPersonCamera _playerCamera;
        private Vector3 _savedCameraPosition;
        private Quaternion _savedCameraRotation;
        private List<FurnitureDefinition> _palette = new List<FurnitureDefinition>();
        private Vector2 _paletteScroll;

        public bool Active => _active;

        private static readonly List<BuildModeController> Enabled = new List<BuildModeController>();

        /// <summary>True while any building is in build mode (a registry, not a scene search: no per-frame lookups).</summary>
        public static bool AnyActive
        {
            get
            {
                foreach (var b in Enabled) if (b._active) return true;
                return false;
            }
        }

        private void OnEnable() => Enabled.Add(this);
        private void OnDisable() => Enabled.Remove(this);

        private void Awake()
        {
            _renderer = GetComponent<LayoutRenderer>();
        }

        private void Reset() => GetComponent<BoxCollider>().isTrigger = true;

        private void Start()
        {
            if (ShowLayoutWhenIdle) RenderCommitted();
        }

        private void OnTriggerEnter(Collider other)
        {
            if (other.CompareTag("Player")) _playerInside = true;
        }

        private void OnTriggerExit(Collider other)
        {
            if (other.CompareTag("Player")) _playerInside = false;
        }

        private void Update()
        {
            if (_input == null) _input = PlayerInputRegistry.Create();
            var input = _input.Read();
            if (!_active)
            {
                if (ShowLayoutWhenIdle && _playerInside) RefreshFromServer();
                if (_playerInside && input.BuildModePressed) Enter();
                return;
            }
            if (input.BuildModePressed || input.PausePressed)
            {
                if (input.PausePressed) UI.UiFocus.ConsumeEscape();
                Exit();
                return;
            }
            if (input.NextToolPressed) _tool = (BuildTool)(((int)_tool + 1) % System.Enum.GetValues(typeof(BuildTool)).Length);
            if (input.RotatePressed) _rotation = (_rotation + 90f) % 360f;
            if (input.SecondaryPressed) Undo();
            if (input.ConfirmPressed) Commit();
            if (input.PrimaryPressed && PointerOnLot(input.Pointer, out var lot)) Click(lot);
        }

        private bool TryGetProperty(out GameSession session, out PropertyRecord property)
        {
            property = null;
            if (!ServiceRegistry.TryGet(out session) || Place == null) return false;
            var place = Place.Resolve(session.World);
            if (place == null || !place.Property.IsValid) return false;
            property = session.World.Properties.Get(place.Property);
            return property != null && property.Layout != null;
        }

        private void RenderCommitted()
        {
            if (!TryGetProperty(out var session, out var property)) return;
            // Online, the server's building wins: someone may have rebuilt it since this client generated its world.
            var replica = Online.NetworkSession.Replica;
            var layout = replica?.Layout(property.Id) ?? property.Layout;
            _renderedLayout = layout;
            _renderer.Render(layout, session.World.Construction.Validator);
        }

        private BuildingLayout _renderedLayout;

        /// <summary>Fetches a rebuilt layout from the server when needed and re-renders once it arrives.</summary>
        private void RefreshFromServer()
        {
            var replica = Online.NetworkSession.Replica;
            if (replica == null || !TryGetProperty(out _, out var property)) return;
            if (replica.NeedsLayout(property.Id)) Online.NetworkSession.Current?.FetchLayout(property.Id);
            var current = replica.Layout(property.Id);
            if (current != null && !ReferenceEquals(current, _renderedLayout)) RenderCommitted();
        }

        private void Enter()
        {
            if (!TryGetProperty(out var session, out var property)) return;
            if (session.LocalCharacter == null || !session.World.Ownership.IsOwnedBy(property.Id, session.LocalCharacter.CharacterId))
            {
                _status = "You can only build on property you own.";
                return;
            }
            _property = property;
            _ops.Clear();
            _floor = 0;
            _palette = session.World.Construction.Validator.Catalog
                .OrderBy(f => f.Category).ThenBy(f => f.DisplayName).ToList();
            _active = true;
            if (Exterior != null) Exterior.SetActive(false);
            _input.GameplayEnabled = false;
            Cursor.lockState = CursorLockMode.None;
            Cursor.visible = true;
            var cam = Camera.main;
            if (cam != null)
            {
                _playerCamera = cam.GetComponent<ThirdPersonCamera>();
                if (_playerCamera != null) _playerCamera.enabled = false;
                _savedCameraPosition = cam.transform.position;
                _savedCameraRotation = cam.transform.rotation;
                var centre = _renderer.ToWorld(property.Layout.LotWidth / 2f, property.Layout.LotDepth / 2f, 0);
                cam.transform.SetPositionAndRotation(centre + Vector3.up * CameraHeight - transform.forward * CameraHeight * 0.35f,
                    Quaternion.LookRotation(centre - (centre + Vector3.up * CameraHeight - transform.forward * CameraHeight * 0.35f), transform.forward));
            }
            Refresh();
        }

        private void Exit()
        {
            _active = false;
            _ops.Clear();
            _dragStart = null;
            _input.GameplayEnabled = true;
            Cursor.lockState = CursorLockMode.Locked;
            Cursor.visible = false;
            var cam = Camera.main;
            if (cam != null) cam.transform.SetPositionAndRotation(_savedCameraPosition, _savedCameraRotation);
            if (_playerCamera != null) _playerCamera.enabled = true;
            _renderer.VisibleFloors = int.MaxValue;
            if (Exterior != null) Exterior.SetActive(true);
            if (ShowLayoutWhenIdle) RenderCommitted();
            else _renderer.Clear();
        }

        private bool PointerOnLot(Vector2 pointer, out Vector2 lot)
        {
            lot = default;
            var cam = Camera.main;
            if (cam == null) return false;
            var ray = cam.ScreenPointToRay(pointer);
            var plane = new Plane(transform.up, _renderer.ToWorld(0f, 0f, _floor));
            if (!plane.Raycast(ray, out var distance)) return false;
            lot = _renderer.ToLot(ray.GetPoint(distance));
            return true;
        }

        private void Click(Vector2 lot)
        {
            var layout = _preview != null ? _preview.Result : _property.Layout;
            var validator = CurrentValidator();
            switch (_tool)
            {
                case BuildTool.Wall:
                case BuildTool.Room:
                    if (_dragStart == null)
                    {
                        _dragStart = lot;
                        return;
                    }
                    var s = _dragStart.Value;
                    _dragStart = null;
                    _ops.Add(_tool == BuildTool.Wall
                        ? BuildTools.WallFromDrag(_floor, s.x, s.y, lot.x, lot.y)
                        : BuildTools.RoomFromDrag(_floor, s.x, s.y, lot.x, lot.y, _roomType));
                    break;
                case BuildTool.Door:
                case BuildTool.Window:
                case BuildTool.Archway:
                    var wall = BuildTools.NearestWall(layout, _floor, lot.x, lot.y, 0.6f, out var offset);
                    if (wall == null) { _status = "Click on a wall."; return; }
                    var kind = _tool == BuildTool.Door ? OpeningKind.Door : _tool == BuildTool.Window ? OpeningKind.Window : OpeningKind.Archway;
                    _ops.Add(BuildTools.OpeningAt(wall, offset, kind, kind == OpeningKind.Archway ? 2f : kind == OpeningKind.Window ? 1.2f : 0.9f));
                    break;
                case BuildTool.Furniture:
                    if (_palette.Count == 0) return;
                    _ops.Add(new BuildOp { Kind = BuildOpKind.PlaceFurniture, Floor = _floor, CatalogId = _palette[_furnitureIndex].Id, X0 = lot.x, Z0 = lot.y, Rotation = _rotation });
                    break;
                case BuildTool.Delete:
                    var removal = BuildTools.RemovalAt(layout, validator, _floor, lot.x, lot.y);
                    if (removal == null) { _status = "Nothing to remove here."; return; }
                    _ops.Add(removal);
                    break;
            }
            Refresh();
        }

        private BuildingValidator CurrentValidator() => ServiceRegistry.TryGet<GameSession>(out var session) ? session.World.Construction.Validator : null;

        private void Undo()
        {
            if (_dragStart != null) { _dragStart = null; return; }
            if (_ops.Count == 0) return;
            _ops.RemoveAt(_ops.Count - 1);
            Refresh();
        }

        private void Refresh()
        {
            if (!ServiceRegistry.TryGet<GameSession>(out var session) || _property == null) return;
            var world = session.World;
            _preview = world.Construction.Preview(_property.Layout, _ops, _property, _property.Zoning != ZoningType.Residential, world.Macro.PriceLevel);
            _renderer.VisibleFloors = _floor + 1;
            _renderer.Render(_preview.Result, world.Construction.Validator, preview: _preview.Report.HasErrors);
            _status = _preview.Report.HasErrors ? FirstError(_preview) : _ops.Count == 0 ? "No changes." : "Ready to build.";
        }

        private void Commit()
        {
            if (_ops.Count == 0 || !ServiceRegistry.TryGet<GameSession>(out var session)) return;
            var result = session.Build(_property, _ops);
            if (!result.Success)
            {
                _status = result.Error;
                return;
            }
            _ops.Clear();
            _status = "Built.";
            Refresh();
        }

        private static string FirstError(BuildPreview preview)
        {
            foreach (var m in preview.Report.Messages)
                if (m.Severity == Core.Foundation.Severity.Error) return m.Path + ": " + m.Message;
            return "";
        }

        private void OnGUI()
        {
            if (!_active)
            {
                if (_playerInside) GUI.Label(new Rect(20, Screen.height - 60, 400, 24), string.IsNullOrEmpty(_status) ? "[B] Build mode" : _status);
                return;
            }
            GUILayout.BeginArea(new Rect(16, 16, 320, Screen.height - 32), GUI.skin.box);
            GUILayout.Label("BUILD — " + _property.Address);
            GUILayout.Label("Tool: " + _tool + "   (Tab to change, R rotate, right-click undo)");
            GUILayout.BeginHorizontal();
            if (GUILayout.Button("Floor -") && _floor > 0) { _floor--; Refresh(); }
            GUILayout.Label("Floor " + _floor);
            if (GUILayout.Button("Floor +") && _preview != null && _floor + 1 < _preview.Result.Floors) { _floor++; Refresh(); }
            if (GUILayout.Button("Add storey")) { _ops.Add(new BuildOp { Kind = BuildOpKind.AddFloor }); Refresh(); }
            GUILayout.EndHorizontal();
            if (_tool == BuildTool.Room)
            {
                GUILayout.Label("Room type: " + _roomType);
                if (GUILayout.Button("Next room type")) _roomType = (RoomType)(((int)_roomType + 1) % System.Enum.GetValues(typeof(RoomType)).Length);
            }
            if (_tool == BuildTool.Furniture && _palette.Count > 0)
            {
                _paletteScroll = GUILayout.BeginScrollView(_paletteScroll, GUILayout.Height(Mathf.Min(360, Screen.height * 0.45f)));
                for (var i = 0; i < _palette.Count; i++)
                {
                    var f = _palette[i];
                    var label = (i == _furnitureIndex ? "> " : "  ") + f.DisplayName + "  $" + (f.PriceCents / 100);
                    if (GUILayout.Button(label)) _furnitureIndex = i;
                }
                GUILayout.EndScrollView();
            }
            if (_preview != null)
            {
                GUILayout.Label("Edits: " + _ops.Count);
                GUILayout.Label("Cost: " + _preview.Cost + "   Salvage: " + _preview.Refund);
                GUILayout.Label("Net: " + _preview.Net);
                foreach (var m in _preview.Report.Messages)
                    if (m.Severity != Core.Foundation.Severity.Info) GUILayout.Label((m.Severity == Core.Foundation.Severity.Error ? "✖ " : "! ") + m.Message);
            }
            GUILayout.Label(_status);
            GUILayout.BeginHorizontal();
            GUI.enabled = _ops.Count > 0 && _preview != null && !_preview.Report.HasErrors;
            if (GUILayout.Button("Build (Enter)")) Commit();
            GUI.enabled = true;
            if (GUILayout.Button("Leave (B)")) Exit();
            GUILayout.EndHorizontal();
            GUILayout.EndArea();
        }
    }
}
