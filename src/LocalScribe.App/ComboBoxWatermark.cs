using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Documents;
using System.Windows.Media;
namespace LocalScribe.App;

/// <summary>Attached watermark for EDITABLE ComboBoxes (UX round 2026-08-02 item 3.9). WPF's
/// ComboBox has no PlaceholderText and Wpf.Ui does not add one, so an empty free-text combo
/// (Settings > Per-app target) painted a blank box that read as a bug - and a real default
/// value would be wrong there. The watermark is an adorner shown only while ComboBox.Text is
/// empty; it is hit-test-invisible and never takes focus, so typing and selection behaviour
/// are untouched. Attached-behavior pattern: SegmentText.cs (the VM stays WPF-free).</summary>
public static class ComboBoxWatermark
{
    public static readonly DependencyProperty TextProperty = DependencyProperty.RegisterAttached(
        "Text", typeof(string), typeof(ComboBoxWatermark), new PropertyMetadata(null, OnTextChanged));
    public static void SetText(DependencyObject o, string? v) => o.SetValue(TextProperty, v);
    public static string? GetText(DependencyObject o) => (string?)o.GetValue(TextProperty);

    private sealed class StoredAdorner
    {
        public required AdornerLayer Layer { get; set; }
        public required WatermarkAdorner Adorner { get; set; }
    }

    // Store layer reference at add time: Unloaded fires after the ancestor walk is severed,
    // so GetAdornerLayer(combo) would return null. Captured at Add, reused at Remove.
    private static readonly ConditionalWeakTable<ComboBox, StoredAdorner> _adorners = new();

    private static void OnTextChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not ComboBox combo) return;
        // Idempotent re-wire: remove-then-add so a re-applied style never double-subscribes.
        combo.Loaded -= OnLoaded;
        combo.Loaded += OnLoaded;
        combo.Unloaded -= OnUnloaded;
        combo.Unloaded += OnUnloaded;
        combo.RemoveHandler(TextBoxBase.TextChangedEvent, (TextChangedEventHandler)OnEditTextChanged);
        combo.AddHandler(TextBoxBase.TextChangedEvent, (TextChangedEventHandler)OnEditTextChanged);
        if (combo.IsLoaded) Update(combo);
    }

    private static void OnLoaded(object sender, RoutedEventArgs e) => Update((ComboBox)sender);

    private static void OnUnloaded(object sender, RoutedEventArgs e)
    {
        if (sender is not ComboBox combo) return;
        if (_adorners.TryGetValue(combo, out var stored))
        {
            stored.Layer.Remove(stored.Adorner);
            _adorners.Remove(combo);
        }
    }

    // TextBoxBase.TextChanged bubbles up from the template's PART_EditableTextBox - no template
    // walking needed, and it fires for typing, suggestion picks, and programmatic Text sets alike.
    private static void OnEditTextChanged(object sender, TextChangedEventArgs e) => Update((ComboBox)sender);

    private static void Update(ComboBox combo)
    {
        bool wanted = string.IsNullOrEmpty(combo.Text) && GetText(combo) is { Length: > 0 };
        if (_adorners.TryGetValue(combo, out var stored))
        {
            if (wanted) return;                         // Already have the right adorner
            stored.Layer.Remove(stored.Adorner);
            _adorners.Remove(combo);
        }
        if (wanted)
        {
            var layer = AdornerLayer.GetAdornerLayer(combo);
            if (layer is null) return;                    // not in a visual tree yet
            var adorner = new WatermarkAdorner(combo, GetText(combo)!);
            layer.Add(adorner);
            _adorners.Add(combo, new StoredAdorner { Layer = layer, Adorner = adorner });
        }
    }

    /// <summary>Muted, hit-test-invisible label over the combo's text area. Inherits the combo's
    /// Foreground so it stays legible in both themes (no ARGB literals - house XAML hygiene).</summary>
    private sealed class WatermarkAdorner : Adorner
    {
        private readonly TextBlock _label;

        public WatermarkAdorner(ComboBox adorned, string text) : base(adorned)
        {
            IsHitTestVisible = false;
            _label = new TextBlock
            {
                Text = text,
                Opacity = 0.6,
                Foreground = adorned.Foreground,
                VerticalAlignment = VerticalAlignment.Center,
                TextTrimming = TextTrimming.CharacterEllipsis,
                // Left edge aligns with the editable text box's caret; right leaves the
                // drop-down button clear.
                Margin = new Thickness(10, 0, 30, 0),
            };
            AddVisualChild(_label);
        }

        protected override int VisualChildrenCount => 1;
        protected override Visual GetVisualChild(int index) => _label;

        protected override Size MeasureOverride(Size constraint)
        {
            _label.Measure(constraint);
            return AdornedElement.RenderSize;
        }

        protected override Size ArrangeOverride(Size finalSize)
        {
            _label.Arrange(new Rect(finalSize));
            return finalSize;
        }
    }
}
