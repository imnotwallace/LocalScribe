// src/LocalScribe.App/ViewModels/AddSessionsPickerViewModel.cs
using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
namespace LocalScribe.App.ViewModels;

/// <summary>One selectable row in the Matters-page "Add sessions..." picker
/// (design 2026-07-18 section 4).</summary>
public sealed partial class PickerSessionItem(string id, string title, string dateDisplay, string source)
    : ObservableObject
{
    public string Id { get; } = id;
    public string Title { get; } = title;
    public string DateDisplay { get; } = dateDisplay;
    public string Source { get; } = source;
    [ObservableProperty] private bool _isSelected;
}

/// <summary>Dialog VM for tagging existing sessions to the selected matter: candidates are
/// pre-filtered by the caller (untagged, unarchived); the filter here narrows by title only.
/// Selection is held on the items themselves, so checked rows survive filtering. WPF-free.</summary>
public sealed partial class AddSessionsPickerViewModel : ObservableObject
{
    private readonly IReadOnlyList<PickerSessionItem> _all;

    public ObservableCollection<PickerSessionItem> Visible { get; } = [];
    [ObservableProperty] private string _filterText = "";

    public AddSessionsPickerViewModel(IReadOnlyList<PickerSessionItem> candidates)
    {
        _all = candidates;
        ApplyFilter();
    }

    partial void OnFilterTextChanged(string value) => ApplyFilter();

    private void ApplyFilter()
    {
        string q = FilterText.Trim();
        Visible.Clear();
        foreach (var item in _all)
            if (q.Length == 0 || item.Title.Contains(q, StringComparison.OrdinalIgnoreCase))
                Visible.Add(item);
    }

    /// <summary>Checked ids across ALL candidates, including ones the filter currently hides.</summary>
    public IReadOnlyList<string> SelectedIds
        => _all.Where(i => i.IsSelected).Select(i => i.Id).ToList();
}
