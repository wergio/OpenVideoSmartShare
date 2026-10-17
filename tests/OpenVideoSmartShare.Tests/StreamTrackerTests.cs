using System.Net;
using OpenVideoSmartShare.Upnp;

namespace OpenVideoSmartShare.Tests;

public class StreamTrackerTests
{
    private static readonly IPAddress Tv = IPAddress.Parse("192.168.1.142");
    private static readonly IPAddress OtherTv = IPAddress.Parse("192.168.1.150");

    private DateTime _now = new(2026, 10, 17, 20, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void ShortResponsesAreNotStreaming()
    {
        var tracker = new StreamTracker(() => _now);
        var probe = tracker.Begin(Tv);
        Advance(1);
        Assert.Empty(tracker.ActiveClients());

        probe.Dispose();
        Assert.Empty(tracker.ActiveClients());
    }

    [Fact]
    public void StreamingLastsUntilShortlyAfterTheResponseEnds()
    {
        var tracker = new StreamTracker(() => _now);
        var stream = tracker.Begin(Tv);
        Advance(2);
        Assert.Equal([Tv], tracker.ActiveClients());

        stream.Dispose();
        Advance(3);
        Assert.Equal([Tv], tracker.ActiveClients());
        Advance(2);
        Assert.Empty(tracker.ActiveClients());
    }

    [Fact]
    public void SeekingKeepsStreaming()
    {
        var tracker = new StreamTracker(() => _now);
        var first = tracker.Begin(Tv);
        Advance(30);
        first.Dispose();
        // The new connection after a seek is not two seconds old yet.
        var second = tracker.Begin(Tv);
        Advance(1);
        Assert.Equal([Tv], tracker.ActiveClients());
        Advance(10);
        Assert.Equal([Tv], tracker.ActiveClients());
        second.Dispose();
    }

    [Fact]
    public void ResetForgetsTheCurrentStreamsOfThatClientOnly()
    {
        var tracker = new StreamTracker(() => _now);
        var stopped = tracker.Begin(Tv);
        var other = tracker.Begin(OtherTv);
        Advance(5);

        tracker.Reset(Tv);
        Assert.Equal([OtherTv], tracker.ActiveClients());
        stopped.Dispose();
        Assert.Equal([OtherTv], tracker.ActiveClients());

        // Playing again on the same TV counts as usual.
        var again = tracker.Begin(Tv);
        Advance(2);
        Assert.Equal(2, tracker.ActiveClients().Count);
        again.Dispose();
        other.Dispose();
    }

    [Fact]
    public void DisposingTwiceIsHarmless()
    {
        var tracker = new StreamTracker(() => _now);
        var stream = tracker.Begin(Tv);
        Advance(3);
        stream.Dispose();
        Advance(10);
        stream.Dispose();
        Assert.Empty(tracker.ActiveClients());
    }

    private void Advance(int seconds) => _now = _now.AddSeconds(seconds);
}
