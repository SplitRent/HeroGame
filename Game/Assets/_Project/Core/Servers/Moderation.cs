using System;
using System.Collections.Generic;
using HeroGame.Core.Time;

namespace HeroGame.Core.Servers
{
    [Flags]
    public enum ServerPermission
    {
        None = 0,
        Kick = 1 << 0,
        Mute = 1 << 1,
        Ban = 1 << 2,
        ViewLogs = 1 << 3,
        ViewReports = 1 << 4,
        EditConfig = 1 << 5,
        ManageRoles = 1 << 6,
        Announce = 1 << 7,
        Teleport = 1 << 8,
        Spectate = 1 << 9,
        /// <summary>Economy/world edits (grant money, move property). Every use is audited.</summary>
        WorldAdmin = 1 << 10,
        ManageWhitelist = 1 << 11,
    }

    [Serializable]
    public sealed class ServerRole
    {
        public string Id = "";
        public string Name = "";
        public int Rank;
        public ServerPermission Permissions;

        public static ServerRole Owner() => new ServerRole { Id = "owner", Name = "Owner", Rank = 100, Permissions = (ServerPermission)~0 };
        public static ServerRole Admin() => new ServerRole
        {
            Id = "admin", Name = "Administrator", Rank = 80,
            Permissions = ServerPermission.Kick | ServerPermission.Mute | ServerPermission.Ban | ServerPermission.ViewLogs | ServerPermission.ViewReports |
                          ServerPermission.EditConfig | ServerPermission.Announce | ServerPermission.Teleport | ServerPermission.Spectate |
                          ServerPermission.WorldAdmin | ServerPermission.ManageWhitelist | ServerPermission.ManageRoles,
        };
        public static ServerRole Moderator() => new ServerRole
        {
            Id = "moderator", Name = "Moderator", Rank = 50,
            Permissions = ServerPermission.Kick | ServerPermission.Mute | ServerPermission.ViewLogs | ServerPermission.ViewReports | ServerPermission.Spectate,
        };
        public static ServerRole Player() => new ServerRole { Id = "player", Name = "Player", Rank = 0, Permissions = ServerPermission.None };
    }

    public enum ModerationActionKind
    {
        Kick,
        Mute,
        Unmute,
        Ban,
        Unban,
        Warn,
        ConfigChange,
        MoneyGrant,
        PropertyTransfer,
        Teleport,
    }

    [Serializable]
    public sealed class ModerationAction
    {
        public ModerationActionKind Kind;
        public string ActorAccountId = "";
        public string TargetAccountId = "";
        public string Reason = "";
        public long RealTimeUnixSeconds;
        public GameDateTime GameTime;
        /// <summary>For timed mutes/bans; 0 = permanent.</summary>
        public long DurationSeconds;
    }

    /// <summary>Role-based permission checks plus an append-only moderation history (GDD §89).</summary>
    public sealed class ModerationService
    {
        private readonly Dictionary<string, ServerRole> _roles = new Dictionary<string, ServerRole>();
        private readonly Dictionary<string, string> _roleByAccount = new Dictionary<string, string>();
        public readonly List<ModerationAction> History = new List<ModerationAction>();

        public ModerationService()
        {
            foreach (var r in new[] { ServerRole.Owner(), ServerRole.Admin(), ServerRole.Moderator(), ServerRole.Player() }) _roles[r.Id] = r;
        }

        public void DefineRole(ServerRole role) => _roles[role.Id] = role;

        public ServerRole RoleOf(string accountId)
        {
            return _roleByAccount.TryGetValue(accountId ?? "", out var id) && _roles.TryGetValue(id, out var role) ? role : _roles["player"];
        }

        public bool Can(string accountId, ServerPermission permission) => (RoleOf(accountId).Permissions & permission) == permission;

        /// <summary>Assign a role. Actors can only grant roles strictly below their own rank.</summary>
        public bool AssignRole(string actorAccountId, string targetAccountId, string roleId, bool bootstrap = false)
        {
            if (!_roles.TryGetValue(roleId, out var role)) return false;
            if (!bootstrap)
            {
                var actor = RoleOf(actorAccountId);
                if ((actor.Permissions & ServerPermission.ManageRoles) == 0) return false;
                if (role.Rank >= actor.Rank || RoleOf(targetAccountId).Rank >= actor.Rank) return false;
            }
            _roleByAccount[targetAccountId] = roleId;
            return true;
        }

        /// <summary>Records an action if permitted. Returns false (and records nothing) otherwise.</summary>
        public bool Perform(ModerationAction action)
        {
            var needed = Required(action.Kind);
            if (!Can(action.ActorAccountId, needed)) return false;
            if (!string.IsNullOrEmpty(action.TargetAccountId) && RoleOf(action.TargetAccountId).Rank >= RoleOf(action.ActorAccountId).Rank
                && action.Kind != ModerationActionKind.ConfigChange && action.Kind != ModerationActionKind.MoneyGrant)
                return false; // cannot moderate peers or superiors
            History.Add(action);
            return true;
        }

        public bool IsBanned(string accountId, long nowUnix) => ActiveUntil(accountId, ModerationActionKind.Ban, ModerationActionKind.Unban, nowUnix);
        public bool IsMuted(string accountId, long nowUnix) => ActiveUntil(accountId, ModerationActionKind.Mute, ModerationActionKind.Unmute, nowUnix);

        private bool ActiveUntil(string accountId, ModerationActionKind on, ModerationActionKind off, long nowUnix)
        {
            for (var i = History.Count - 1; i >= 0; i--)
            {
                var a = History[i];
                if (a.TargetAccountId != accountId) continue;
                if (a.Kind == off) return false;
                if (a.Kind == on) return a.DurationSeconds == 0 || nowUnix < a.RealTimeUnixSeconds + a.DurationSeconds;
            }
            return false;
        }

        private static ServerPermission Required(ModerationActionKind kind)
        {
            switch (kind)
            {
                case ModerationActionKind.Kick: return ServerPermission.Kick;
                case ModerationActionKind.Mute:
                case ModerationActionKind.Unmute:
                case ModerationActionKind.Warn: return ServerPermission.Mute;
                case ModerationActionKind.Ban:
                case ModerationActionKind.Unban: return ServerPermission.Ban;
                case ModerationActionKind.ConfigChange: return ServerPermission.EditConfig;
                case ModerationActionKind.Teleport: return ServerPermission.Teleport;
                default: return ServerPermission.WorldAdmin;
            }
        }
    }
}
