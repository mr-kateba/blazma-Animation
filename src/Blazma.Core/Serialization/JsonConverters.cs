using System.Text.Json;
using System.Text.Json.Serialization;
using Blazma.Core.Ids;
using Blazma.Core.Mathematics;
using Blazma.Core.Timing;

namespace Blazma.Core.Serialization;

internal static class JsonHelpers
{
    public static double[] ReadArray(ref Utf8JsonReader reader, int count, string name)
    {
        if (reader.TokenType != JsonTokenType.StartArray)
            throw new JsonException($"{name} must be a JSON array of {count} numbers.");
        var values = new double[count];
        for (int i = 0; i < count; i++)
        {
            if (!reader.Read() || reader.TokenType != JsonTokenType.Number)
                throw new JsonException($"{name} must be a JSON array of {count} numbers.");
            values[i] = reader.GetDouble();
        }
        if (!reader.Read() || reader.TokenType != JsonTokenType.EndArray)
            throw new JsonException($"{name} must be a JSON array of {count} numbers.");
        return values;
    }
}

/// <summary>Vec3 as [x,y,z].</summary>
public sealed class Vec3JsonConverter : JsonConverter<Vec3>
{
    /// <inheritdoc />
    public override Vec3 Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        double[] v = JsonHelpers.ReadArray(ref reader, 3, nameof(Vec3));
        return new Vec3(v[0], v[1], v[2]);
    }

    /// <inheritdoc />
    public override void Write(Utf8JsonWriter writer, Vec3 value, JsonSerializerOptions options)
    {
        writer.WriteStartArray();
        writer.WriteNumberValue(value.X);
        writer.WriteNumberValue(value.Y);
        writer.WriteNumberValue(value.Z);
        writer.WriteEndArray();
    }
}

/// <summary>Quat as [x,y,z,w].</summary>
public sealed class QuatJsonConverter : JsonConverter<Quat>
{
    /// <inheritdoc />
    public override Quat Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        double[] v = JsonHelpers.ReadArray(ref reader, 4, nameof(Quat));
        return new Quat(v[0], v[1], v[2], v[3]);
    }

    /// <inheritdoc />
    public override void Write(Utf8JsonWriter writer, Quat value, JsonSerializerOptions options)
    {
        writer.WriteStartArray();
        writer.WriteNumberValue(value.X);
        writer.WriteNumberValue(value.Y);
        writer.WriteNumberValue(value.Z);
        writer.WriteNumberValue(value.W);
        writer.WriteEndArray();
    }
}

/// <summary>ObjectId as a string; the empty id is null.</summary>
public sealed class ObjectIdJsonConverter : JsonConverter<ObjectId>
{
    /// <inheritdoc />
    public override bool HandleNull => true;

    /// <inheritdoc />
    public override ObjectId Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType == JsonTokenType.Null) return ObjectId.Empty;
        if (reader.TokenType != JsonTokenType.String)
            throw new JsonException("ObjectId must be a string or null.");
        return ObjectId.TryParse(reader.GetString(), out var id)
            ? id
            : throw new JsonException("Invalid ObjectId string.");
    }

    /// <inheritdoc />
    public override void Write(Utf8JsonWriter writer, ObjectId value, JsonSerializerOptions options)
    {
        if (value.IsEmpty) writer.WriteNullValue();
        else writer.WriteStringValue(value.Value);
    }
}

/// <summary>Flicks as a JSON integer.</summary>
public sealed class FlicksJsonConverter : JsonConverter<Flicks>
{
    /// <inheritdoc />
    public override Flicks Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType != JsonTokenType.Number || !reader.TryGetInt64(out long v))
            throw new JsonException("Flicks must be a JSON integer.");
        return new Flicks(v);
    }

    /// <inheritdoc />
    public override void Write(Utf8JsonWriter writer, Flicks value, JsonSerializerOptions options) =>
        writer.WriteNumberValue(value.Value);
}

