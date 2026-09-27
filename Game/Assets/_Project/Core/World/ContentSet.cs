using System.Collections.Generic;
using HeroGame.Core.Business;
using HeroGame.Core.Crime;
using HeroGame.Core.Population;
using HeroGame.Core.Powers;

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

        public CrimeType FindCrime(string id)
        {
            foreach (var c in CrimeTypes) if (c.Id == id) return c;
            return null;
        }
    }
}
