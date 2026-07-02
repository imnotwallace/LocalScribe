using LocalScribe.Core.Audio;
using LocalScribe.Core.Pipeline;
using LocalScribe.Core.Vad;

public class VadCoreTests
{
    private const int Win = 512;                      // 32 ms @ 16 kHz
    private const int WinMs = 32;

    /// <summary>Scripted model: probability per window index; default 0.</summary>
    private sealed class ScriptedProbe : ISpeechProbabilityModel
    {
        private readonly Func<int, float> _probOf;
        private int _i;
        public ScriptedProbe(Func<int, float> probOf) => _probOf = probOf;
        public float SpeechProbability(ReadOnlySpan<float> window) => _probOf(_i++);
        public void Reset() => _i = 0;
    }

    private static VadCore Sut(Func<int, float> probOf, VadOptions? o = null) =>
        new(SourceKind.Local, o ?? new VadOptions(), new ScriptedProbe(probOf));

    /// <summary>Push n windows of dummy PCM as one frame starting at startMs.</summary>
    private static List<AudioSegment> PushWindows(VadCore vad, int n, long startMs = 0)
    {
        var all = new List<AudioSegment>();
        all.AddRange(vad.Push(new AudioFrame(SourceKind.Local, startMs, new float[n * Win])));
        return all;
    }

    [Fact]
    public void Speech_burst_emits_one_padded_segment()
    {
        // windows 10..29 speech (20 windows = 640 ms), silence elsewhere.
        var vad = Sut(i => i is >= 10 and < 30 ? 0.9f : 0.1f);
        var segs = PushWindows(vad, 60);

        var s = Assert.Single(segs);
        // leading pad: 5 windows before onset -> starts at window 5
        Assert.Equal(5 * WinMs, s.StartMs);
        // 5 pre-roll + 20 speech + 5 trailing pad = 30 windows
        Assert.Equal(30 * Win, s.Pcm.Length);
        Assert.Equal(s.StartMs + 30 * WinMs, s.EndMs);
    }

    [Fact]
    public void Short_blip_below_minSpeech_is_dropped()
    {
        // 4 speech windows (128 ms) < minSpeech (250 ms -> 8 windows)
        var vad = Sut(i => i is >= 10 and < 14 ? 0.9f : 0.1f);
        Assert.Empty(PushWindows(vad, 60));
        Assert.Null(vad.Flush());
    }

    [Fact]
    public void Silence_only_emits_nothing()
    {
        var vad = Sut(_ => 0.05f);
        Assert.Empty(PushWindows(vad, 100));
        Assert.Null(vad.Flush());
    }

    [Fact]
    public void Long_monologue_is_force_cut_at_max_and_continues()
    {
        // continuous speech for 600 windows (19.2 s) then silence
        var vad = Sut(i => i < 600 ? 0.9f : 0.1f);
        var segs = PushWindows(vad, 700);

        Assert.Equal(2, segs.Count);
        // first segment hard-cut at maxSegment (no dip existed): 468 windows
        Assert.Equal(468 * Win, segs[0].Pcm.Length);
        // remainder continues seamlessly: second starts where the first ended
        Assert.Equal(segs[0].EndMs, segs[1].StartMs);
    }

    [Fact]
    public void Flush_force_emits_in_progress_utterance()
    {
        // speech from window 10, stream ends mid-utterance
        var vad = Sut(i => i >= 10 ? 0.9f : 0.1f);
        Assert.Empty(PushWindows(vad, 40));           // still in speech, nothing final
        var s = vad.Flush();
        Assert.NotNull(s);
        Assert.Equal(5 * WinMs, s!.StartMs);          // leading pad honored
        Assert.Equal(35 * Win, s.Pcm.Length);         // 5 pre-roll + 30 speech windows
    }

    [Fact]
    public void Anchor_comes_from_first_frame_startMs()
    {
        var vad = Sut(i => i is >= 1 and < 12 ? 0.9f : 0.1f);
        var segs = PushWindows(vad, 40, startMs: 100_000);
        var s = Assert.Single(segs);
        Assert.Equal(100_000 + 0 * WinMs, s.StartMs); // onset at window 1; pre-roll has 1 window -> starts at window 0
    }

    [Fact]
    public void Partial_frames_are_carried_across_pushes()
    {
        // Two frames of 1.5 windows each -> 3 whole windows total processed.
        var vad = Sut(_ => 0.9f);
        vad.Push(new AudioFrame(SourceKind.Local, 0, new float[Win + Win / 2]));
        vad.Push(new AudioFrame(SourceKind.Local, 48, new float[Win + Win / 2]));
        var s = vad.Flush();
        Assert.NotNull(s);
        Assert.Equal(3 * Win, s!.Pcm.Length);
    }
}
