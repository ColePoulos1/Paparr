using Paparr.API.Services;

namespace Paparr.UnitTests;

public sealed class FileStabilityTests
{
    [Fact]
    public void A_file_must_stop_changing_for_the_full_stability_window()
    {
        using var library = new TemporaryLibrary();
        var path = library.Write("one");
        var clock = new TestClock();
        var tracker = new FileStabilityTracker(clock);
        Assert.False(tracker.IsReady(new FileInfo(path), TimeSpan.FromSeconds(5)));
        clock.Advance(4);
        Assert.False(tracker.IsReady(new FileInfo(path), TimeSpan.FromSeconds(5)));
        File.AppendAllText(path, "more");
        Assert.False(tracker.IsReady(new FileInfo(path), TimeSpan.FromSeconds(5)));
        clock.Advance(5);
        Assert.True(tracker.IsReady(new FileInfo(path), TimeSpan.FromSeconds(5)));
    }

    private sealed class TestClock : TimeProvider
    {
        private DateTimeOffset now = DateTimeOffset.UtcNow;
        public override DateTimeOffset GetUtcNow() => now;
        public void Advance(int seconds) => now = now.AddSeconds(seconds);
    }
}
