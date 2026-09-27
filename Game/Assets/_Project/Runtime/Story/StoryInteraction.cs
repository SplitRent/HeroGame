using HeroGame.Runtime.Bootstrap;
using HeroGame.Runtime.Interaction;
using UnityEngine;

namespace HeroGame.Runtime.Story
{
    /// <summary>A world point for a tagged story objective ("sandbags"); using it reports the tag to the story.</summary>
    public sealed class StoryInteraction : Interactable
    {
        public string Tag = "";

        private void Awake()
        {
            var box = gameObject.AddComponent<BoxCollider>();
            box.isTrigger = true;
            box.size = new Vector3(2f, 2f, 2f);
        }

        public override InteractionCategory Categories => InteractionCategory.Usable;

        public override void Interact(InteractionContext context)
        {
            if (ServiceRegistry.TryGet<GameSession>(out var session) && session.Story != null) session.Story.Interact(Tag);
        }
    }
}
