using UnityEngine;

namespace HeroGame.Runtime.Online
{
    using HeroGame.Core.Foundation;

    /// <summary>Marks a remote player's avatar so aiming and interaction know who it is.</summary>
    public sealed class RemotePlayerTag : MonoBehaviour
    {
        public EntityId CharacterId;
    }
}
