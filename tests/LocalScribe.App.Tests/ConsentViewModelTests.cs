using LocalScribe.App.ViewModels;
using LocalScribe.Core.Tests;
using Xunit;

namespace LocalScribe.App.Tests;

public sealed class ConsentViewModelTests
{
    private static readonly DateTimeOffset Now = new(2026, 7, 3, 4, 0, 0, TimeSpan.Zero);

    private static (ConsentViewModel Vm, FakeSettingsService Settings, List<bool> Closed) Make()
    {
        var settings = new FakeSettingsService();
        var vm = new ConsentViewModel(settings, new ManualUtcTimeProvider(Now), "0.4.0");
        var closed = new List<bool>();
        vm.Closed += closed.Add;
        return (vm, settings, closed);
    }

    [Fact]
    public async Task Accept_persists_time_and_version_then_closes_accepted()
    {
        var (vm, settings, closed) = Make();
        Assert.Null(settings.Current.ConsentNotice);            // fresh install: notice due

        await vm.AcceptCommand.ExecuteAsync(null);

        Assert.NotNull(settings.Current.ConsentNotice);
        Assert.Equal(Now, settings.Current.ConsentNotice!.AcknowledgedAtUtc);
        Assert.Equal("0.4.0", settings.Current.ConsentNotice.AppVersion);
        Assert.Equal(new[] { true }, closed);
        // "Never shown again": the App gate is exactly ConsentNotice != null on the next launch.
        Assert.True(settings.Current.ConsentNotice is not null);
    }

    [Fact]
    public void Decline_persists_nothing_and_closes_declined()
    {
        var (vm, settings, closed) = Make();
        vm.DeclineCommand.Execute(null);
        Assert.Equal(0, settings.SaveCount);
        Assert.Null(settings.Current.ConsentNotice);            // next launch shows the notice again
        Assert.Equal(new[] { false }, closed);
    }

    [Fact]
    public void Text_carries_the_local_summary_and_the_legal_responsibility_statement()
    {
        var (vm, _, _) = Make();
        Assert.Contains("never leave your computer", vm.SummaryText);
        Assert.Contains("Recording others is your responsibility", vm.ResponsibilityText);
        Assert.Contains("two-party / all-party consent", vm.ResponsibilityText);
        Assert.Contains("disclosing the recording to the other participants is up to you",
            vm.ResponsibilityText);
    }
}
