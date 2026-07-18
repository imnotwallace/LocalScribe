// src/LocalScribe.App/ViewModels/PagerViewModel.cs
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
namespace LocalScribe.App.ViewModels;

/// <summary>Classic pager state (design 2026-07-18 section 1), shared by the Sessions grid,
/// Search results, and the Matters tagged-sessions grid. WPF-free. Contract: SetTotal/Reset are
/// SILENT (they clamp/rewind without raising Changed - the host re-slices immediately after);
/// Changed fires only for user-driven page/size moves, and the host re-slices in the handler.</summary>
public sealed partial class PagerViewModel : ObservableObject
{
    /// <summary>Bound by PagerControl's page-size ComboBox. Default PageSize is 50.</summary>
    public static IReadOnlyList<int> PageSizeChoices { get; } = [25, 50, 100];

    [ObservableProperty] private int _currentPage = 1;   // 1-based
    [ObservableProperty] private int _pageSize = 50;
    [ObservableProperty] private int _totalCount;

    private bool _silent;

    public int PageCount => Math.Max(1, (TotalCount + PageSize - 1) / PageSize);
    public bool HasItems => TotalCount > 0;
    public string PageText => $"Page {CurrentPage} of {PageCount}";
    public bool CanGoPrev => CurrentPage > 1;
    public bool CanGoNext => CurrentPage < PageCount;

    public IRelayCommand PrevCommand { get; }
    public IRelayCommand NextCommand { get; }

    public event Action? Changed;

    public PagerViewModel()
    {
        PrevCommand = new RelayCommand(() => CurrentPage--, () => CanGoPrev);
        NextCommand = new RelayCommand(() => CurrentPage++, () => CanGoNext);
    }

    public void SetTotal(int count)
    {
        _silent = true;
        try
        {
            TotalCount = Math.Max(0, count);
            if (CurrentPage > PageCount) CurrentPage = PageCount;
        }
        finally { _silent = false; }
    }

    public void Reset()
    {
        _silent = true;
        try { CurrentPage = 1; }
        finally { _silent = false; }
    }

    public IReadOnlyList<T> Slice<T>(IReadOnlyList<T> items)
        => items.Skip((CurrentPage - 1) * PageSize).Take(PageSize).ToList();

    partial void OnCurrentPageChanged(int value)
    {
        NotifyDerived();
        if (!_silent) Changed?.Invoke();
    }

    partial void OnPageSizeChanged(int value)
    {
        // A size flip re-windows everything: rewind to page 1 (spec-accepted simplification)
        // inside the silent guard so the whole flip raises exactly one Changed.
        bool wasSilent = _silent;
        _silent = true;
        try { if (CurrentPage != 1) CurrentPage = 1; }
        finally { _silent = wasSilent; }
        NotifyDerived();
        if (!_silent) Changed?.Invoke();
    }

    partial void OnTotalCountChanged(int value) => NotifyDerived();

    private void NotifyDerived()
    {
        OnPropertyChanged(nameof(PageCount));
        OnPropertyChanged(nameof(HasItems));
        OnPropertyChanged(nameof(PageText));
        OnPropertyChanged(nameof(CanGoPrev));
        OnPropertyChanged(nameof(CanGoNext));
        PrevCommand.NotifyCanExecuteChanged();
        NextCommand.NotifyCanExecuteChanged();
    }
}
