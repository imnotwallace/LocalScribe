using LocalScribe.App.Services;
using LocalScribe.App.ViewModels;
using LocalScribe.Core.Model;
using Xunit;

namespace LocalScribe.App.Tests;

public sealed class VocabularyEditorViewModelTests
{
    private readonly FakeReporter _reporter = new();
    private Vocabulary _persisted = new();
    private int _persistCount;

    private VocabularyEditorViewModel MakeVm()
        => new((v, ct) => { _persisted = v; _persistCount++; return Task.CompletedTask; }, _reporter);

    [Fact]
    public async Task Add_term_persists_the_rebuilt_vocabulary()
    {
        var vm = MakeVm();
        vm.NewTerm = "arraignment";
        await vm.AddTermCommand.ExecuteAsync(null);

        Assert.Contains(vm.Terms, r => r.Text == "arraignment");
        Assert.Equal(new[] { "arraignment" }, _persisted.Terms);
        Assert.Equal("", vm.NewTerm);                     // input cleared
    }

    [Fact]
    public async Task Add_blank_term_is_rejected_without_persisting()
    {
        var vm = MakeVm();
        vm.NewTerm = "   ";
        await vm.AddTermCommand.ExecuteAsync(null);

        Assert.Empty(vm.Terms);
        Assert.Equal(0, _persistCount);
        Assert.NotEmpty(_reporter.Infos);                 // validation surfaced, not silent
    }

    [Fact]
    public async Task Add_case_insensitive_duplicate_term_is_rejected()
    {
        var vm = MakeVm();
        vm.NewTerm = "Auth";
        await vm.AddTermCommand.ExecuteAsync(null);
        vm.NewTerm = "auth";
        await vm.AddTermCommand.ExecuteAsync(null);

        Assert.Single(vm.Terms);                          // OrdinalIgnoreCase collapse
        Assert.NotEmpty(_reporter.Infos);
    }

    [Fact]
    public async Task Remove_term_persists()
    {
        var vm = MakeVm();
        vm.Load(new Vocabulary { Terms = new[] { "a", "b" } });
        await vm.RemoveTermCommand.ExecuteAsync(vm.Terms.First(r => r.Text == "a"));

        Assert.DoesNotContain(vm.Terms, r => r.Text == "a");
        Assert.Equal(new[] { "b" }, _persisted.Terms);
    }

    [Fact]
    public async Task Add_correction_persists_key_and_value()
    {
        var vm = MakeVm();
        vm.NewHeard = "acme";
        vm.NewCorrect = "ACME Corp";
        await vm.AddCorrectionCommand.ExecuteAsync(null);

        Assert.Equal("ACME Corp", _persisted.Corrections["acme"]);
        Assert.Equal("", vm.NewHeard);
        Assert.Equal("", vm.NewCorrect);
    }

    [Fact]
    public async Task Add_correction_with_blank_key_or_value_is_rejected()
    {
        var vm = MakeVm();
        vm.NewHeard = "  ";
        vm.NewCorrect = "x";
        await vm.AddCorrectionCommand.ExecuteAsync(null);
        Assert.Empty(vm.Corrections);

        vm.NewHeard = "k";
        vm.NewCorrect = "   ";
        await vm.AddCorrectionCommand.ExecuteAsync(null);
        Assert.Empty(vm.Corrections);
        Assert.Equal(0, _persistCount);
    }

    [Fact]
    public async Task Add_case_insensitive_duplicate_correction_key_is_rejected()
    {
        var vm = MakeVm();
        vm.NewHeard = "Auth"; vm.NewCorrect = "OAuth";
        await vm.AddCorrectionCommand.ExecuteAsync(null);
        vm.NewHeard = "auth"; vm.NewCorrect = "OAuth2";
        await vm.AddCorrectionCommand.ExecuteAsync(null);

        Assert.Single(vm.Corrections);                    // key collision under OrdinalIgnoreCase
        Assert.NotEmpty(_reporter.Infos);
    }

    [Fact]
    public void Load_replaces_collections_without_persisting()
    {
        var vm = MakeVm();
        vm.Load(new Vocabulary
        {
            Terms = new[] { "t1" },
            Corrections = new Dictionary<string, string> { ["h"] = "c" },
        });

        Assert.Single(vm.Terms);
        Assert.Single(vm.Corrections);
        Assert.Equal(0, _persistCount);                   // Load is not a save
    }

    private sealed class FakeReporter : IUiErrorReporter
    {
        public List<(string, Exception)> Errors { get; } = new();
        public List<string> Infos { get; } = new();
        public void Report(string context, Exception ex) => Errors.Add((context, ex));
        public void Info(string message) => Infos.Add(message);
    }
}
