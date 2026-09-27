using HeroGame.Runtime.Bootstrap;
using HeroGame.Runtime.Online;
using HeroGame.Runtime.Vehicles;
using UnityEngine;

namespace HeroGame.Runtime.WorldProps
{
    /// <summary>
    /// A piece of street furniture in the scene. Vehicle hits are reported to the authority: the local world in single
    /// player, or the server (which uses its own speed and position) online.
    /// </summary>
    public sealed class DestructibleProp : MonoBehaviour
    {
        public int PropId;

        private void OnCollisionEnter(Collision collision)
        {
            var body = collision.rigidbody;
            if (body == null || body.GetComponent<VehicleController>() == null) return;
            var speed = collision.relativeVelocity.magnitude;
            if (speed < 3f) return;
            var online = NetworkSession.Current;
            if (online != null && online.State == Networking.Client.ClientState.Connected)
            {
                _ = online.Request("prop.impact", new System.Collections.Generic.Dictionary<string, string> { ["prop"] = PropId.ToString(System.Globalization.CultureInfo.InvariantCulture) });
                return;
            }
            if (!ServiceRegistry.TryGet<GameSession>(out var session)) return;
            var prop = session.World.Destructibles.Get(PropId);
            session.World.Destructibles.Impact(prop, speed, body.mass);
        }
    }
}
