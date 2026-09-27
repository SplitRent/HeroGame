using System.Collections.Generic;
using HeroGame.Core.Building;
using HeroGame.Core.Business;
using HeroGame.Core.Crime;
using HeroGame.Core.Population;
using HeroGame.Core.Powers;
using HeroGame.Core.Social;
using HeroGame.Core.Vehicles;

namespace HeroGame.Core.World
{
    /// <summary>
    /// All data-driven content a world needs (TDD §12). Loaded from JSON by the persistence layer
    /// (<c>ContentLoader</c>) so servers and future mods can extend it without code changes.
    /// </summary>
    public sealed class ContentSet
    {
        public List<OccupationDefinition> Occupations = new List<OccupationDefinition>();
        public List<BusinessTemplate> BusinessTemplates = new List<BusinessTemplate>();
        public NameTables Names = NameTables.Fallback();
        public List<PowerArchetype> PowerArchetypes = new List<PowerArchetype>();
        public List<AnomalyCause> AnomalyCauses = new List<AnomalyCause>();
        public List<InteractionRule> InteractionRules = new List<InteractionRule>();
        public List<CrimeType> CrimeTypes = new List<CrimeType>();
        public WorldLayout Layout = new WorldLayout();
        public List<BarkLine> Barks = new List<BarkLine>();
        public List<VehicleModel> VehicleModels = new List<VehicleModel>();
        public List<VehicleMod> VehicleMods = new List<VehicleMod>();
        public List<FurnitureDefinition> Furniture = new List<FurnitureDefinition>();
        public List<BusinessRequirement> BusinessRequirements = new List<BusinessRequirement>();
        public List<ItemDefinition> Items = new List<ItemDefinition>();
        public List<Civic.OrdinanceDefinition> Ordinances = new List<Civic.OrdinanceDefinition>();
        public List<Civic.CalendarEvent> CalendarEvents = new List<Civic.CalendarEvent>();
        public List<Social.RippleTemplate> RippleTemplates = new List<Social.RippleTemplate>();
        public List<Audio.RadioStation> RadioStations = new List<Audio.RadioStation>();

        public ItemDefinition FindItem(string id)
        {
            foreach (var i in Items) if (i.Id == id) return i;
            return null;
        }

        public CrimeType FindCrime(string id)
        {
            foreach (var c in CrimeTypes) if (c.Id == id) return c;
            return null;
        }
    }
}
