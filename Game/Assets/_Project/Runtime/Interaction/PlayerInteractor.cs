using HeroGame.Runtime.Player;
using UnityEngine;

namespace HeroGame.Runtime.Interaction
{
    /// <summary>
    /// Finds the best interactable in front of the player (view ray first, then proximity cone) and
    /// triggers it on the interact input. Exposes the current prompt for the HUD.
    /// </summary>
    public sealed class PlayerInteractor : MonoBehaviour
    {
        public Transform ViewOrigin;
        public float Reach = 3f;
        public float ProximityRadius = 1.6f;
        public LayerMask Mask = ~0;

        public IInteractable Focus { get; private set; }
        public string PromptText { get; private set; } = "";
        public IPlayerInputSource Input { get; set; }

        private readonly Collider[] _overlap = new Collider[16];

        private void Start()
        {
            if (Input == null) Input = PlayerInputRegistry.Create();
            if (ViewOrigin == null && Camera.main != null) ViewOrigin = Camera.main.transform;
        }

        private void Update()
        {
            var ctx = new InteractionContext { Actor = gameObject };
            Focus = FindFocus(ref ctx);
            PromptText = Focus != null && Focus.CanInteract(ctx) ? Focus.GetPrompt(ctx) : "";
            if (Focus == null || Input == null || !Input.GameplayEnabled) return;
            // During a mugging, E means "hand it over" (StreetEncounterPresenter), not the door behind you.
            if (Crime.StreetEncounterPresenter.Active) return;
            if (Input.Read().InteractPressed && Focus.CanInteract(ctx)) Focus.Interact(ctx);
        }

        private IInteractable FindFocus(ref InteractionContext ctx)
        {
            if (ViewOrigin != null)
            {
                var ray = new Ray(ViewOrigin.position, ViewOrigin.forward);
                var cameraToPlayer = Vector3.Distance(ViewOrigin.position, transform.position);
                if (Physics.Raycast(ray, out var hit, cameraToPlayer + Reach, Mask, QueryTriggerInteraction.Collide))
                {
                    var candidate = hit.collider.GetComponentInParent<IInteractable>();
                    if (candidate != null && Vector3.Distance(hit.point, transform.position) <= Reach)
                    {
                        ctx.Point = hit.point;
                        return candidate;
                    }
                }
            }

            // Fallback: nearest interactable roughly in front of the character.
            var count = Physics.OverlapSphereNonAlloc(transform.position + transform.forward * 0.8f + Vector3.up, ProximityRadius, _overlap, Mask, QueryTriggerInteraction.Collide);
            IInteractable best = null;
            var bestScore = float.MaxValue;
            for (var i = 0; i < count; i++)
            {
                var candidate = _overlap[i].GetComponentInParent<IInteractable>();
                if (candidate == null) continue;
                var to = candidate.transform.position - transform.position;
                var score = to.sqrMagnitude * (1.5f - Vector3.Dot(transform.forward, to.normalized));
                if (score < bestScore)
                {
                    bestScore = score;
                    best = candidate;
                    ctx.Point = candidate.transform.position;
                }
            }
            return best;
        }
    }
}
