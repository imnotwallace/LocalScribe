using LocalScribe.Core.Audio;
using LocalScribe.Core.Live;
using Xunit;

namespace LocalScribe.Core.Tests;

public sealed class CaptureFrameBridgeTests
{
    private static float[] Frame(float value) => Enumerable.Repeat(value, 512).ToArray();

    [Fact]
    public async Task Frames_pushed_before_and_after_read_starts_all_arrive_in_order()
    {
        var source = new FakeCaptureSource(SourceKind.Local, [Frame(0.1f), Frame(0.2f), Frame(0.3f)]);
        using var bridge = new CaptureFrameBridge(source);

        source.Start();                       // synchronous replay: frames land before reading
        bridge.Complete();

        var got = new List<AudioFrame>();
        await foreach (var f in bridge.ReadAllAsync(CancellationToken.None)) got.Add(f);

        Assert.Equal(3, got.Count);
        Assert.Equal([0.1f, 0.2f, 0.3f], got.Select(f => f.Samples[0]));
        Assert.Equal(0, got[0].StartMs);
        Assert.Equal(32, got[1].StartMs);     // 512 samples @ 16 kHz = 32 ms
    }

    [Fact]
    public async Task Complete_ends_enumeration_and_detaches_handler()
    {
        var source = new FakeCaptureSource(SourceKind.Local, [Frame(0.5f)]);
        using var bridge = new CaptureFrameBridge(source);
        bridge.Complete();
        bridge.Complete();                    // idempotent

        source.Start();                       // fires after Complete: must NOT throw or enqueue

        var got = new List<AudioFrame>();
        await foreach (var f in bridge.ReadAllAsync(CancellationToken.None)) got.Add(f);
        Assert.Empty(got);
    }

    [Fact]
    public async Task Cancellation_stops_enumeration()
    {
        var source = new FakeCaptureSource(SourceKind.Local, []);
        using var bridge = new CaptureFrameBridge(source);
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
        {
            await foreach (var _ in bridge.ReadAllAsync(cts.Token)) { }
        });
    }
}