/// <summary>FrameRate as {"num":N,"den":D}.</summary>
public sealed class FrameRateJsonConverter : JsonConverter<FrameRate>
{
    /// <inheritdoc />
    public override FrameRate Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType != JsonTokenType.StartObject)
            throw new JsonException("FrameRate must be an object with num and den.");
        int? num = null, den = null;
        while (reader.Read())
        {
            if (reader.TokenType == JsonTokenType.EndObject) break;
            if (reader.TokenType != JsonTokenType.PropertyName) throw new JsonException("Malformed FrameRate.");
            string? name = reader.GetString();
            reader.Read();
            switch (name)
            {
                case "num":
                    if (reader.TokenType != JsonTokenType.Number || !reader.TryGetInt32(out int n))
                        throw new JsonException("FrameRate.num must be an integer.");
                    num = n;
                    break;
                case "den":
                    if (reader.TokenType != JsonTokenType.Number || !reader.TryGetInt32(out int d))
                        throw new JsonException("FrameRate.den must be an integer.");
                    den = d;
                    break;
                default:
                    reader.Skip();
                    break;
            }
        }
        if (reader.TokenType != JsonTokenType.EndObject || num is null || den is null)
            throw new JsonException("FrameRate requires num and den.");
        try
        {
            return new FrameRate(num.Value, den.Value);
        }
        catch (ArgumentOutOfRangeException e)
        {
            throw new JsonException("FrameRate num and den must be positive.", e);
        }
    }

    /// <inheritdoc />
    public override void Write(Utf8JsonWriter writer, FrameRate value, JsonSerializerOptions options)
    {
        writer.WriteStartObject();
        writer.WriteNumber("num", value.Numerator);
        writer.WriteNumber("den", value.Denominator);
        writer.WriteEndObject();
    }
}

/// <summary>
/// Transform as {"translation":[x,y,z],"rotation":[x,y,z,w],"scale":[x,y,z]}. Missing parts default
/// to identity (a missing scale must not become 0 and collapse a skeleton); rotations are normalized;
/// a zero-length rotation or a zero scale component is rejected.
/// </summary>
public sealed class TransformJsonConverter : JsonConverter<Transform>
{
    /// <inheritdoc />
    public override Transform Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType != JsonTokenType.StartObject)
            throw new JsonException("Transform must be a JSON object.");
        var t = Vec3.Zero;
        var r = Quat.Identity;
        var s = Vec3.One;
        var vec = new Vec3JsonConverter();
        var quat = new QuatJsonConverter();
        while (reader.Read())
        {
            if (reader.TokenType == JsonTokenType.EndObject)
            {
                if (r.Length() < 1e-12) throw new JsonException("Transform rotation has zero length.");
                if (s.X == 0 || s.Y == 0 || s.Z == 0) throw new JsonException("Transform scale must not contain 0.");
                // Only renormalize when needed, so saved data round-trips bit-exactly.
                return new Transform(t, Math.Abs(r.Length() - 1) > 1e-12 ? r.Normalized() : r, s);
            }
            if (reader.TokenType != JsonTokenType.PropertyName) throw new JsonException("Invalid Transform.");
            var name = reader.GetString();
            reader.Read();
            switch (name)
            {
                case "translation": t = vec.Read(ref reader, typeof(Vec3), options); break;
                case "rotation": r = quat.Read(ref reader, typeof(Quat), options); break;
                case "scale": s = vec.Read(ref reader, typeof(Vec3), options); break;
                default: reader.Skip(); break;
            }
        }
        throw new JsonException("Unterminated Transform.");
    }

    /// <inheritdoc />
    public override void Write(Utf8JsonWriter writer, Transform value, JsonSerializerOptions options)
    {
        writer.WriteStartObject();
        writer.WritePropertyName("translation");
        new Vec3JsonConverter().Write(writer, value.Translation, options);
        writer.WritePropertyName("rotation");
        new QuatJsonConverter().Write(writer, value.Rotation, options);
        writer.WritePropertyName("scale");
        new Vec3JsonConverter().Write(writer, value.Scale, options);
        writer.WriteEndObject();
    }
}
