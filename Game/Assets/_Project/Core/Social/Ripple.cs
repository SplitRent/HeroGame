using System;
using System.Collections.Generic;

namespace HeroGame.Core.Social
{
    using HeroGame.Core.Foundation;
    using HeroGame.Core.Time;

    /// <summary>A post on Ripple, the in-world social platform (GDD §51).</summary>
    [Serializable]
    public sealed class RipplePost
    {
        public long Id;
        /// <summary>Character or NPC who posted (None for organisations).</summary>
        public EntityId Author;
        public string AuthorName = "";
        public bool AuthorIsPlayer;
        public GameDateTime At;
        public string Text = "";
        public string Topic = "";
        public List<string> Tags = new List<string>();
        public EntityId District;
        public int Likes;
        public int Reposts;
        public List<EntityId> LikedBy = new List<EntityId>();
        /// <summary>History record that prompted it (NPC posts react to real events).</summary>
        public long SourceDay;
    }

    /// <summary>Authoring templates for NPC posts by topic (ripple_templates.json).</summary>
    [Serializable]
    public sealed class RippleTemplate
    {
        public string Topic = "";
        public string Tag = "";
        public List<string> Lines = new List<string>();
    }

    [Serializable]
    public sealed class RippleState
    {
        public const int MaxPosts = 1500;

        public List<RipplePost> Posts = new List<RipplePost>();
        public long NextId = 1;
        /// <summary>Who follows whom (players following NPCs or players).</summary>
        public Dictionary<string, List<EntityId>> Following = new Dictionary<string, List<EntityId>>();
        public int LastHistoryCount;

        public void Add(RipplePost p)
        {
            p.Id = NextId++;
            Posts.Add(p);
            if (Posts.Count > MaxPosts) Posts.RemoveRange(0, Posts.Count - MaxPosts);
        }

        public List<EntityId> FollowsOf(EntityId who) =>
            Following.TryGetValue(who.ToString(), out var list) ? list : new List<EntityId>();
    }
}
