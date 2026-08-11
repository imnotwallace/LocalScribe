using CommunityToolkit.Mvvm.ComponentModel;
using LocalScribe.App.Services;
using LocalScribe.Core.Model;
using LocalScribe.Core.Projection;

namespace LocalScribe.App.ViewModels;

/// <summary>Editor VM for the read view's "Add line here..." dialog (design 2026-08-11 section
/// 2.3): a person authoring a transcript line where the machine heard nothing. WPF-free, like
/// CorrectTextViewModel, and for the same reason - the App suite has no STA/dispatcher harness, so
/// anything decidable that lived in the dialog's code-behind would be permanently untestable.
///
/// The speaker list offers NAMED PARTICIPANTS ONLY plus Unattributed. Detected-voice clusters are
/// deliberately absent: an inserted line's speaker is a meta.json participant id, never a
/// clusterKey, because speakers.json maps diarisation clusters to names and a human-authored line
/// has no cluster (see EditableSectionViewModel.InsertedChoices for the same rule on the inline
/// editor - the pre-Task-10 amendment made both entry points share it).
///
/// The timestamp is human-estimated by definition and is parsed by TimestampParser - the same
/// parser the read view's go-to box uses, so an unpadded "1:02:03" cannot mean two different
/// instants in two places.</summary>
public sealed partial class InsertLineViewModel : ObservableObject
{
    private readonly MaintenanceService _maintenance;
    private readonly IUiErrorReporter _reporter;
    private readonly string _versionId;
    private readonly string _timestampsMode;
    private readonly DateTimeOffset _startedAtLocal;
    private readonly long _durationMs;

    public string SessionId { get; }
    /// <summary>The seed from the clicked row, kept so a caller can assert what was seeded even
    /// after the user retypes TimestampText.</summary>
    public long StartMs { get; }
    public IReadOnlyList<SpeakerChoice> SpeakerChoices { get; }

    [ObservableProperty] private string _timestampText;
    [ObservableProperty] private string _text = "";
    [ObservableProperty] private SpeakerChoice? _speaker;
    [ObservableProperty] private bool _isSaving;
    [ObservableProperty] private string _validationMessage = "";

    public InsertLineViewModel(MaintenanceService maintenance, IUiErrorReporter reporter,
        string sessionId, long seedStartMs, SessionMeta meta, string timestampsMode,
        DateTimeOffset startedAtLocal, string versionId, long durationMs)
    {
        (_maintenance, _reporter, SessionId, _versionId) = (maintenance, reporter, sessionId, versionId);
        (_timestampsMode, _startedAtLocal, _durationMs) = (timestampsMode, startedAtLocal, durationMs);
        StartMs = seedStartMs;
        _timestampText = TimestampFormat.Stamp(seedStartMs, timestampsMode, startedAtLocal);

        // Named participants only. SpeakerChoices.Build also appends detected-voice clusters and a
        // leading "Automatic (Me / Them)" - neither belongs on a line with no audio stream and no
        // cluster behind it.
        var choices = new List<SpeakerChoice>
        {
            new(InsertedLineLabels.Unattributed, null, null),
        };
        foreach (var p in meta.Participants)
            if (p.Kind == ParticipantKind.Named && !string.IsNullOrEmpty(p.Name))
                choices.Add(new SpeakerChoice(p.Name, p.Id, null));
        SpeakerChoices = choices;
        _speaker = choices[0];
    }

    /// <summary>True = the line was written, close the dialog. False = stay open: a validation
    /// problem (ValidationMessage set) or an IO failure (reported via IUiErrorReporter).</summary>
    public async Task<bool> SaveAsync(CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(Text))
        {
            ValidationMessage = "Type the words that were said. A transcript line is never blank.";
            return false;
        }
        if (!TimestampParser.TryParse(TimestampText, _timestampsMode, _startedAtLocal, out long ms))
        {
            ValidationMessage = "That timestamp could not be read. Use the same format as the "
                + "transcript's own stamps.";
            return false;
        }
        if (ms < 0 || (_durationMs > 0 && ms > _durationMs))
        {
            ValidationMessage = "That time is outside this recording. Choose a time within the "
                + "session.";
            return false;
        }

        ValidationMessage = "";
        IsSaving = true;
        try
        {
            var batch = new TranscriptEditBatch(new Dictionary<int, string>(), [], [], [])
            {
                Insertions = [new InsertedLineEdit(null, Text.Trim(), ms, Speaker?.ParticipantId)],
            };
            await _maintenance.SaveTranscriptEditsAsync(SessionId, batch, _versionId, ct);
            return true;
        }
        catch (Exception ex)
        {
            _reporter.Report("Add transcript line", ex);
            return false;
        }
        finally { IsSaving = false; }
    }
}
