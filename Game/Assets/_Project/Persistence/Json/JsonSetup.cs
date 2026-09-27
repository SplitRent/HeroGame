using System;
using HeroGame.Core.Foundation;
using HeroGame.Core.Time;
using Newtonsoft.Json;
using Newtonsoft.Json.Converters;
using Newtonsoft.Json.Serialization;

namespace HeroGame.Persistence.Json
{
    /// <summary>Single place that defines how game data maps to JSON (saves, content, network snapshots).</summary>
    public static class JsonSetup
    {
        public static JsonSerializerSettings CreateSettings(bool indented = false)
        {
            var settings = new JsonSerializerSettings
            {
                Formatting = indented ? Formatting.Indented : Formatting.None,
                NullValueHandling = NullValueHandling.Ignore,
                DefaultValueHandling = DefaultValueHandling.Include,
                ObjectCreationHandling = ObjectCreationHandling.Replace,
                MissingMemberHandling = MissingMemberHandling.Ignore,
                TypeNameHandling = TypeNameHandling.None, // never allow type names from data (security)
                ContractResolver = new DefaultContractResolver(),
            };
            settings.Converters.Add(new StringEnumConverter());
            settings.Converters.Add(new EntityIdConverter());
            settings.Converters.Add(new MoneyConverter());
            settings.Converters.Add(new GameDateTimeConverter());
            return settings;
        }

        public static readonly JsonSerializerSettings Compact = CreateSettings(false);
        public static readonly JsonSerializerSettings Pretty = CreateSettings(true);

        public static string Serialize(object value, bool indented = false) => JsonConvert.SerializeObject(value, indented ? Pretty : Compact);
        public static T Deserialize<T>(string json) => JsonConvert.DeserializeObject<T>(json, Compact);
    }

    /// <summary>EntityId ⇄ "Kind:Sequence" (human readable in saves and content).</summary>
    public sealed class EntityIdConverter : JsonConverter<EntityId>
    {
        public override void WriteJson(JsonWriter writer, EntityId value, JsonSerializer serializer) => writer.WriteValue(value.ToString());

        public override EntityId ReadJson(JsonReader reader, Type objectType, EntityId existingValue, bool hasExistingValue, JsonSerializer serializer)
        {
            if (reader.TokenType == JsonToken.Null) return EntityId.None;
            if (reader.TokenType == JsonToken.Integer) return new EntityId(Convert.ToUInt64(reader.Value));
            var text = reader.Value as string;
            if (EntityId.TryParse(text, out var id)) return id;
            throw new JsonSerializationException("Invalid EntityId '" + text + "' at " + reader.Path);
        }
    }

    public sealed class MoneyConverter : JsonConverter<Money>
    {
        public override void WriteJson(JsonWriter writer, Money value, JsonSerializer serializer) => writer.WriteValue(value.Cents);

        public override Money ReadJson(JsonReader reader, Type objectType, Money existingValue, bool hasExistingValue, JsonSerializer serializer)
        {
            return reader.TokenType == JsonToken.Null ? Money.Zero : new Money(Convert.ToInt64(reader.Value));
        }
    }

    public sealed class GameDateTimeConverter : JsonConverter<GameDateTime>
    {
        public override void WriteJson(JsonWriter writer, GameDateTime value, JsonSerializer serializer) => writer.WriteValue(value.TotalSeconds);

        public override GameDateTime ReadJson(JsonReader reader, Type objectType, GameDateTime existingValue, bool hasExistingValue, JsonSerializer serializer)
        {
            return reader.TokenType == JsonToken.Null ? default : new GameDateTime(Convert.ToInt64(reader.Value));
        }
    }
}
