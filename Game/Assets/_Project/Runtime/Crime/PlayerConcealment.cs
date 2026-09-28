using UnityEngine;

namespace HeroGame.Runtime.Crime
{
    using HeroGame.Core.Business;
    using HeroGame.Core.Characters;
    using HeroGame.Core.Crime;
    using HeroGame.Core.Property;
    using HeroGame.Core.Simulation;
    using HeroGame.Runtime.Bootstrap;
    using HeroGame.Runtime.Interaction;
    using HeroGame.Runtime.Presentation;
    using HeroGame.Runtime.UI;
    using HeroGame.Runtime.Vehicles;

    /// <summary>
    /// What the police and witnesses can see of the player right now: a worn ski mask hides the face, and the
    /// alias costume makes evidence point at the alias instead of the civilian identity (GDD §29).
    /// </summary>
    public static class PlayerConcealment
    {
        public static bool MaskOn;

        public static ConcealmentState For(ServerCharacter c)
        {
            var state = new ConcealmentState();
            if (c == null) return state;
            var hasMask = c.Inventory.Exists(s => s.ItemId == "ski_mask");
            if (MaskOn && hasMask) state.FaceConcealment = 0.95f;
            state.InAliasCostume = c.Alias != null && !string.IsNullOrEmpty(c.Alias.CostumeOutfitId) && c.CurrentOutfit == c.Alias.CostumeOutfitId;
            if (state.InAliasCostume) state.FaceConcealment = Mathf.Max(state.FaceConcealment, 0.8f);
            return state;
        }

        public static void Say(CrimeResult r) => SubtitleFeed.Say("", r.Message + (r.Reported ? " Someone called the police." : ""), 4f);
    }
}
