using System.Text.Json;
using Blazma.Core.Ids;
using Blazma.Core.Mathematics;
using Blazma.Core.Timing;

namespace Blazma.Tests.Serialization;

public class JsonConvertersTests
{
    private static T RoundTrip<T>(T value) => JsonSerializer.Deserialize<T>(JsonSerializer.Serialize(value))!;

    [Fact]
    public void Vec3Format()
    {
        Assert.Equal("[1,2.5,-3]", JsonSerializer.Serialize(new Vec3(1, 2.5, -3)));
        Assert.Equal(new Vec3(1, 2, 3), JsonSerializer.Deserialize<Vec3>("[1, 2, 3]"));
    }

    [Fact]
    public void QuatFormat()
    {
        Assert.Equal("[0,0,0,1]", JsonSerializer.Serialize(Quat.Identity));
        Assert.Equal(new Quat(0, 1, 0, 0), JsonSerializer.Deserialize<Quat>("[0,1,0,0]"));
    }

    [Fact]
    public void DoublesRoundTripExactly()
    {
        var r = new Random(5);
        for (int i = 0; i < 200; i++)
        {
            var v = new Vec3(r.NextDouble() * 1e3, -r.NextDouble() * 1e-7, Math.PI * r.NextDouble());
            Assert.Equal(v, RoundTrip(v));
            var q = new Quat(r.NextDouble(), r.NextDouble(), r.NextDouble(), r.NextDouble()).Normalized();
            Assert.Equal(q, RoundTrip(q));
        }
    }

    [Theory]
    [InlineData("[]")]
    [InlineData("[1,2]")]
    [InlineData("[1,2,3,4]")]
    [InlineData("{}")]
    [InlineData("\"x\"")]
    [InlineData("[1,\"a\",3]")]
    [InlineData("null")]
    public void Vec3RejectsBadInput(string json) =>
        Assert.ThrowsAny<JsonException>(() => JsonSerializer.Deserialize<Vec3>(json));

    [Theory]
    [InlineData("[1,2,3]")]
    [InlineData("[1,2,3,4,5]")]
    [InlineData("[]")]
    [InlineData("{}")]
    public void QuatRejectsBadInput(string json) =>
        Assert.ThrowsAny<JsonException>(() => JsonSerializer.Deserialize<Quat>(json));

    [Fact]
    public void ObjectIdRoundTripAndEmptyIsNull()
    {
        var id = ObjectId.New();
        Assert.Equal($"\"{id.Value}\"", JsonSerializer.Serialize(id));
        Assert.Equal(id, RoundTrip(id));
        Assert.Equal("null", JsonSerializer.Serialize(ObjectId.Empty));
        Assert.True(JsonSerializer.Deserialize<ObjectId>("null").IsEmpty);
        Assert.Equal(id, JsonSerializer.Deserialize<ObjectId>($"\"{id.Value.ToLowerInvariant()}\""));
    }

    [Theory]
    [InlineData("\"short\"")]
    [InlineData("123")]
    [InlineData("\"\"")]
    public void ObjectIdRejectsBadInput(string json) =>
        Assert.ThrowsAny<JsonException>(() => JsonSerializer.Deserialize<ObjectId>(json));

    [Fact]
    public void FlicksIsPlainInteger()
    {
        Assert.Equal("705600000", JsonSerializer.Serialize(new Flicks(705_600_000)));
        Assert.Equal(new Flicks(-42), RoundTrip(new Flicks(-42)));
        Assert.Equal(new Flicks(long.MaxValue), RoundTrip(new Flicks(long.MaxValue)));
    }

    [Theory]
    [InlineData("1.5")]
    [InlineData("\"1\"")]
    [InlineData("null")]
    public void FlicksRejectsBadInput(string json) =>
        Assert.ThrowsAny<JsonException>(() => JsonSerializer.Deserialize<Flicks>(json));

    [Fact]
    public void FrameRateFormat()
    {
        Assert.Equal("{\"num\":30,\"den\":1}", JsonSerializer.Serialize(FrameRate.Fps30));
        Assert.Equal(FrameRate.Ntsc2997, RoundTrip(FrameRate.Ntsc2997));
        Assert.Equal(FrameRate.Fps30, JsonSerializer.Deserialize<FrameRate>("{\"den\":2,\"num\":60}"));
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("{\"num\":30}")]
    [InlineData("{\"num\":0,\"den\":1}")]
    [InlineData("{\"num\":30,\"den\":-1}")]
    [InlineData("{\"num\":\"30\",\"den\":1}")]
    [InlineData("30")]
    public void FrameRateRejectsBadInput(string json) =>
        Assert.ThrowsAny<JsonException>(() => JsonSerializer.Deserialize<FrameRate>(json));

    [Fact]
    public void WorksNestedInObjects()
    {
        var model = new Holder(new Vec3(1, 2, 3), Quat.Identity, ObjectId.Empty, new Flicks(5), FrameRate.Fps24);
        Assert.Equal(model, RoundTrip(model));
    }

    private sealed record Holder(Vec3 P, Quat R, ObjectId Id, Flicks T, FrameRate Rate);
}
