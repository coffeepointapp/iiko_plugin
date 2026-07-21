using System;
using Newtonsoft.Json;

namespace Bonoos.iikoFront.LoyaltyPlugin.Models
{
    /// <summary>
    /// Accepts JSON number or string for money-like fields (OpenAPI availableAmount).
    /// </summary>
    internal sealed class FlexibleDecimalConverter : JsonConverter
    {
        public override bool CanConvert(Type objectType) =>
            objectType == typeof(decimal) || objectType == typeof(decimal?);

        public override object ReadJson(JsonReader reader, Type objectType, object existingValue, JsonSerializer serializer)
        {
            if (reader.TokenType == JsonToken.Null)
                return null;
            if (reader.TokenType == JsonToken.Integer || reader.TokenType == JsonToken.Float)
                return Convert.ToDecimal(reader.Value, System.Globalization.CultureInfo.InvariantCulture);
            if (reader.TokenType == JsonToken.String)
            {
                var s = (string)reader.Value;
                if (string.IsNullOrWhiteSpace(s))
                    return null;
                if (decimal.TryParse(s, System.Globalization.NumberStyles.Any,
                        System.Globalization.CultureInfo.InvariantCulture, out var d))
                    return d;
                return null;
            }
            return null;
        }

        public override void WriteJson(JsonWriter writer, object value, JsonSerializer serializer)
        {
            if (value == null) writer.WriteNull();
            else writer.WriteValue(Convert.ToDecimal(value, System.Globalization.CultureInfo.InvariantCulture));
        }
    }
}
