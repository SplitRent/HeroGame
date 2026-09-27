using System;
using System.Collections.Generic;
using HeroGame.Core.Business;
using HeroGame.Core.Economy;
using HeroGame.Core.Foundation;
using HeroGame.Core.Property;
using HeroGame.Core.Time;

namespace HeroGame.Core.Building
{
    public enum BuildOpKind
    {
        AddWall,
        RemoveWall,
        AddOpening,
        RemoveOpening,
        AddRoom,
        RemoveRoom,
        PlaceFurniture,
        RemoveFurniture,
        MoveFurniture,
        AddFloor,
    }

    /// <summary>One edit in a build session. Plain data so edits can be sent over the network and validated server-side.</summary>
    [Serializable]
    public sealed class BuildOp
    {
        public BuildOpKind Kind;
        public int TargetId;
        public int Floor;
        public float X0, Z0, X1, Z1;
        public WallKind WallKind = WallKind.Interior;
        public OpeningKind OpeningKind = OpeningKind.Door;
        public float Offset;
        public float Width = 0.9f;
        public RoomType RoomType;
        public string Name = "";
        public List<float> Polygon = new List<float>();
        public string CatalogId = "";
        public float Rotation;
    }

    public sealed class BuildPreview
    {
        public BuildingLayout Result;
        public ValidationReport Report;
        public Money Cost;
        public Money Refund;
        public Money Net => Cost - Refund;
    }

    /// <summary>
    /// Sims-style construction (GDD §21–22): a build session applies edits to a copy of the property's layout,
    /// validates the result, prices it (materials + labour + furniture, minus resale of removed items), and
    /// commits layout and payment together. Change-of-use turns a building into a business once zoning,
    /// layout and equipment satisfy that business type.
    /// </summary>
    public sealed class ConstructionService
    {
        public const long WallCostPerMetreCents = 18000;      // framing, drywall, finish
        public const long ExteriorWallCostPerMetreCents = 42000;
        public const long OpeningCostCents = 65000;
        public const long FloorCostPerSqmCents = 90000;       // new storey structure
        public const double FurnitureResale = 0.5;

        private readonly BuildingValidator _validator;
        private readonly TransactionProcessor _processor;
        private readonly OwnershipRegistry _ownership;
        private readonly Dictionary<string, BusinessRequirement> _requirements = new Dictionary<string, BusinessRequirement>();

        public ConstructionService(BuildingValidator validator, TransactionProcessor processor, OwnershipRegistry ownership, IEnumerable<BusinessRequirement> requirements)
        {
            _validator = validator;
            _processor = processor;
            _ownership = ownership;
            foreach (var r in requirements) _requirements[r.TemplateId] = r;
        }

        public BuildingValidator Validator => _validator;
        public BusinessRequirement Requirement(string templateId) => templateId != null && _requirements.TryGetValue(templateId, out var r) ? r : null;

        public BuildPreview Preview(BuildingLayout current, IReadOnlyList<BuildOp> ops, PropertyRecord property, bool commercial, double priceLevel, string businessTemplate = null)
        {
            var layout = current.Clone();
            long cost = 0, refund = 0;
            var report = new ValidationReport();
            foreach (var op in ops) Apply(layout, op, report, ref cost, ref refund, priceLevel);
            var validation = _validator.Validate(layout, property, commercial, Requirement(businessTemplate));
            report.Messages.AddRange(validation.Messages);
            return new BuildPreview { Result = layout, Report = report, Cost = new Money(cost), Refund = new Money(refund) };
        }

        /// <summary>Validates, charges and applies a set of edits atomically. Payment goes to the contractor account.</summary>
        public OpResult Commit(PropertyRecord property, BuildingLayout current, IReadOnlyList<BuildOp> ops, EntityId owner, EntityId ownerAccount,
            EntityId contractorAccount, GameDateTime now, double priceLevel, string idempotencyKey, Action<BuildingLayout> store)
        {
            if (!_ownership.IsOwnedBy(property.Id, owner)) return OpResult.Fail("You can only build on property you own.");
            var commercial = property.Zoning != ZoningType.Residential;
            var preview = Preview(current, ops, property, commercial, priceLevel);
            if (preview.Report.HasErrors) return OpResult.Fail(FirstError(preview.Report));
            var net = preview.Net;
            if (net.Cents != 0)
            {
                var money = net.Cents > 0
                    ? LedgerTransaction.Transfer(ownerAccount, contractorAccount, net, TransactionReason.Purchase, "Construction at " + property.Address)
                    : LedgerTransaction.Transfer(contractorAccount, ownerAccount, -net, TransactionReason.Refund, "Salvage at " + property.Address);
                var result = _processor.Execute(new WorldTransaction { IdempotencyKey = idempotencyKey ?? "", Initiator = owner, Timestamp = now, Description = "Construction", Money = money });
                if (!result.Success) return result;
            }
            store(preview.Result);
            // Renovation raises value by roughly what was spent on structure (not furniture, which depreciates).
            property.Condition = Math.Min(1f, property.Condition + 0.05f);
            return OpResult.Ok();
        }

        /// <summary>
        /// Converts a property to a business use (GDD §21: warehouse → nightclub). Requires ownership, zoning, a
        /// layout that satisfies the template, and a permit fee. Returns the new business on success.
        /// </summary>
        /// <summary>City ordinances scale permit fees (e.g. fast-tracked port redevelopment).</summary>
        public float PermitFeeMultiplier = 1f;

