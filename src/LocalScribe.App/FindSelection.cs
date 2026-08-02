// src/LocalScribe.App/FindSelection.cs
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using LocalScribe.App.ViewModels;
namespace LocalScribe.App;

/// <summary>Attached behavior that turns a segment VM's one-shot FindSelection request into
/// TextBox.Select + Focus (item 1, UX round 2026-08-02) - the SegmentText pattern, so the VM
/// stays WPF-free. Tolerates unrealized containers: a request stamped before the virtualized
/// EditList realized this row is applied on Loaded/DataContextChanged instead of being lost.
/// One-shot: the request is cleared after applying, so a recycled container scrolling back into
/// view can never steal focus again. Recycling-safe: DataContextChanged re-points the segment
/// subscription, tearing down the old handler first (ConditionalWeakTable, as SegmentText).</summary>
public static class FindSelection
{
    public static readonly DependencyProperty EnableProperty = DependencyProperty.RegisterAttached(
        "Enable", typeof(bool), typeof(FindSelection), new PropertyMetadata(false, OnEnableChanged));
    public static void SetEnable(DependencyObject o, bool v) => o.SetValue(EnableProperty, v);
    public static bool GetEnable(DependencyObject o) => (bool)o.GetValue(EnableProperty);

    private sealed class Hook
    {
        public EditableSegmentViewModel? Segment;
        public PropertyChangedEventHandler? Handler;
    }
    private static readonly ConditionalWeakTable<TextBox, Hook> _state = new();

    private static void OnEnableChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not TextBox tb) return;
        if ((bool)e.NewValue)
        {
            tb.DataContextChanged += OnDataContextChanged;
            tb.Loaded += OnLoaded;
            Attach(tb, tb.DataContext as EditableSegmentViewModel);
        }
        else
        {
            tb.DataContextChanged -= OnDataContextChanged;
            tb.Loaded -= OnLoaded;
            Attach(tb, null);
        }
    }

    private static void OnDataContextChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (sender is TextBox tb) Attach(tb, e.NewValue as EditableSegmentViewModel);
    }

    private static void OnLoaded(object sender, RoutedEventArgs e)
    {
        // A container realized AFTER the navigation stamped the request (virtualized list).
        if (sender is TextBox tb && tb.DataContext is EditableSegmentViewModel seg) Apply(tb, seg);
    }

    private static void Attach(TextBox tb, EditableSegmentViewModel? seg)
    {
        if (_state.TryGetValue(tb, out var old))
        {
            if (old.Segment is not null && old.Handler is not null)
                old.Segment.PropertyChanged -= old.Handler;
            _state.Remove(tb);
        }
        if (seg is null) return;
        PropertyChangedEventHandler handler = (_, args) =>
        {
            if (args.PropertyName == nameof(EditableSegmentViewModel.FindSelectionStart))
                Apply(tb, seg);
        };
        seg.PropertyChanged += handler;
        _state.Add(tb, new Hook { Segment = seg, Handler = handler });
        Apply(tb, seg);   // a request stamped before this container existed
    }

    /// <summary>Deferred so it runs after the expand/scroll layout pass; re-reads the request at
    /// apply time (a newer navigation may have moved or cleared it), then clears it (one-shot).</summary>
    private static void Apply(TextBox tb, EditableSegmentViewModel seg)
    {
        if (seg.FindSelectionStart < 0) return;
        tb.Dispatcher.BeginInvoke(DispatcherPriority.Loaded, () =>
        {
            int start = seg.FindSelectionStart;
            if (start < 0) return;                              // superseded meanwhile
            if (!ReferenceEquals(tb.DataContext, seg)) return;   // container recycled meanwhile
            int clampedStart = Math.Min(start, tb.Text.Length);
            int len = Math.Max(0, Math.Min(seg.FindSelectionLength, tb.Text.Length - clampedStart));
            tb.Focus();
            tb.Select(clampedStart, len);
            tb.BringIntoView();
            seg.ClearFindSelection();
        });
    }
}
