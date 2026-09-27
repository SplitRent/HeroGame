using HeroGame.Core.Characters;
using HeroGame.Runtime.Bootstrap;
using UnityEngine;

namespace HeroGame.Runtime.UI
{
    /// <summary>
    /// The phone's Ripple app (GDD §51): compose, trending tags and the feed with likes and follows. Drawn inside
    /// the phone panel. Placeholder IMGUI (docs/ASSET_TRACKER.md).
    /// </summary>
    public sealed class RippleApp
    {
        private string _draft = "";
        private string _tag = "";
        private string _status = "";

        public void Draw(GameSession session)
        {
            var w = session.World;
            var me = session.LocalCharacter;
            GUILayout.Label("Ripple · " + w.Feed.FollowerCount(me.CharacterId) + " followers");
            _draft = GUILayout.TextArea(_draft, Core.Simulation.RippleService.MaxPostLength, GUILayout.Height(54));
            GUILayout.BeginHorizontal();
            GUILayout.Label(_draft.Length + "/" + Core.Simulation.RippleService.MaxPostLength);
            if (GUILayout.Button("Post", GUILayout.Width(70)))
            {
                var name = ServiceRegistry.TryGet<AccountProfile>(out var a) && a != null ? a.Character.FullName : "You";
                var r = w.Feed.Post(me, name, _draft, out _);
                _status = r.Success ? "Posted." : r.Error;
                if (r.Success) _draft = "";
            }
            GUILayout.EndHorizontal();

            GUILayout.BeginHorizontal();
            GUILayout.Label("Trending:");
            foreach (var (tag, _) in w.Feed.Trending())
                if (GUILayout.Button(tag)) _tag = _tag == tag ? "" : tag;
            GUILayout.EndHorizontal();
            if (_tag != "") GUILayout.Label("Showing " + _tag);

            foreach (var p in w.Feed.Feed(me.CharacterId, 25, _tag))
            {
                GUILayout.BeginVertical(GUI.skin.box);
                GUILayout.Label(p.AuthorName + " · " + p.At.Hour.ToString("00") + ":" + p.At.Minute.ToString("00"));
                GUILayout.Label(p.Text);
                GUILayout.BeginHorizontal();
                if (GUILayout.Button("♥ " + p.Likes, GUILayout.Width(70))) { var r = w.Feed.Like(me, p.Id); if (!r.Success) _status = r.Error; }
                if (p.Author != me.CharacterId)
                {
                    var following = w.Ripple.FollowsOf(me.CharacterId).Contains(p.Author);
                    if (GUILayout.Button(following ? "Unfollow" : "Follow", GUILayout.Width(80))) w.Feed.Follow(me, p.Author, !following);
                }
                GUILayout.EndHorizontal();
                GUILayout.EndVertical();
            }
            GUILayout.Label(_status);
        }
    }
}
