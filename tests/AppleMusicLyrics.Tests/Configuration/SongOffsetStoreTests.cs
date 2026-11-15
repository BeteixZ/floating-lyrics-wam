using AppleMusicLyrics.Infrastructure.Windows.Configuration;
using Xunit;

namespace AppleMusicLyrics.Tests.Configuration;

public sealed class SongOffsetStoreTests : IDisposable
{
    private readonly string _root;

    public SongOffsetStoreTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "AppleMusicLyrics.Tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
    }

    [Fact]
    public void RememberAndGet_RoundTripsOffset()
    {
        var store = new SongOffsetStore(Path.Combine(_root, "offsets.json"));
        store.RememberOffset("Artist", "Song", 1.25);
        Assert.Equal(1.25, store.TryGetOffset("Artist", "Song"));
        Assert.Null(store.TryGetOffset("Artist", "Other"));
    }

    [Fact]
    public void Get_NormalizesCaseAndWhitespace()
    {
        var store = new SongOffsetStore(Path.Combine(_root, "offsets.json"));
        store.RememberOffset("  The   Artist ", "Some Song", 0.5);
        Assert.Equal(0.5, store.TryGetOffset("the artist", "some  song"));
    }

    [Fact]
    public void Get_MissingFile_ReturnsNull()
    {
        var store = new SongOffsetStore(Path.Combine(_root, "absent.json"));
        Assert.Null(store.TryGetOffset("Artist", "Song"));
    }

    [Fact]
    public void Get_CorruptFile_ReturnsNullAndRecovers()
    {
        var path = Path.Combine(_root, "corrupt.json");
        File.WriteAllText(path, "{ not json");
        var store = new SongOffsetStore(path);
        Assert.Null(store.TryGetOffset("Artist", "Song"));
        store.RememberOffset("Artist", "Song", 0.9);
        Assert.Equal(0.9, store.TryGetOffset("Artist", "Song"));
    }

    [Fact]
    public void Remember_PersistsAcrossInstances()
    {
        var path = Path.Combine(_root, "offsets.json");
        new SongOffsetStore(path).RememberOffset("Artist", "Song", 1.1);
        Assert.Equal(1.1, new SongOffsetStore(path).TryGetOffset("Artist", "Song"));
    }

    [Fact]
    public void Remember_EvictsLeastRecentlyUsedBeyondCapacity()
    {
        var store = new SongOffsetStore(Path.Combine(_root, "offsets.json"), capacity: 2);
        store.RememberOffset("Artist", "First", 0.1);
        store.RememberOffset("Artist", "Second", 0.2);
        store.RememberOffset("Artist", "Third", 0.3);
        Assert.Null(store.TryGetOffset("Artist", "First"));
        Assert.Equal(0.2, store.TryGetOffset("Artist", "Second"));
        Assert.Equal(0.3, store.TryGetOffset("Artist", "Third"));
    }

    [Fact]
    public void Remember_IgnoresEmptyIdentityAndNonFiniteOffset()
    {
        var path = Path.Combine(_root, "offsets.json");
        var store = new SongOffsetStore(path);
        store.RememberOffset(null, "  ", 0.5);
        store.RememberOffset("Artist", "Song", double.NaN);
        Assert.Null(store.TryGetOffset(null, ""));
        Assert.Null(store.TryGetOffset("Artist", "Song"));
        Assert.False(File.Exists(path));
    }

    public void Dispose()
    {
        Directory.Delete(_root, recursive: true);
    }
}
