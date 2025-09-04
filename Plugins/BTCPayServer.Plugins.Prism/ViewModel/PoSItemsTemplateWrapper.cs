using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace BTCPayServer.Plugins.Prism.ViewModel;

public class PoSItemsTemplateWrapper
{
    [JsonPropertyName("items")]
    public List<PoSAppItem> Items { get; set; } = new();
}

public class PoSAppItem
{
    [JsonPropertyName("id")]
    public string Id { get; set; } = string.Empty;

    [JsonPropertyName("title")]
    public string Title { get; set; } = string.Empty;

    [JsonPropertyName("price")]
    [JsonConverter(typeof(DecimalStringConverter))]
    public decimal? Price { get; set; }

    [JsonPropertyName("description")]
    public string Description { get; set; }

    [JsonPropertyName("image")]
    public string Image { get; set; }

    [JsonPropertyName("priceType")]
    public string PriceType { get; set; }

    [JsonPropertyName("disabled")]
    public bool Disabled { get; set; }
}

public class DecimalStringConverter : JsonConverter<decimal?>
{
    public override decimal? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType == JsonTokenType.String && decimal.TryParse(reader.GetString(), NumberStyles.Number, CultureInfo.InvariantCulture, out var value))
        {
            return value;
        }
        if (reader.TokenType == JsonTokenType.Number)
        {
            return reader.GetDecimal();
        }
        return null;
    }

    public override void Write(Utf8JsonWriter writer, decimal? value, JsonSerializerOptions options)
    {
        if (value.HasValue)
            writer.WriteStringValue(value.Value.ToString(CultureInfo.InvariantCulture));
        else
            writer.WriteNullValue();
    }
}

