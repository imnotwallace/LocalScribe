using System.Collections.Generic;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using LocalScribe.App.ViewModels;
using LocalScribe.Core.Projection;
namespace LocalScribe.App;

/// <summary>Attached behavior that renders a read-view speaker turn as one interactive inline per
/// segment (ITEM 5, 2026-08-01): hover shows the segment's [mm:ss], double-click seeks to it, and
/// the segment under the playhead is tinted. Owns the target TextBlock's Inlines. Empty/null
/// Segments -> a single plain Run of FallbackText (markers, live rows), preserving today's look.
/// Recycling-safe: rebuilds and re-subscribes whenever Segments/FallbackText change (ListView
/// container reuse re-sets the bindings), tearing down old PropertyChanged handlers first.</summary>
public static class SegmentText
{
    public static readonly DependencyProperty SegmentsProperty = DependencyProperty.RegisterAttached(
        "Segments", typeof(IReadOnlyList<ReadSegment>), typeof(SegmentText),
        new PropertyMetadata(null, OnChanged));
    public static void SetSegments(DependencyObject o, IReadOnlyList<ReadSegment>? v) => o.SetValue(SegmentsProperty, v);
    public static IReadOnlyList<ReadSegment>? GetSegments(DependencyObject o) => (IReadOnlyList<ReadSegment>?)o.GetValue(SegmentsProperty);

    public static readonly DependencyProperty FallbackTextProperty = DependencyProperty.RegisterAttached(
        "FallbackText", typeof(string), typeof(SegmentText), new PropertyMetadata(null, OnChanged));
    public static void SetFallbackText(DependencyObject o, string? v) => o.SetValue(FallbackTextProperty, v);
    public static string? GetFallbackText(DependencyObject o) => (string?)o.GetValue(FallbackTextProperty);

    public static readonly DependencyProperty SeekCommandProperty = DependencyProperty.RegisterAttached(
        "SeekCommand", typeof(ICommand), typeof(SegmentText), new PropertyMetadata(null));
    public static void SetSeekCommand(DependencyObject o, ICommand? v) => o.SetValue(SeekCommandProperty, v);
    public static ICommand? GetSeekCommand(DependencyObject o) => (ICommand?)o.GetValue(SeekCommandProperty);

    private sealed class Bindings
    {
        public readonly List<(ReadSegment Seg, Run Run, PropertyChangedEventHandler Handler)> Items = new();
    }
    private static readonly ConditionalWeakTable<TextBlock, Bindings> _state = new();

    private static void OnChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is TextBlock tb) Rebuild(tb);
    }

    private static void Rebuild(TextBlock tb)
    {
        if (_state.TryGetValue(tb, out var old))
        {
            foreach (var (seg, _, handler) in old.Items) seg.PropertyChanged -= handler;
            _state.Remove(tb);
        }
        tb.Inlines.Clear();

        var segments = GetSegments(tb);
        if (segments is null || segments.Count == 0)
        {
            tb.Inlines.Add(new Run(GetFallbackText(tb) ?? string.Empty));
            return;
        }

        var brush = NowPlayingBrush();
        var bindings = new Bindings();
        foreach (var seg in segments)
        {
            var run = new Run(seg.Text + " ") { Cursor = Cursors.Hand };
            string stamp = TimestampFormat.Stamp(seg.StartMs, "relative", default);
            run.ToolTip = seg.IsEstimatedStart ? $"~[{stamp}] (estimated)" : $"[{stamp}]";
            if (seg.IsNowPlaying) run.Background = brush;

            var captured = seg;
            var capturedRun = run;
            // Preview (tunneling) so this beats the ListViewItem's own double-click (JumpToSection).
            run.PreviewMouseLeftButtonDown += (_, args) =>
            {
                if (args.ClickCount != 2) return;
                var cmd = GetSeekCommand(tb);
                if (cmd is not null && cmd.CanExecute(captured.StartMs)) cmd.Execute(captured.StartMs);
                args.Handled = true;
            };
            PropertyChangedEventHandler handler = (_, args) =>
            {
                if (args.PropertyName == nameof(ReadSegment.IsNowPlaying))
                    capturedRun.Background = captured.IsNowPlaying ? brush : null;
            };
            captured.PropertyChanged += handler;

            tb.Inlines.Add(run);
            bindings.Items.Add((captured, capturedRun, handler));
        }
        _state.Add(tb, bindings);
    }

    // Theme accent at the same hue as the row's now-playing trigger, built per rebuild so it tracks
    // the current theme at container-realization time. XamlHygiene: color comes from the resource,
    // never an ARGB literal.
    private static Brush NowPlayingBrush()
    {
        if (Application.Current?.TryFindResource("SystemAccentColor") is Color c)
            return new SolidColorBrush(c) { Opacity = 0.40 };
        return Brushes.Transparent;
    }
}
