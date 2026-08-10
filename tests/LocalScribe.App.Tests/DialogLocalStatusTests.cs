using LocalScribe.App.ViewModels;
using Xunit;

namespace LocalScribe.App.Tests;

/// <summary>The dialog-local status pair on the import and re-transcribe dialogs (Tier 1 plan D,
/// T1-5, 2026-08-05). Both routed every outcome to MainWindow's InfoBar, which a separate modal
/// cannot show. These facts pin only the OBSERVABLE contract the two windows bind to - the
/// end-to-end runs live in ImportDialogViewModelTests and RetranscribeDialogViewModelTests, which
/// already own the heavy fixtures.</summary>
public sealed class DialogLocalStatusTests
{
    [Fact]
    public void Import_status_starts_empty_and_HasStatus_follows_the_message()
    {
        var vm = new ImportDialogViewModel(
            new NullDecoder(), (req, p, tp, dp, confirm, ct) => Task.FromResult("s1"),
            maintenance: null!, availableModels: () => new HashSet<string> { "base.en" },
            pickOpenPath: _ => null, confirmMismatch: _ => Task.FromResult(true),
            new FakeUiErrorReporter(), dispatch: a => a(), TimeProvider.System);

        Assert.False(vm.HasStatus);
        Assert.Null(vm.StatusMessage);

        vm.ShowStatus("Imported \"Doe intake\".", isError: false);
        Assert.True(vm.HasStatus);
        Assert.False(vm.StatusIsError);

        vm.ShowStatus("ffmpeg is not installed.", isError: true);
        Assert.True(vm.StatusIsError);
    }

    private sealed class NullDecoder : LocalScribe.Core.Import.IAudioDecoder
    {
        public Task<LocalScribe.Core.Import.AudioProbeResult> ProbeAsync(string path, CancellationToken ct)
            => Task.FromResult(new LocalScribe.Core.Import.AudioProbeResult());
        public Task<LocalScribe.Core.Import.DecodedAudio> DecodeAsync(string path,
            LocalScribe.Core.Import.AudioProbeResult probe, string workDir, CancellationToken ct)
            => throw new NotSupportedException("this fact never decodes");
    }
}
