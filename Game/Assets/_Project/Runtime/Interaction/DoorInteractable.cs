using HeroGame.Core.Business;
using HeroGame.Core.Foundation;
using HeroGame.Runtime.Bootstrap;
using HeroGame.Runtime.Presentation;
using UnityEngine;

namespace HeroGame.Runtime.Interaction
{
    /// <summary>Hinged door. Locked doors respect ownership: owners pass, others need to break in (crime hook).</summary>
    public sealed class DoorInteractable : Interactable
    {
        public Transform Hinge;
        public float OpenAngle = 95f;
        public float Speed = 240f;
        public bool Locked;
        public PlaceMarker OwnerPlace;

        private bool _open;
        private float _angle;

        public override InteractionCategory Categories => InteractionCategory.Usable | InteractionCategory.Enterable | InteractionCategory.Breakable;

        public override string GetPrompt(InteractionContext context)
        {
            if (Locked && !ActorOwnsPlace()) return "Locked";
            return _open ? "Close door" : "Open door";
        }

        public override bool CanInteract(InteractionContext context) => base.CanInteract(context) && (!Locked || ActorOwnsPlace());

        public override void Interact(InteractionContext context) => _open = !_open;

        private void Update()
        {
            var target = _open ? OpenAngle : 0f;
            _angle = Mathf.MoveTowards(_angle, target, Speed * Time.deltaTime);
            var hinge = Hinge != null ? Hinge : transform;
            hinge.localRotation = Quaternion.Euler(0f, _angle, 0f);
        }

        private bool ActorOwnsPlace()
        {
            if (OwnerPlace == null || !ServiceRegistry.TryGet<GameSession>(out var session) || session.LocalCharacter == null) return false;
            var place = OwnerPlace.Resolve(session.World);
            return place != null && place.Property.IsValid && session.World.Ownership.OwnerOf(place.Property) == session.LocalCharacter.CharacterId;
        }
    }
}
