using System.Collections.ObjectModel;
using System.Globalization;
using System.Linq;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using LocalScribe.App.Services;

namespace LocalScribe.App.ViewModels;

/// <summary>One row of the Settings Components panel (Tier 1 plan D, T1-10, 2026-08-05).
/// CanDownload is false for a probe-only component (ffmpeg, the diarizer, the assistant helper):
/// those arrive with the installer or via tools/fetch-ffmpeg.ps1, so Detail carries the remedy
/// and no button is offered.</summary>
public sealed partial class ComponentRow(string id, string name, ComponentPin? pin) : ObservableObject
{
    public string Id { get; } = id;
    public string Name { get; } = name;
    public ComponentPin? Pin { get; } = pin;

    /// <summary>The licence the user is agreeing to by fetching this component, shown BEFORE the
    /// download starts (packaging design note 2026-08-06, decision 5). Null for a probe-only row,
    /// which fetches nothing and so has no terms to state here.</summary>
    public string? License { get; } = pin?.License;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanDownload))]
    private bool _installed;

    [ObservableProperty] private string _sizeText = "";
    [ObservableProperty] private string? _detail;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanDownload))]
    private bool _isDownloading;

    [ObservableProperty] private double _progress;
    [ObservableProperty] private string _progressText = "";

    public bool CanDownload => Pin is not null && !Installed && !IsDownloading;
}

/// <summary>The Settings "Components" panel (Tier 1 plan D, T1-10, 2026-08-05): what is
/// installed, how big it is, and a Download button that runs the OUT-OF-PROCESS fetch helper
/// with progress and resume.
///
/// Every collaborator is a delegate or an injected object - the pin loader, the probe, the
/// destination resolver, the fetch client - so this VM never reads the machine and never starts
/// a process during a test run, and so nothing here has to know that a download happens in
/// another executable at all.
///
/// LastLoad / LastDownload follow the SettingsPageViewModel.LastSave precedent: production
/// fire-and-forgets and surfaces failures through IUiErrorReporter; tests await them so no work
/// is in flight when they assert.</summary>
public sealed partial class ComponentsPanelViewModel : ObservableObject
{
    private readonly Func<CancellationToken, Task<IReadOnlyList<ComponentPin>>> _loadPins;
    private readonly ComponentProbe _probe;
    private readonly Func<ComponentPin, string> _destPathFor;
    private readonly ComponentFetchClient _fetch;
    private readonly IUiErrorReporter _errors;
    private readonly Action<Action> _dispatch;
    private readonly Dictionary<string, CancellationTokenSource> _running = new(StringComparer.Ordinal);
    private IReadOnlyList<ComponentPin> _pins = [];

    public ComponentsPanelViewModel(
        Func<CancellationToken, Task<IReadOnlyList<ComponentPin>>> loadPins,
        ComponentProbe probe, Func<ComponentPin, string> destPathFor,
        ComponentFetchClient fetch, IUiErrorReporter errors, Action<Action> dispatch)
    {
        (_loadPins, _probe, _destPathFor, _fetch, _errors, _dispatch)
            = (loadPins, probe, destPathFor, fetch, errors, dispatch);
        DownloadCommand = new AsyncRelayCommand<ComponentRow>(DownloadAsync);
        CancelCommand = new RelayCommand<ComponentRow>(Cancel);
        RefreshCommand = new AsyncRelayCommand(() => LastLoad = ReloadAsync());
        LastLoad = ReloadAsync();
    }

    public ObservableCollection<ComponentRow> Rows { get; } = [];

    /// <summary>The last pin-load + probe round trip. Production fire-and-forgets; tests await
    /// it so the rows exist before they assert (the LastSave precedent).</summary>
    public Task LastLoad { get; private set; } = Task.CompletedTask;

    /// <summary>The last download round trip, same contract as LastLoad.</summary>
    public Task LastDownload { get; private set; } = Task.CompletedTask;

    public IAsyncRelayCommand<ComponentRow> DownloadCommand { get; }
    public IRelayCommand<ComponentRow> CancelCommand { get; }
    public IAsyncRelayCommand RefreshCommand { get; }

    private async Task ReloadAsync()
    {
        try
        {
            _pins = await _loadPins(CancellationToken.None);
            var states = _probe.Probe(_pins);
            _dispatch(() =>
            {
                Rows.Clear();
                foreach (var s in states) Rows.Add(ToRow(s));
            });
        }
        catch (Exception ex) { _errors.Report("Reading installed components", ex); }
    }

    private static ComponentRow ToRow(ComponentState s) => new(s.Id, s.Name, s.Pin)
    {
        Installed = s.Installed,
        SizeText = FormatSize(s.Bytes),
        Detail = s.Detail,
    };

    /// <summary>Invariant culture and one decimal: the panel is a size the user compares against
    /// free disk space, not a precise figure, and it must render identically on every machine.</summary>
    public static string FormatSize(long bytes)
    {
        if (bytes <= 0) return "-";
        double mb = bytes / 1_000_000.0;
        return mb >= 1000
            ? string.Create(CultureInfo.InvariantCulture, $"{mb / 1000:0.0} GB")
            : string.Create(CultureInfo.InvariantCulture, $"{mb:0.0} MB");
    }

    private Task DownloadAsync(ComponentRow? row) => LastDownload = RunDownloadAsync(row);

    private async Task RunDownloadAsync(ComponentRow? row)
    {
        // A probe-only row has no pin: refuse rather than start a helper with nothing to fetch.
        // The button is hidden for these, but a bound command must never rely on that.
        if (row?.Pin is not { } pin || row.Installed || row.IsDownloading) return;

        var cts = new CancellationTokenSource();
        lock (_running) _running[row.Id] = cts;
        _dispatch(() => { row.IsDownloading = true; row.Progress = 0; row.ProgressText = "0%"; });
        try
        {
            var progress = new Progress<ComponentFetchProgress>(p => _dispatch(() =>
            {
                row.Progress = p.Fraction;
                row.ProgressText = string.Create(CultureInfo.InvariantCulture,
                    $"{(int)Math.Round(p.Fraction * 100)}%");
            }));
            await _fetch.FetchAsync(pin, _destPathFor(pin), progress, cts.Token);
            _errors.Info("Installed " + pin.Name + ".", NoticeSeverity.Success);
        }
        catch (OperationCanceledException)
        {
            // A user cancel. The partial file stays on disk ON PURPOSE - the helper RESUMES from
            // it on the next attempt, which is the whole reason it sends a range request.
            _errors.Info("Download cancelled - " + pin.Name + " will resume from where it stopped.");
        }
        catch (Exception ex) { _errors.Report("Downloading " + pin.Name, ex); }
        finally
        {
            lock (_running) _running.Remove(row.Id);
            cts.Dispose();
            _dispatch(() => { row.IsDownloading = false; row.Progress = 0; row.ProgressText = ""; });
            // Re-probe rather than assume: the helper deletes a hash-mismatched file, so
            // "the call returned" is not the same fact as "the component is installed".
            LastLoad = ReloadAsync();
        }
    }

    private void Cancel(ComponentRow? row)
    {
        if (row is null) return;
        lock (_running) { if (_running.TryGetValue(row.Id, out var cts)) cts.Cancel(); }
    }
}
