using System;
using System.Collections.Generic;
using System.Globalization;
using HeroGame.Core.Characters;
using HeroGame.Core.Foundation;
using HeroGame.Core.Servers;
using HeroGame.Core.Simulation;
using HeroGame.Networking.Protocol;

namespace HeroGame.Networking.Server
{
    /// <summary>Everything a request handler may use. Identity comes from the connection, never from arguments.</summary>
    public sealed class RequestContext
    {
        public readonly GameServer Server;
        public readonly ServerConnection Connection;
        public readonly Request Request;

        public RequestContext(GameServer server, ServerConnection connection, Request request)
        {
            Server = server;
            Connection = connection;
            Request = request;
        }

        public World World => Server.World;
        public ServerCharacter Me => Connection.Character;
        public string AccountId => Connection.AccountId;

        /// <summary>Idempotency key scoped to this account so clients cannot collide with (or replay) other players' keys.</summary>
        public string Key => "acct:" + AccountId + ":" + (string.IsNullOrEmpty(Request.Key) ? "r" + Request.RequestId + ":" + World.Clock.Now.TotalSeconds : Request.Key);

        public string Str(string name, int maxLength = 256)
        {
            if (!Request.Args.TryGetValue(name, out var v)) throw new ArgumentException("Missing argument '" + name + "'.");
            if (v.Length > maxLength) throw new ArgumentException("Argument '" + name + "' too long.");
            return v;
        }

        public string OptStr(string name, string fallback = "") => Request.Args.TryGetValue(name, out var v) ? v : fallback;

        public EntityId Id(string name)
        {
            if (!EntityId.TryParse(Str(name, 64), out var id)) throw new ArgumentException("Argument '" + name + "' is not an id.");
            return id;
        }

        public long Long(string name, long min = long.MinValue, long max = long.MaxValue)
        {
            if (!long.TryParse(Str(name, 32), NumberStyles.Integer, CultureInfo.InvariantCulture, out var v) || v < min || v > max)
                throw new ArgumentException("Argument '" + name + "' out of range.");
            return v;
        }

        public float Float(string name, float min, float max)
        {
            if (!float.TryParse(Str(name, 32), NumberStyles.Float, CultureInfo.InvariantCulture, out var v) || float.IsNaN(v) || v < min || v > max)
                throw new ArgumentException("Argument '" + name + "' out of range.");
            return v;
        }

        public bool Near(WorldPosition target, float radius) => WorldPosition.DistanceXZ(Connection.Position, target) <= radius;

        public bool Can(ServerPermission permission) => Server.Moderation.Can(AccountId, permission);

        public static Response Ok(Dictionary<string, string> data = null) => new Response { Success = true, Data = data ?? new Dictionary<string, string>() };
        public static Response Fail(string error) => new Response { Success = false, Error = error ?? "Failed." };
        public static Response From(OpResult r) => r.Success ? Ok() : Fail(r.Error);
    }

    public delegate Response RequestHandler(RequestContext ctx);

    /// <summary>Maps request op names to handlers; argument errors become failed responses, not disconnects.</summary>
    public sealed class RequestRouter
    {
        private readonly Dictionary<string, RequestHandler> _handlers = new Dictionary<string, RequestHandler>(StringComparer.Ordinal);

        public void Register(string op, RequestHandler handler) => _handlers[op] = handler;

        public IEnumerable<string> Ops => _handlers.Keys;

        public Response Handle(RequestContext ctx)
        {
            if (!_handlers.TryGetValue(ctx.Request.Op ?? "", out var handler)) return RequestContext.Fail("Unknown request '" + ctx.Request.Op + "'.");
            try
            {
                return handler(ctx) ?? RequestContext.Fail("No result.");
            }
            catch (ArgumentException ex)
            {
                return RequestContext.Fail(ex.Message);
            }
        }
    }
}
