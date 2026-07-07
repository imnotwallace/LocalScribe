using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using LocalScribe.App.Services;
using LocalScribe.Core.Model;

namespace LocalScribe.App.ViewModels;

/// <summary>One bias term row (Stage 6.2).</summary>
public sealed record TermRow(string Text);

/// <summary>One heard->correct row (Stage 6.2). Heard is the key (case-insensitive); Correct is
/// the replacement.</summary>
public sealed record CorrectionRow(string Heard, string Correct);

/// <summary>Shared custom-vocabulary editor (Stage 6.2), hosted by Settings (global) and the
/// Matters page (per-matter). WPF-free. Add/remove only - editing a term or key is remove +
/// re-add, which sidesteps row-identity churn and keeps the model a plain rebuild. Every valid
/// mutation rebuilds the immutable <see cref="Vocabulary"/> and invokes the injected persist
/// callback (Settings wires it to its auto-save Commit; Matters wires it to SaveMatterAsync).
/// Empty and case-insensitive-duplicate inputs are rejected via IUiErrorReporter.Info (matching
/// VocabularyProvider's OrdinalIgnoreCase collapse) - a save never happens for a rejected input.</summary>
public sealed partial class VocabularyEditorViewModel : ObservableObject
{
    private readonly Func<Vocabulary, CancellationToken, Task> _persist;
    private readonly IUiErrorReporter _reporter;

    public ObservableCollection<TermRow> Terms { get; } = new();
    public ObservableCollection<CorrectionRow> Corrections { get; } = new();

    [ObservableProperty] private string _newTerm = "";
    [ObservableProperty] private string _newHeard = "";
    [ObservableProperty] private string _newCorrect = "";

    public IAsyncRelayCommand AddTermCommand { get; }
    public IAsyncRelayCommand<TermRow> RemoveTermCommand { get; }
    public IAsyncRelayCommand AddCorrectionCommand { get; }
    public IAsyncRelayCommand<CorrectionRow> RemoveCorrectionCommand { get; }

    public VocabularyEditorViewModel(Func<Vocabulary, CancellationToken, Task> persist,
        IUiErrorReporter reporter)
    {
        (_persist, _reporter) = (persist, reporter);
        AddTermCommand = new AsyncRelayCommand(AddTermAsync);
        RemoveTermCommand = new AsyncRelayCommand<TermRow>(RemoveTermAsync);
        AddCorrectionCommand = new AsyncRelayCommand(AddCorrectionAsync);
        RemoveCorrectionCommand = new AsyncRelayCommand<CorrectionRow>(RemoveCorrectionAsync);
    }

    /// <summary>Repopulate the collections from a stored Vocabulary WITHOUT persisting (call on
    /// host load / matter selection).</summary>
    public void Load(Vocabulary v)
    {
        Terms.Clear();
        foreach (string t in v.Terms) Terms.Add(new TermRow(t));
        Corrections.Clear();
        foreach (var kv in v.Corrections) Corrections.Add(new CorrectionRow(kv.Key, kv.Value));
    }

    public Vocabulary Build() => new()
    {
        Terms = Terms.Select(r => r.Text).ToList(),
        Corrections = Corrections.ToDictionary(r => r.Heard, r => r.Correct, StringComparer.Ordinal),
    };

    private async Task AddTermAsync()
    {
        string term = NewTerm.Trim();
        if (term.Length == 0) { _reporter.Info("A term cannot be empty."); return; }
        if (Terms.Any(r => string.Equals(r.Text, term, StringComparison.OrdinalIgnoreCase)))
        { _reporter.Info($"\"{term}\" is already in the term list."); return; }
        Terms.Add(new TermRow(term));
        NewTerm = "";
        await SaveAsync();
    }

    private async Task RemoveTermAsync(TermRow? row)
    {
        if (row is null || !Terms.Remove(row)) return;
        await SaveAsync();
    }

    private async Task AddCorrectionAsync()
    {
        string heard = NewHeard.Trim();
        string correct = NewCorrect.Trim();
        if (heard.Length == 0 || correct.Length == 0)
        { _reporter.Info("Both the heard word and its correction are required."); return; }
        if (Corrections.Any(r => string.Equals(r.Heard, heard, StringComparison.OrdinalIgnoreCase)))
        { _reporter.Info($"\"{heard}\" already has a correction."); return; }
        Corrections.Add(new CorrectionRow(heard, correct));
        NewHeard = "";
        NewCorrect = "";
        await SaveAsync();
    }

    private async Task RemoveCorrectionAsync(CorrectionRow? row)
    {
        if (row is null || !Corrections.Remove(row)) return;
        await SaveAsync();
    }

    private async Task SaveAsync()
    {
        try { await _persist(Build(), CancellationToken.None); }
        catch (Exception ex) { _reporter.Report("Saving vocabulary", ex); }
    }
}
