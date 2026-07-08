using LocalScribe.Core.Live;
using Xunit;

namespace LocalScribe.Core.Tests;

public class CaptureDeviceEnumeratorTests
{
    [Fact]
    public void AudioDeviceInfo_CarriesIdAndName()
    {
        var d = new AudioDeviceInfo("{0.0.1.00000000}.{guid}", "Headset Microphone");
        Assert.Equal("{0.0.1.00000000}.{guid}", d.Id);
        Assert.Equal("Headset Microphone", d.Name);
    }

    [Fact]
    public void FakeEnumerator_ReturnsSeededDevices()
    {
        var fake = new FakeCaptureDeviceEnumerator(
            new AudioDeviceInfo("id-1", "Mic One"),
            new AudioDeviceInfo("id-2", "Mic Two"));
        var list = fake.ListInputDevices();
        Assert.Equal(2, list.Count);
        Assert.Equal("Mic Two", list[1].Name);
    }
}
