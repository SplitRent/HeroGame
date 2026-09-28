using System;
using Newtonsoft.Json;

namespace HeroGame.Networking.Protocol
{
    using HeroGame.Core.Characters;

    /// <summary>
    /// A character's name and appearance on the wire (inside <see cref="Hello"/>). Decoding never trusts the
    /// sender: bad JSON gives nothing, and whatever parses is passed through <see cref="IdentityRules.Sanitize"/>.
    /// </summary>
    public static class IdentityCodec
    {
        private static readonly JsonSerializerSettings Settings = new JsonSerializerSettings
        {
            TypeNameHandling = TypeNameHandling.None,
            MaxDepth = 8,
            MissingMemberHandling = MissingMemberHandling.Ignore,
        };

        public static string Encode(CharacterIdentity identity) =>
            identity == null ? "" : JsonConvert.SerializeObject(IdentityRules.Sanitize(identity), Settings);

        /// <summary>The sanitised identity, or false when the text is empty, malformed or has no usable name.</summary>
        public static bool TryDecode(string json, out CharacterIdentity identity)
        {
            identity = null;
            if (string.IsNullOrEmpty(json)) return false;
            try
            {
                var parsed = JsonConvert.DeserializeObject<CharacterIdentity>(json, Settings);
                if (!IdentityRules.IsValid(parsed)) return false;
                identity = IdentityRules.Sanitize(parsed);
                return true;
            }
            catch (Exception ex) when (ex is JsonException || ex is ArgumentException || ex is FormatException || ex is InvalidCastException || ex is OverflowException)
            {
                return false;
            }
        }
    }
}
