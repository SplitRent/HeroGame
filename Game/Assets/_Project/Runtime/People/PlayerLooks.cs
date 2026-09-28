using UnityEngine;

namespace HeroGame.Runtime.People
{
    using HeroGame.Core.Characters;
    using HeroGame.Runtime.Bootstrap;

    /// <summary>
    /// Keeps the player's body looking like their character: the identity from the creator (face, body, skin, eyes,
    /// walk) and the outfit they are wearing, offline from the local world and online from the server's view.
    /// </summary>
    public sealed class PlayerLooks : MonoBehaviour
    {
        private HumanAvatar _avatar;
        private HumanGait _gait;
        private string _signature = "";
        private float _next;

        private void Awake()
        {
            _avatar = GetComponentInChildren<HumanAvatar>();
            _gait = GetComponentInChildren<HumanGait>();
        }

        private void Update()
        {
            if (_avatar == null || Time.unscaledTime < _next) return;
            _next = Time.unscaledTime + 1f;
            if (!ServiceRegistry.TryGet<GameSession>(out var session) || session.LocalCharacter == null) return;
            var me = session.LocalCharacter;
            var identity = me.Identity != null && !string.IsNullOrEmpty(me.Identity.FirstName) ? me.Identity
                : GameBootstrap.Instance != null && GameBootstrap.Instance.Account != null ? GameBootstrap.Instance.Account.Character : null;
            if (identity == null) return;
            Outfit worn;
            var online = Online.NetworkSession.Me;
            if (online != null) worn = online.Outfits.Find(o => o.Id == online.CurrentOutfit);
            else worn = session.World.Wardrobe.Worn(me);
            var signature = identity.FullName + "|" + identity.Age + "|" + (worn != null ? worn.Id + WardrobeRules.EncodePieces(worn) : "");
            if (signature == _signature) return;
            _signature = signature;
            _avatar.Apply(identity.Appearance, identity.Presentation, identity.Age);
            _avatar.Dress(worn, session.World.Content, identity.Presentation);
            if (_gait != null) _gait.Style = session.World.Content.Animations.Walk(string.IsNullOrEmpty(identity.WalkStyleId) ? "casual" : identity.WalkStyleId);
        }
    }
}
