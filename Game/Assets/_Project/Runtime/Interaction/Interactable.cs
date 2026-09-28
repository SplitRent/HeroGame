using System;
using UnityEngine;

namespace HeroGame.Runtime.Interaction
{
    /// <summary>Every environmental object declares what can be done with it (GDD §112).</summary>
    [Flags]
    public enum InteractionCategory
    {
        None = 0,
        Pushable = 1 << 0,
        Breakable = 1 << 1,
        Usable = 1 << 2,
        Climbable = 1 << 3,
        Enterable = 1 << 4,
        Stealable = 1 << 5,
        Movable = 1 << 6,
        Destructible = 1 << 7,
        Decorative = 1 << 8,
        Purchasable = 1 << 9,
        Talkable = 1 << 10,
    }

    /// <summary>Who is interacting, handed to interactables so they can apply rules (ownership, law, relationships).</summary>
    public struct InteractionContext
    {
        public GameObject Actor;
        public Vector3 Point;
    }

    public interface IInteractable
    {
        InteractionCategory Categories { get; }
        /// <summary>Short verb phrase for the prompt, e.g. "Open door", "Buy for $180,000".</summary>
        string GetPrompt(InteractionContext context);
        bool CanInteract(InteractionContext context);
        void Interact(InteractionContext context);
        Transform transform { get; }
    }

    /// <summary>Convenience base for MonoBehaviour interactables.</summary>
    public abstract class Interactable : MonoBehaviour, IInteractable
    {
        [SerializeField] private InteractionCategory _categories = InteractionCategory.Usable;
        public string Prompt = "Use";
        public float MaxDistance = 2.5f;

        public virtual InteractionCategory Categories => _categories;
        public virtual string GetPrompt(InteractionContext context) => Prompt;

        public virtual bool CanInteract(InteractionContext context)
        {
            return context.Actor != null && (context.Actor.transform.position - transform.position).sqrMagnitude <= MaxDistance * MaxDistance * 4f;
        }

        public abstract void Interact(InteractionContext context);
    }
}
