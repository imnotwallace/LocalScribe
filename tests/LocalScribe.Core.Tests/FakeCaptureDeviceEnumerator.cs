using LocalScribe.Core.Live;

namespace LocalScribe.Core.Tests;

/// <summary>Deterministic ICaptureDeviceEnumerator for planner + VM tests. Seed a fixed device
/// list (the production enumerator swallows an enumeration failure into an empty list; a VM
/// under test can assert the empty-list path directly by seeding no devices). Shared by
/// Core.Tests and App.Tests (App.Tests references this namespace).</summary>
public sealed class FakeCaptureDeviceEnumerator : ICaptureDeviceEnumerator
{
    private readonly IReadOnlyList<AudioDeviceInfo> _devices;
    public FakeCaptureDeviceEnumerator(params AudioDeviceInfo[] devices) => _devices = devices;
    public IReadOnlyList<AudioDeviceInfo> ListInputDevices() => _devices;
}
