using System.Text.Json;
using System.Text.Json.Serialization;

namespace CombatSolver;

/// <summary>
/// Reads and writes <see cref="FlashSearchMode"/>. <c>On</c> keeps its existing string value; the
/// removed <c>Shadow</c> mode and every numeric enum form read as <see cref="FlashSearchMode.Off"/>
/// so settings files, checkpoints and issue bundles recorded by older builds still load instead of
/// failing deserialization. Unknown text also reads as Off: a persisted mode the current build no
/// longer offers must not run Flash.
/// </summary>
internal sealed class FlashSearchModeJsonConverter : JsonConverter<FlashSearchMode>
{
    public override FlashSearchMode Read(ref Utf8JsonReader reader, Type typeToConvert,
        JsonSerializerOptions options)
    {
        switch (reader.TokenType)
        {
            case JsonTokenType.String:
                return string.Equals(reader.GetString(), nameof(FlashSearchMode.On),
                    StringComparison.OrdinalIgnoreCase)
                        ? FlashSearchMode.On
                        : FlashSearchMode.Off;
            case JsonTokenType.Number:
                // Current builds write the string form; a number is always a legacy value.
                return FlashSearchMode.Off;
            default:
                throw new JsonException($"Unexpected FlashSearchMode token {reader.TokenType}.");
        }
    }

    public override void Write(Utf8JsonWriter writer, FlashSearchMode value,
        JsonSerializerOptions options)
        => writer.WriteStringValue(value == FlashSearchMode.On
            ? nameof(FlashSearchMode.On)
            : nameof(FlashSearchMode.Off));
}
