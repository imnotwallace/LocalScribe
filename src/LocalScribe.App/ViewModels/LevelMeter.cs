using CommunityToolkit.Mvvm.ComponentModel;
namespace LocalScribe.App.ViewModels;

/// <summary>Audio-present bar state: per-frame peaks push it up (with gain so normal speech
/// fills the bar), Tick() decays it toward zero within about a second of silence. WPF-free.</summary>
public sealed partial class LevelMeter : ObservableObject
{
    [ObservableProperty] private double _value;

    public void Observe(float peak)
        => Value = Math.Max(Value, Math.Min(1.0, peak * 3.0));

    public void Tick()
    {
        double next = Value * 0.7;
        Value = next < 0.01 ? 0 : next;
    }
}
