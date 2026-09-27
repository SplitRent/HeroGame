using System.Collections.Generic;
using HeroGame.Core.Foundation;
using HeroGame.Runtime.Crime;
using HeroGame.Runtime.Interaction;
using HeroGame.Runtime.Online;
using HeroGame.Runtime.UI;
using UnityEngine;

namespace HeroGame.Runtime.Combat
{
    /// <summary>
    /// The weapons counter in shops whose template sells them (hardware, pharmacy, sporting goods). Lists what this shop
    /// stocks with prices; firearms ask for the City Hall permit. Online the purchase is a server request.
    /// Placeholder IMGUI (ASSET_TRACKER).
    /// </summary>
    public sealed class WeaponCounter : PlaceInteractable
    {
        private bool _open;
        private string _status = "";

        public override InteractionCategory Categories => InteractionCategory.Purchasable;

        public override string GetPrompt(InteractionContext context)
        {
            if (!TryContext(out var session, out _, out var place)) return "";
            var shop = BusinessAt(session, place);
            return shop != null && Stock(session, shop.TemplateId).Count > 0 ? "Browse the weapons counter" : "";
        }

        public override bool CanInteract(InteractionContext context) => base.CanInteract(context) && !string.IsNullOrEmpty(GetPrompt(context));

        public override void Interact(InteractionContext context)
        {
            _open = !_open;
            if (_open) UiFocus.Acquire();
            else UiFocus.Release();
        }

        private static List<Core.Combat.WeaponDefinition> Stock(Bootstrap.GameSession session, string template)
        {
            var list = new List<Core.Combat.WeaponDefinition>();
            foreach (var w in session.World.Content.Weapons) if (w.SoldBy.Contains(template)) list.Add(w);
            return list;
        }

        private void OnGUI()
        {
            if (!_open || !TryContext(out var session, out var me, out var place)) return;
            var shop = BusinessAt(session, place);
            if (shop == null) return;
            var w = session.World;
            GUILayout.BeginArea(new Rect(40, 60, 460, Screen.height - 120), GUI.skin.box);
            GUILayout.Label(shop.Name + " · weapons counter");
            GUILayout.Label("Cash " + w.Ledger.BalanceOf(me.CheckingAccount) + (w.Combat.HasLicense(me, Core.Simulation.CombatService.FirearmPermit) ? " · firearm permit held" : ""));
            foreach (var weapon in Stock(session, shop.TemplateId))
            {
                GUILayout.BeginVertical(GUI.skin.box);
                var price = new Money((long)(weapon.PriceCents * w.Macro.PriceLevel));
                GUILayout.Label(weapon.DisplayName + " — " + price + (weapon.Lethal ? "" : " · non-lethal"));
                GUILayout.Label(weapon.Description);
                GUILayout.BeginHorizontal();
                if (GUILayout.Button("Buy", GUILayout.Width(80))) Buy(session, shop, "weapons.buy", weapon.Id, () => w.Combat.BuyWeapon(me, shop, weapon.Id, session.NextRequestKey("weapon")));
                if (weapon.UsesAmmo && GUILayout.Button("Box of " + weapon.AmmoPackSize + " (" + new Money((long)(weapon.AmmoPackPriceCents * w.Macro.PriceLevel)) + ")"))
                    Buy(session, shop, "weapons.ammo", weapon.Id, () => w.Combat.BuyAmmo(me, shop, weapon.Id, 1, session.NextRequestKey("ammo")));
                GUILayout.EndHorizontal();
                GUILayout.EndVertical();
            }
            GUILayout.Label(_status);
            if (GUILayout.Button("Done")) Interact(default);
            GUILayout.EndArea();
        }

        private void Buy(Bootstrap.GameSession session, Core.Business.BusinessRecord shop, string op, string weaponId, System.Func<OpResult> offline)
        {
            if (NetworkSession.Replica != null && NetworkSession.Current != null)
            {
                _status = "…";
                _ = NetworkSession.Current.Request(op, new Dictionary<string, string> { ["business"] = shop.Id.ToString(), ["weapon"] = weaponId, ["packs"] = "1" })
                    .ContinueWith(t => _status = t.Result.Success ? "Bought." : t.Result.Error, System.Threading.Tasks.TaskScheduler.FromCurrentSynchronizationContext());
                return;
            }
            var r = offline();
            _status = r.Success ? "Bought." : r.Error;
            if (r.Success) session.Save();
        }
    }
}
