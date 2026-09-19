using System;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Jellyfin.Plugin.Chaosflix.Api.Models;

/// <summary>
/// Reads a JSON number that may be <c>null</c> into a plain <see cref="int"/>, mapping null to 0.
///
/// media.ccc.de sends <c>null</c> for <c>length</c>, <c>size</c>, <c>width</c> and <c>height</c> on
/// recordings that are not media (unfinished subtitle tracks, for instance). System.Text.Json
/// rejects null for a non-nullable int, and because those recordings sit in the same array as the
/// video ones, a single null made the whole event fail to deserialise and the talk disappear.
/// </summary>
public sealed class NullableToZeroInt32Converter : JsonConverter<int>
{
    /// <inheritdoc />
    public override int Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        => reader.TokenType == JsonTokenType.Null ? 0 : reader.GetInt32();

    /// <inheritdoc />
    public override void Write(Utf8JsonWriter writer, int value, JsonSerializerOptions options)
        => writer.WriteNumberValue(value);
}
