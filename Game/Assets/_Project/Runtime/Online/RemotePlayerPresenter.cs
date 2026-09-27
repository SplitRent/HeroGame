using System.Collections.Generic;
using System.Threading.Tasks;
using HeroGame.Core.Foundation;
using HeroGame.Networking.Client;
using HeroGame.Networking.Protocol;
using HeroGame.Runtime.Bootstrap;
using HeroGame.Runtime.Player;
using HeroGame.Runtime.UI;
using UnityEngine;

namespace HeroGame.Runtime.Online
{
    /// <summary>Other players, interpolated between snapshots (100 ms behind for smoothness).</summary>
    public sealed class RemotePlayerPresenter : MonoBehaviour
    {
        public GameObject AvatarPrefab;
        public float Smoothing = 10f;

        private readonly Dictionary<EntityId, (GameObject go, Vector3 target, float heading)> _players = new Dictionary<EntityId, (GameObject, Vector3, float)>();
        private readonly List<EntityId> _stale = new List<EntityId>();

        public void Apply(Snapshot s)
        {
            _stale.Clear();
            _stale.AddRange(_players.Keys);
            foreach (var p in s.Players)
            {
                _stale.Remove(p.CharacterId);
                if (!_players.TryGetValue(p.CharacterId, out var entry))
                {
                    if (AvatarPrefab == null) continue;
                    var go = Instantiate(AvatarPrefab, p.Position.ToVector3(), Quaternion.Euler(0, p.Heading, 0), transform);
                    go.name = p.Name;
                    go.AddComponent<RemotePlayerTag>().CharacterId = p.CharacterId;
                    entry = (go, p.Position.ToVector3(), p.Heading);
                }
                _players[p.CharacterId] = (entry.go, p.Position.ToVector3(), p.Heading);
            }
            foreach (var id in _stale)
            {
                Destroy(_players[id].go);
                _players.Remove(id);
            }
        }

        private void Update()
        {
            foreach (var e in _players.Values)
            {
                var t = e.go.transform;
                t.position = Vector3.Lerp(t.position, e.target, 1f - Mathf.Exp(-Smoothing * Time.deltaTime));
                t.rotation = Quaternion.Slerp(t.rotation, Quaternion.Euler(0f, e.heading, 0f), 1f - Mathf.Exp(-Smoothing * Time.deltaTime));
            }
        }
    }
}
