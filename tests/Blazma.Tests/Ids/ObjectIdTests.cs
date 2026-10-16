using Blazma.Core.Ids;

namespace Blazma.Tests.Ids;

public class ObjectIdTests
{
    private const string Chars = "0123456789ABCDEFGHJKMNPQRSTVWXYZ";

    [Fact]
    public void LengthAndCharset()
    {
        for (int i = 0; i < 100; i++)
        {
            var id = ObjectId.New();
            Assert.Equal(26, id.Value.Length);
            Assert.All(id.Value, c => Assert.Contains(c, Chars));
            Assert.False(id.IsEmpty);
            Assert.Equal(id.Value, id.ToString());
        }
    }

    [Fact]
    public void UniqueIds()
    {
        var set = new HashSet<ObjectId>();
        for (int i = 0; i < 5000; i++) Assert.True(set.Add(ObjectId.New()));
    }

    [Fact]
    public void LaterTimeSortsLater()
    {
        var t = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        string prev = ObjectId.New(t).Value;
        for (int i = 1; i < 200; i++)
        {
            string next = ObjectId.New(t.AddMilliseconds(i * 7)).Value;
            Assert.True(string.CompareOrdinal(prev, next) < 0);
            prev = next;
        }
    }

    [Fact]
    public void SameMillisecondStaysOrdered()
    {
        var t = new DateTimeOffset(2030, 5, 5, 0, 0, 0, TimeSpan.Zero);
        string prev = ObjectId.New(t).Value;
        for (int i = 0; i < 500; i++)
        {
            string next = ObjectId.New(t).Value;
            Assert.True(string.CompareOrdinal(prev, next) < 0);
            prev = next;
        }
    }

    [Fact]
    public void TimestampEncodedInPrefix()
    {
        var id = ObjectId.New(DateTimeOffset.FromUnixTimeMilliseconds(0).AddMilliseconds(31));
        Assert.StartsWith("000000000", id.Value); // 10 chars = 50 bits timestamp; 31 fits in the 10th
        Assert.Equal('Z', id.Value[9]);
    }

    [Fact]
    public void ParseIsCaseInsensitiveAndNormalizes()
    {
        var id = ObjectId.New();
        var parsed = ObjectId.Parse(id.Value.ToLowerInvariant());
        Assert.Equal(id, parsed);
        Assert.Equal(id.Value, parsed.Value);
        Assert.True(ObjectId.TryParse(id.Value, out var p2));
        Assert.Equal(id, p2);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("0123")]
    [InlineData("0000000000000000000000000U")]
    [InlineData("0000000000000000000000000I")]
    [InlineData("8ZZZZZZZZZZZZZZZZZZZZZZZZZ")]
    [InlineData("000000000000000000000000000")]
    public void TryParseRejects(string? s)
    {
        Assert.False(ObjectId.TryParse(s, out var id));
        Assert.True(id.IsEmpty);
    }

    [Fact]
    public void ParseThrowsFormatException() => Assert.Throws<FormatException>(() => ObjectId.Parse("nope"));

    [Fact]
    public void EmptyBehaviour()
    {
        Assert.True(ObjectId.Empty.IsEmpty);
        Assert.True(default(ObjectId).IsEmpty);
        Assert.Equal("", ObjectId.Empty.ToString());
        Assert.Equal(ObjectId.Empty, default);
        Assert.NotEqual(ObjectId.New(), ObjectId.Empty);
    }
}
