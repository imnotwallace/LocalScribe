using System.IO;
using LocalScribe.App.ViewModels;
using LocalScribe.Core.Live;
using LocalScribe.Core.Model;
using LocalScribe.Core.Tests;
using Xunit;

namespace LocalScribe.App.Tests;

/// <summary>A persistent notice surface on the live session VM (Tier 1 plan D, T1-5,
/// 2026-08-05). Before this, a failed StartAsync threw out of the AsyncRelayCommand into the
/// dispatcher handler that swallowed everything, and the only live notice surface in the product
/// was a tray balloon Focus Assist suppresses. LastNotice existed but was bound in no XAML at
/// all.</summary>
public sealed class SessionNoticeTests : IDisposable
{
    private readonly string _root =
        Path.Combine(Path.GetTempPath(), "ls-sessnotice-" + Guid.NewGuid().ToString("N"));
    public void Dispose() { try { Directory.Delete(_root, recursive: true); } catch { } }

    [Fact]
    public async Task A_failed_start_is_surfaced_instead_of_thrown()
    {
        var (controller, provider, _, _) = LiveTestDoubles.MakeController(_root);
        provider.ThrowOnNextMicCreate = true;      // one-shot: CreateMic throws "mic gone"
        var vm = new SessionViewModel(controller, new Settings { StorageRoot = _root },
            dispatch: a => a(), startOptions: LiveTestDoubles.Options());

        await vm.StartCommand.ExecuteAsync(null);   // must NOT throw out of the command

        Assert.True(vm.HasNotice);
        Assert.True(vm.NoticeIsError);
        Assert.Contains("mic gone", vm.NoticeText);
        Assert.Equal(SessionState.Idle, vm.State);
    }

    [Fact]
    public void A_repeated_identical_notice_re_opens_a_dismissed_bar()
    {
        // THE trap: [ObservableProperty] gates PropertyChanged on equality, so a naive bar bound
        // to a same-valued property would stay shut on a repeat. This is why NoticeRaised exists
        // at all, and why RaiseNotice nulls the text first.
        var (controller, _, _, _) = LiveTestDoubles.MakeController(_root);
        var vm = new SessionViewModel(controller, new Settings { StorageRoot = _root },
            dispatch: a => a(), startOptions: LiveTestDoubles.Options());
        int opens = 0;
        vm.PropertyChanged += (_, e) => { if (e.PropertyName == nameof(vm.HasNotice)) opens++; };

        vm.RaiseNotice("Recording is degraded to the system mix.", isError: false);
        vm.DismissNoticeCommand.Execute(null);
        vm.RaiseNotice("Recording is degraded to the system mix.", isError: false);

        Assert.True(vm.HasNotice);                 // the SAME text re-opened the bar
        Assert.True(opens >= 3);                   // open, dismiss, re-open
    }

    [Fact]
    public void Dismiss_clears_both_halves_so_the_next_notice_starts_clean()
    {
        var (controller, _, _, _) = LiveTestDoubles.MakeController(_root);
        var vm = new SessionViewModel(controller, new Settings { StorageRoot = _root },
            dispatch: a => a(), startOptions: LiveTestDoubles.Options());

        vm.RaiseNotice("Couldn't start recording: mic gone", isError: true);
        vm.DismissNoticeCommand.Execute(null);

        Assert.False(vm.HasNotice);
        Assert.False(vm.NoticeIsError);
    }
}
