using System;
using System.Diagnostics;
using System.Windows.Controls;
using System.Windows.Media;
namespace LocalScribe.App;

/// <summary>UX round 2026-08-03 item A: the easing math behind the sync-transcript follow
/// glide (ReadViewWindow.ScrollRowToUpperThird), pulled out as a pure static class with NO WPF
/// references so it is unit-testable without an STA harness (ScrollGlide below, which drives an
/// actual ScrollViewer off CompositionTarget.Rendering, cannot be).</summary>
public static class ScrollEasing
{
    /// <summary>Cubic ease-out: fast start, gentle settle at the destination. t is clamped to
    /// [0,1] so a caller passing elapsed/duration slightly past 1.0 on a delayed final frame (or
    /// slightly below 0 before the first tick) still lands exactly on the curve's endpoint
    /// instead of overshooting past the destination offset.</summary>
    public static double EaseOutCubic(double t)
    {
        t = Math.Clamp(t, 0.0, 1.0);
        return 1 - Math.Pow(1 - t, 3);
    }

    /// <summary>Distance-scaled, capped duration: a one-row nudge glides in ~180ms, a big seek's
    /// centering leg never exceeds 400ms no matter how far it travels. Math.Abs makes duration
    /// depend on distance alone, never direction, so an upward scroll (an earlier row) takes the
    /// same time as the same-magnitude downward one.</summary>
    public static TimeSpan DurationFor(double distancePx)
        => TimeSpan.FromMilliseconds(Math.Clamp(140 + 0.35 * Math.Abs(distancePx), 180, 400));
}

/// <summary>The frame pump behind the glide: animates a ScrollViewer's vertical offset from its
/// CURRENT position to a target over ScrollEasing.DurationFor(distance), eased by
/// ScrollEasing.EaseOutCubic, driven off CompositionTarget.Rendering (WPF's per-frame render
/// tick) timed with a Stopwatch rather than a DispatcherTimer so the glide tracks actual elapsed
/// wall-clock time even if a frame is skipped under load. View-layer only (references
/// ScrollViewer) - not unit-tested here per the item A design; the build gate plus manual smoke
/// are this type's verification.</summary>
public sealed class ScrollGlide
{
    private readonly Stopwatch _stopwatch = new();
    private ScrollViewer? _target;
    private double _from;
    private double _to;
    private TimeSpan _duration;
    private Action? _onFinished;

    /// <summary>True while a glide is in flight. ReadViewWindow does not currently read this
    /// (the guard flag it drives is _programmaticFollowScroll, set independently by the caller),
    /// but it is part of the approved shape so a future caller can query in-flight state without
    /// this type growing a second way to ask the same question.</summary>
    public bool IsRunning { get; private set; }

    /// <summary>Starts (or retargets) a glide toward toOffset. Calling Start again while already
    /// running RETARGETS smoothly: `from` is re-captured as target's CURRENT (mid-flight)
    /// VerticalOffset, so the row doesn't jump back to the original start point first. The prior
    /// flight is stopped via the ordinary public Cancel() below - including firing ITS
    /// onFinished, exactly like any other stop. An earlier revision of this type special-cased
    /// retargeting to swallow the stale onFinished (to avoid it releasing the caller's guard flag
    /// out from under the new flight), but that guard-release race is now closed on the CALLER's
    /// side instead: ReadViewWindow.GlideTo stamps every deferred release with a generation token
    /// and checks it before clearing the flag, so a stale release - from a retarget, or from an
    /// externally-Cancel()'d flight that a fresh Start() supersedes before the release runs - is
    /// simply a no-op. That fix is general (it also covers Cancel() racing a subsequent Start(),
    /// which swallowing-on-retarget alone never did), so ScrollGlide itself can stay simple: ONE
    /// rule - stopping always fires onFinished - rather than two stop paths with different
    /// contracts.</summary>
    public void Start(ScrollViewer target, double toOffset, Action onFinished)
    {
        Cancel();
        _target = target;
        _from = target.VerticalOffset;
        _to = toOffset;
        _duration = ScrollEasing.DurationFor(toOffset - _from);
        _onFinished = onFinished;
        IsRunning = true;
        _stopwatch.Restart();
        CompositionTarget.Rendering += OnRendering;
    }

    /// <summary>Hard stop: unhooks the frame pump AND fires onFinished, so a caller's guard flag
    /// is never left stranded true by a glide that was aborted rather than left to finish (the
    /// user grabbing the scrollbar mid-glide, the window closing, or Start() retargeting over
    /// this flight). A no-op when nothing is running - there is no in-flight onFinished to
    /// release.</summary>
    public void Cancel()
    {
        if (!IsRunning) return;
        Finish();
    }

    private void OnRendering(object? sender, EventArgs e)
    {
        double t = _stopwatch.Elapsed.TotalMilliseconds / _duration.TotalMilliseconds;
        if (t >= 1.0)
        {
            _target!.ScrollToVerticalOffset(_to);          // land exactly on target, not 1-epsilon short
            Finish();
            return;
        }
        _target!.ScrollToVerticalOffset(_from + (_to - _from) * ScrollEasing.EaseOutCubic(t));
    }

    /// <summary>Shared by Cancel and natural completion: unhook CompositionTarget.Rendering (a
    /// leaked handler is a per-frame cost for the life of the app) and fire onFinished exactly
    /// once. Callers never see IsRunning true again until the next Start.</summary>
    private void Finish()
    {
        CompositionTarget.Rendering -= OnRendering;
        IsRunning = false;
        var onFinished = _onFinished;
        _onFinished = null;
        onFinished?.Invoke();
    }
}
