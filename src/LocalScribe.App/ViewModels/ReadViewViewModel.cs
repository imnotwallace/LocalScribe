// src/LocalScribe.App/ViewModels/ReadViewViewModel.cs
using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using LocalScribe.App.Services;
using LocalScribe.Core.Model;
using LocalScribe.Core.Projection;
using LocalScribe.Core.Storage;
using LocalScribe.Core.Vocabulary;
namespace LocalScribe.App.ViewModels;

/// <summary>Read-only session view (design section 5). Rows come from the canonical
/// TranscriptProjection - the same pipeline as transcript.md/.txt and session.txt. The load
/// pipeline mirrors SessionWriter.RegenerateProjectionsAsync (load order, meta fallback,
/// vocabulary provider construction) so what the window shows is what the files say. Known
/// deliberate divergence: the 3b live view renders raw merger lines with no projection pass,
/// so this view may differ from what was seen live. WPF-free; all reads run inside the
/// maintenance per-session queue so a load cannot interleave with recovery or a cascade.</summary>
public sealed partial class ReadViewViewModel : ObservableObject
{
    private readonly MaintenanceService _maintenance;
    private readonly StoragePaths _paths;
    private readonly ISettingsService _settings;
    private readonly IUiErrorReporter _reporter;
    private readonly Action<Action> _dispatch;
    private readonly TimeProvider _time;

    [ObservableProperty] private bool _isLoaded;

    public ObservableCollection<DisplayRow> Rows { get; } = new();
    public string SessionId { get; private set; } = "";
    public string TimestampsMode { get; private set; } = "relative";   // read by the window's stamp converter
    public DateTimeOffset StartedAtLocal { get; private set; }

    public ReadViewViewModel(MaintenanceService maintenance, StoragePaths paths,
        ISettingsService settings, IUiErrorReporter reporter, Action<Action> dispatch, TimeProvider time)
        => (_maintenance, _paths, _settings, _reporter, _dispatch, _time)
            = (maintenance, paths, settings, reporter, dispatch, time);

    private sealed record LoadedView(SessionRecord Session, SessionMeta Meta,
        IReadOnlyList<string> MatterDisplays, IReadOnlyList<DisplayRow> Rows,
        bool HasDegraded, DateTimeOffset StartedLocal);

    public async Task LoadAsync(string sessionId, CancellationToken ct)
    {
        SessionId = sessionId;
        try
        {
            var settings = _settings.Current;
            var view = await _maintenance.RunForSessionAsync(sessionId, async token =>
            {
                // Mirrors SessionWriter.RegenerateProjectionsAsync exactly: load order, the
                // session-offset local time, the CreateDefault meta fallback (self: null),
                // matter resolution, and the VocabularyProvider construction.
                var session = await new SessionStore(_paths.SessionJson(sessionId)).ReadAsync(token)
                              ?? throw new InvalidOperationException($"session.json missing for {sessionId}");
                var startedLocal = session.UtcOffsetMinutes is int offsetMin
                    ? session.StartedAtUtc.ToOffset(TimeSpan.FromMinutes(offsetMin))
                    : session.StartedAtUtc.ToLocalTime();
                var meta = await new MetadataStore(_paths.MetaJson(sessionId)).LoadAsync(token)
                           ?? SessionMeta.CreateDefault(session.App, startedLocal, self: null);
                var lines = await new TranscriptStore(_paths.TranscriptJsonl(sessionId)).ReadAllAsync(token);
                var speakers = await new SpeakersStore(_paths.SpeakersJson(sessionId)).LoadAsync(token);
                var edits = await new EditStore(_paths.SessionDir(sessionId), _time).LoadAsync(token);

                var matterStore = new MatterStore(_paths.MattersDir);
                var mattersById = new Dictionary<string, Matter>();
                var matterDisplays = new List<string>();
                foreach (string mid in meta.MatterIds)
                {
                    var m = await matterStore.LoadAsync(mid, token);
                    if (m is null) { matterDisplays.Add(mid); continue; }
                    mattersById[mid] = m;
                    matterDisplays.Add(string.IsNullOrEmpty(m.Reference) ? m.Name : $"{m.Name} ({m.Reference})");
                }

                var projection = new TranscriptProjection(
                    new VocabularyProvider(settings.Vocabulary, mattersById), new PhantomBleedDedup());
                var rows = projection.Build(lines, speakers, edits, meta);

                // Mid-session degradation exists only as a transcript marker (design 3.2/5) -
                // the list badge cannot see it, so the read view surfaces it.
                bool degraded = lines.Any(l =>
                    l.Kind == TranscriptKind.Marker && l.Text == Markers.DegradedSystemAudioLoopback);

                return new LoadedView(session, meta, matterDisplays, rows, degraded, startedLocal);
            }, ct);

            _dispatch(() => Apply(view, settings));
        }
        catch (Exception ex) { _reporter.Report("Open read view", ex); }
    }

    private void Apply(LoadedView view, Settings settings)
    {
        TimestampsMode = settings.Timestamps;
        StartedAtLocal = view.StartedLocal;
        Rows.Clear();
        foreach (var r in view.Rows) Rows.Add(r);
        IsLoaded = true;
    }
}