        public long PermitFee(BusinessRequirement req) => (long)Math.Round(req.PermitFeeCents * (double)Math.Max(0f, PermitFeeMultiplier));

        public OpResult ChangeOfUse(PropertyRecord property, BuildingLayout layout, BusinessTemplate template, EntityId owner, EntityId ownerAccount,
            EntityId treasuryAccount, GameDateTime now, string idempotencyKey)
        {
            if (!_ownership.IsOwnedBy(property.Id, owner)) return OpResult.Fail("Only the owner can apply for a change of use.");
            var req = Requirement(template.Id);
            if (req == null) return OpResult.Fail("No permit rules for " + template.DisplayName + ".");
            var report = _validator.Validate(layout, property, true, req);
            if (report.HasErrors) return OpResult.Fail(FirstError(report));
            return _processor.Execute(new WorldTransaction
            {
                IdempotencyKey = idempotencyKey ?? "",
                Initiator = owner,
                Timestamp = now,
                Description = "Change-of-use permit: " + template.DisplayName,
                Money = LedgerTransaction.Transfer(ownerAccount, treasuryAccount, new Money(PermitFee(req)), TransactionReason.Fee, "Permit " + template.Id),
            });
        }

        private void Apply(BuildingLayout l, BuildOp op, ValidationReport r, ref long cost, ref long refund, double priceLevel)
        {
            switch (op.Kind)
            {
                case BuildOpKind.AddWall:
                    var wall = new Wall { Id = l.NextId++, Floor = op.Floor, X0 = op.X0, Z0 = op.Z0, X1 = op.X1, Z1 = op.Z1, Kind = op.WallKind };
                    l.Walls.Add(wall);
                    cost += (long)(wall.Length * (op.WallKind == WallKind.Interior ? WallCostPerMetreCents : ExteriorWallCostPerMetreCents) * priceLevel);
                    break;
                case BuildOpKind.RemoveWall:
                    var w = l.FindWall(op.TargetId);
                    if (w == null) { r.Error("op", "No wall " + op.TargetId); break; }
                    if (w.Kind == WallKind.LoadBearing) r.Warn("wall " + w.Id, "Removing a load-bearing wall — check spans.");
                    l.Walls.Remove(w);
                    l.Openings.RemoveAll(o => o.WallId == w.Id);
                    cost += (long)(w.Length * WallCostPerMetreCents * 0.3 * priceLevel); // demolition
                    break;
                case BuildOpKind.AddOpening:
                    l.Openings.Add(new Opening { Id = l.NextId++, WallId = op.TargetId, Kind = op.OpeningKind, Offset = op.Offset, Width = op.Width, Height = op.OpeningKind == OpeningKind.Window ? 1.4f : op.OpeningKind == OpeningKind.GarageDoor ? 2.6f : 2.1f });
                    cost += (long)(OpeningCostCents * (op.OpeningKind == OpeningKind.GarageDoor ? 3 : 1) * priceLevel);
                    break;
                case BuildOpKind.RemoveOpening:
                    if (l.Openings.RemoveAll(o => o.Id == op.TargetId) == 0) r.Error("op", "No opening " + op.TargetId);
                    break;
                case BuildOpKind.AddRoom:
                    l.Rooms.Add(new Room { Id = l.NextId++, Floor = op.Floor, Type = op.RoomType, Name = string.IsNullOrEmpty(op.Name) ? op.RoomType.ToString() : op.Name, Polygon = new List<float>(op.Polygon) });
                    break;
                case BuildOpKind.RemoveRoom:
                    if (l.Rooms.RemoveAll(x => x.Id == op.TargetId) == 0) r.Error("op", "No room " + op.TargetId);
                    break;
                case BuildOpKind.PlaceFurniture:
                    var def = _validator.Item(op.CatalogId);
                    if (def == null) { r.Error("op", "Unknown item " + op.CatalogId); break; }
                    l.Furniture.Add(new PlacedFurniture { Id = l.NextId++, CatalogId = op.CatalogId, Floor = op.Floor, X = op.X0, Z = op.Z0, Rotation = op.Rotation });
                    cost += (long)(def.PriceCents * priceLevel);
                    break;
                case BuildOpKind.RemoveFurniture:
                    var f = l.Furniture.Find(x => x.Id == op.TargetId);
                    if (f == null) { r.Error("op", "No furniture " + op.TargetId); break; }
                    var fdef = _validator.Item(f.CatalogId);
                    if (fdef != null) refund += (long)(fdef.PriceCents * FurnitureResale * priceLevel);
                    l.Furniture.Remove(f);
                    break;
                case BuildOpKind.MoveFurniture:
                    var m = l.Furniture.Find(x => x.Id == op.TargetId);
                    if (m == null) { r.Error("op", "No furniture " + op.TargetId); break; }
                    m.X = op.X0;
                    m.Z = op.Z0;
                    m.Rotation = op.Rotation;
                    m.Floor = op.Floor;
                    break;
                case BuildOpKind.AddFloor:
                    l.Floors++;
                    var footprint = 0f;
                    foreach (var room in l.Rooms) if (room.Floor == 0) footprint += room.Area;
                    cost += (long)(footprint * FloorCostPerSqmCents * priceLevel);
                    break;
            }
        }

        private static string FirstError(ValidationReport report)
        {
            foreach (var m in report.Messages) if (m.Severity == Severity.Error) return m.Path + ": " + m.Message;
            return "Invalid build.";
        }
    }
}
