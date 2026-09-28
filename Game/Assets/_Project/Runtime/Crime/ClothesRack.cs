namespace HeroGame.Runtime.Crime
{
    using HeroGame.Runtime.Interaction;
    using HeroGame.Runtime.UI;

    /// <summary>
    /// The racks in any shop whose template stocks clothing (boutiques, sporting goods, hardware stores, pharmacies,
    /// corner stores): opens the store screen with that shop's range. Buying is a wardrobe purchase (a server request
    /// online).
    /// </summary>
    public sealed class ClothesRack : PlaceInteractable
    {
        public override InteractionCategory Categories => InteractionCategory.Purchasable;

        public override string GetPrompt(InteractionContext context)
        {
            if (!TryContext(out var session, out _, out var place)) return "";
            var shop = BusinessAt(session, place);
            return shop != null && session.World.Wardrobe.Stock(shop).Count > 0 ? "Browse clothes and accessories" : "";
        }

        public override bool CanInteract(InteractionContext context) => base.CanInteract(context) && !string.IsNullOrEmpty(GetPrompt(context));

        public override void Interact(InteractionContext context)
        {
            if (!TryContext(out var session, out _, out var place)) return;
            var shop = BusinessAt(session, place);
            if (shop != null) GameUi.OpenStore(shop.Id);
        }
    }
}
