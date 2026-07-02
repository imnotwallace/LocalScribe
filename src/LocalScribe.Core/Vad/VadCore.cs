using LocalScribe.Core.Audio;
using LocalScribe.Core.Pipeline;
namespace LocalScribe.Core.Vad;

/// <summary>Pure spec section 4 utterance segmentation over 512-sample windows. Per source,
/// speaker-count-agnostic. All times derive from the first frame's StartMs anchor plus
/// a window counter (32 ms per window @ 16 kHz) - no wall clock.</summary>
public sealed class VadCore
{
    private readonly SourceKind _source;
    private readonly VadOptions _o;
    private readonly ISpeechProbabilityModel _model;
    private readonly int _padWin, _minSilenceWin, _minSpeechWin, _maxWin;
    private readonly long _winMs;

    private readonly List<float> _carry = new();          // partial-window carry between pushes
    private readonly Queue<float[]> _preRoll = new();     // rolling pad windows while idle
    private readonly List<float[]> _current = new();      // padded in-progress utterance
    private long _anchorMs = long.MinValue;               // stream time of window index 0
    private long _windowIndex;                            // absolute processed-window counter
    private long _utteranceStartWin;                      // absolute index of first padded window
    private int _speechWin;                               // windows >= threshold in current utterance
    private int _silenceRun;                              // consecutive sub-threshold windows (in speech)
    private int _lastDipIndex = -1;                       // index into _current of last sub-threshold window

    public VadCore(SourceKind source, VadOptions options, ISpeechProbabilityModel model)
    {
        (_source, _o, _model) = (source, options, model);
        _winMs = 1000L * _o.WindowSizeSamples / _o.SampleRate;
        _padWin = (int)Math.Ceiling(_o.SpeechPadMs / (double)_winMs);
        _minSilenceWin = (int)Math.Ceiling(_o.MinSilenceMs / (double)_winMs);
        _minSpeechWin = (int)Math.Ceiling(_o.MinSpeechMs / (double)_winMs);
        _maxWin = (int)(_o.MaxSegmentMs / _winMs);
        _model.Reset();
    }

    private bool InSpeech => _current.Count > 0;

    public IReadOnlyList<AudioSegment> Push(AudioFrame frame)
    {
        if (_anchorMs == long.MinValue) _anchorMs = frame.StartMs;
        _carry.AddRange(frame.Samples);

        List<AudioSegment>? emitted = null;
        while (_carry.Count >= _o.WindowSizeSamples)
        {
            var win = _carry.GetRange(0, _o.WindowSizeSamples).ToArray();
            _carry.RemoveRange(0, _o.WindowSizeSamples);
            var seg = ProcessWindow(win);
            if (seg is not null) (emitted ??= new()).Add(seg);
        }
        return (IReadOnlyList<AudioSegment>?)emitted ?? Array.Empty<AudioSegment>();
    }

    /// <summary>Force-emits the in-progress utterance on Stop/Pause/EOF regardless of length
    /// (user decision 2026-07-02: never silently drop trailing audio; the minSpeech blip-drop
    /// applies only to mid-stream silence-ends). Null when no utterance is in progress.
    /// Resets all state either way.</summary>
    public AudioSegment? Flush()
    {
        AudioSegment? seg = null;
        if (InSpeech)
            seg = Emit(_current.Count);
        ResetUtterance(clearPreRoll: true);
        _model.Reset();
        _anchorMs = long.MinValue;
        _windowIndex = 0;
        _carry.Clear();
        return seg;
    }

    private AudioSegment? ProcessWindow(float[] win)
    {
        float p = _model.SpeechProbability(win);
        AudioSegment? emitted = null;

        if (!InSpeech)
        {
            if (p >= _o.Threshold)
            {
                _current.AddRange(_preRoll);                       // leading pad (spec 4)
                _utteranceStartWin = _windowIndex - _preRoll.Count;
                _preRoll.Clear();
                _current.Add(win);
                _speechWin = 1;
                _silenceRun = 0;
                _lastDipIndex = -1;
            }
            else
            {
                _preRoll.Enqueue(win);
                while (_preRoll.Count > _padWin) _preRoll.Dequeue();
            }
        }
        else
        {
            _current.Add(win);
            if (p >= _o.Threshold)
            {
                _speechWin++;
                _silenceRun = 0;
            }
            else
            {
                _silenceRun++;
                _lastDipIndex = _current.Count - 1;
                if (_silenceRun >= _minSilenceWin)
                {
                    // end of utterance: keep pad windows of the silence tail, drop the rest
                    int keep = _current.Count - (_silenceRun - _padWin);
                    emitted = _speechWin >= _minSpeechWin ? Emit(keep) : null;
                    ResetUtterance(clearPreRoll: false);
                }
            }

            if (InSpeech && _current.Count >= _maxWin)
                emitted = ForceCut() ?? emitted;
        }

        _windowIndex++;
        return emitted;
    }

    private AudioSegment? ForceCut()
    {
        // cut at the last dip if one exists, else hard cut at max (spec 4)
        int cut = _lastDipIndex > 0 ? _lastDipIndex : _current.Count;
        var seg = _speechWin >= _minSpeechWin ? Emit(cut) : null;

        // remainder seeds the next in-progress utterance, still in speech
        var remainder = _current.GetRange(cut, _current.Count - cut);
        long remainderStart = _utteranceStartWin + cut;
        ResetUtterance(clearPreRoll: false);
        if (remainder.Count > 0)
        {
            _current.AddRange(remainder);
            _utteranceStartWin = remainderStart;
            _speechWin = remainder.Count;              // conservative: treat carried windows as speech
        }
        return seg;
    }

    private AudioSegment Emit(int windowCount)
    {
        windowCount = Math.Min(windowCount, _current.Count);
        var pcm = new float[windowCount * _o.WindowSizeSamples];
        for (int i = 0; i < windowCount; i++)
            _current[i].CopyTo(pcm, i * _o.WindowSizeSamples);
        long startMs = _anchorMs + _utteranceStartWin * _winMs;
        long endMs = startMs + windowCount * _winMs;
        return new AudioSegment(_source, startMs, endMs, pcm);
    }

    private void ResetUtterance(bool clearPreRoll)
    {
        _current.Clear();
        _speechWin = 0;
        _silenceRun = 0;
        _lastDipIndex = -1;
        if (clearPreRoll) _preRoll.Clear();
    }
}
