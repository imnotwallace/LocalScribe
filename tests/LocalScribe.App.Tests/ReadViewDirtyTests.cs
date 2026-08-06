using System.IO;
using LocalScribe.App.Services;
using LocalScribe.App.ViewModels;
using LocalScribe.Core.Audio;
using LocalScribe.Core.Model;
using LocalScribe.Core.Storage;
using Xunit;

namespace LocalScribe.App.Tests;

/// <summary>The read-view close guard's dirty signal (Tier 1B design 2026-08-05, T1-3).
/// ReadViewViewModel had no IsDirty and no equivalent; edit state lives in EditSections and was
/// harvested only inside SaveEditsAsync. The window code-behind that consumes this is untestable in
/// this suite (no STA harness anywhere in tests/LocalScribe.App.Tests), so every decidable part of
/// the guard lives here instead.</summary>
public sealed class ReadViewDirtyTests : IDisposable
{
    private readonly string _root =
        Path.Combine(Path.GetTempPath(), "ls-readview-dirty-" + Guid.NewGuid().ToString("N"));
    private readonly StoragePaths _paths;
    private readonly FakeSettings _settings;
    private readonly FakeReporter _reporter = new();
    private readonly ManualUtcTimeProvider _time =
        new(new DateTimeOffset(2026, 7, 3, 12, 0, 0, TimeSpan.Zero));
    private readonly MaintenanceService _maintenance;
    private readonly FakePlayer _player = new();

    public ReadViewDirtyTests()
    {
        _paths = new StoragePaths(_root);
        _settings = new FakeSettings(new Settings { StorageRoot = _root });
        _maintenance = new MaintenanceService(_paths, _settings, new FakeBin(), _time);
    }

    public void Dispose() { try { Directory.Delete(_root, recursive: true); } catch { } }

    /// <summary>A finalized two-turn session: EndedAtUtc set (CanEdit gates on it) and two turns
    /// 10 minutes apart, well past the SectionGapMs default, so TranscriptProjection always groups
    /// them into two DISTINCT rows - one editable section per turn.</summary>
    private async Task<ReadViewViewModel> LoadAsync()
    {
        Directory.CreateDirectory(_paths.SessionDir("s1"));
        await new SessionStore(_paths.SessionJson("s1")).SaveAsync(new SessionRecord
        {
            Id = "s1", App = AppKind.Webex,
            StartedAtUtc = new DateTimeOffset(2026, 7, 3, 9, 0, 0, TimeSpan.Zero),
            EndedAtUtc = new DateTimeOffset(2026, 7, 3, 9, 30, 0, TimeSpan.Zero),
            TimeZoneId = "UTC", UtcOffsetMinutes = 0, DurationMs = 1_800_000,
        }, default);
        await new MetadataStore(_paths.MetaJson("s1")).SaveAsync(new SessionMeta
        {
            Title = "Doe intake",
            // TWO named Local participants, seeded UNCONDITIONALLY. SpeakerChoices.Build emits a
            // leading "Automatic (Me / Them)" choice and then one entry per NAMED participant on the
            // matching side, so a fixture with no participants yields a single-entry list and
            // A_speaker_reassignment_alone_makes_it_dirty has nothing to reassign TO. Both segments
            // below are TranscriptSource.Local, so both slots must be Side = SourceKind.Local.
            // REJECTED: seeding this only if the test throws - a conditional fixture makes the one
            // leg this task calls "easiest to forget" the one leg that silently stops running.
            Participants =
            [
                new SessionParticipant { Id = "p-me", Name = "Me", Side = SourceKind.Local, IsSelf = true },
                new SessionParticipant { Id = "p-roe", Name = "Ms Roe", Side = SourceKind.Local },
            ],
            LocalCount = 2,
        }, default);
        var store = new TranscriptStore(_paths.TranscriptJsonl("s1"));
        await store.AppendAsync(TranscriptLine.Segment(0, TranscriptSource.Local, 0, 4000,
            "Good morning.", "Me"), default);
        await store.AppendAsync(TranscriptLine.Segment(1, TranscriptSource.Local, 600_000, 604_000,
            "That concludes it.", "Me"), default);

        var vm = new ReadViewViewModel(_maintenance, _paths, _settings, _reporter, _player,
            dispatch: a => a(), _time);
        await vm.LoadAsync("s1", default);
        return vm;
    }

    /// <summary>Expands through the VM's OWN seam. ReadViewViewModel.ExpandSection is documented
    /// "Public: find jump-in and tests share it" and passes SpeakerChoicesForRemote(),
    /// SpeakerChoicesForLocal() and CurrentSpeakerFor. REJECTED: section.BeginEdit("relative",
    /// vm.StartedAtLocal) - BeginEdit's three trailing arguments are optional and coalesce to `[]`,
    /// so every materialized segment would get an EMPTY SpeakerChoices list and a null Speaker, and
    /// the reassignment fact could never find an alternative choice no matter what meta.json
    /// holds.</summary>
    private static EditableSectionViewModel Expand(ReadViewViewModel vm, int index)
    {
        var section = vm.EditSections[index];
        vm.ExpandSection(section);
        return section;
    }

    [Fact]
    public async Task Not_dirty_before_edit_mode_is_even_entered()
    {
        var vm = await LoadAsync();
        Assert.False(vm.IsEditMode);
        Assert.False(vm.HasUnsavedEdits);
    }

    [Fact]
    public async Task Not_dirty_after_entering_edit_mode_and_expanding_a_section_but_typing_nothing()
    {
        // The most important negative: merely OPENING the editor must never prompt on close. A
        // false "unsaved changes" dialog on every read-view close trains the user to click through
        // it, which is how a real one gets dismissed.
        var vm = await LoadAsync();
        vm.EnterEditMode();
        Expand(vm, 0);

        Assert.True(vm.IsEditMode);
        Assert.False(vm.HasUnsavedEdits);
    }

    [Fact]
    public async Task A_text_correction_makes_it_dirty()
    {
        var vm = await LoadAsync();
        vm.EnterEditMode();
        var section = Expand(vm, 0);

        section.Segments[0].EditedText = "Good morning, Mr Doe.";

        Assert.True(vm.HasUnsavedEdits);
    }

    [Fact]
    public async Task Whitespace_only_retyping_is_not_dirty()
    {
        // Matches the correction no-op guard SaveEditsAsync uses (EditedText.Trim() vs
        // ProjectedText.Trim()): a stray trailing space must not manufacture a phantom edit and
        // must not manufacture a phantom close prompt either.
        var vm = await LoadAsync();
        vm.EnterEditMode();
        var section = Expand(vm, 0);

        section.Segments[0].EditedText = "  Good morning.  ";

        Assert.False(vm.HasUnsavedEdits);
    }

    [Fact]
    public async Task A_new_split_makes_it_dirty()
    {
        var vm = await LoadAsync();
        vm.EnterEditMode();
        var section = Expand(vm, 0);

        section.SplitSegment(section.Segments[0], caret: 5);

        Assert.Equal(2, section.Segments.Count);
        Assert.True(vm.HasUnsavedEdits);
    }

    [Fact]
    public async Task A_split_revert_makes_it_dirty()
    {
        var vm = await LoadAsync();
        vm.EnterEditMode();
        var section = Expand(vm, 0);
        section.SplitSegment(section.Segments[0], caret: 5);

        section.RevertSplit(section.Segments[0].Seq);

        // Part count is back to one, so the count comparison alone would read CLEAN - the pending
        // revert is only visible through CollectSplitReverts, which is why it is checked separately.
        Assert.Single(section.Segments);
        Assert.True(vm.HasUnsavedEdits);
    }

    [Fact]
    public async Task A_speaker_reassignment_alone_makes_it_dirty()
    {
        // The leg that is easiest to forget: a pure re-attribution changes no text and creates no
        // split, and SaveEditsAsync detects it in a SEPARATE loop via SameSpeakerTarget. Missing
        // it would let a whole session's re-attribution close silently.
        var vm = await LoadAsync();
        vm.EnterEditMode();
        var section = Expand(vm, 0);
        var seg = section.Segments[0];
        var current = seg.Speaker;

        // The fixture seeds two named Local participants, so Build() returns
        // [Automatic (Me / Them), Me, Ms Roe] and there is ALWAYS a genuine alternative. Asserted
        // rather than null-coalesced into a throw: if this ever comes back empty the fixture broke,
        // and that must read as a failure here, not as a confusing exception inside the act.
        var other = seg.SpeakerChoices.FirstOrDefault(c => !SameTarget(c, current));
        Assert.NotNull(other);

        seg.Speaker = other;

        Assert.True(vm.HasUnsavedEdits);
    }

    /// <summary>Local mirror of ReadViewViewModel's private SameSpeakerTarget, used only to pick a
    /// choice that genuinely DIFFERS from the pre-selected one - comparing by target, not display
    /// text, so a renamed participant is not mistaken for a different one.</summary>
    private static bool SameTarget(SpeakerChoice? a, SpeakerChoice? b) =>
        (a?.IsUnassign ?? false) == (b?.IsUnassign ?? false)
        && string.Equals(a?.ParticipantId, b?.ParticipantId, StringComparison.Ordinal)
        && string.Equals(a?.ClusterKey, b?.ClusterKey, StringComparison.Ordinal);

    [Fact]
    public async Task Cancelling_the_edit_clears_the_dirty_flag()
    {
        var vm = await LoadAsync();
        vm.EnterEditMode();
        var section = Expand(vm, 0);
        section.Segments[0].EditedText = "Good morning, Mr Doe.";
        Assert.True(vm.HasUnsavedEdits);

        vm.CancelEdit();

        Assert.False(vm.IsEditMode);
        Assert.False(vm.HasUnsavedEdits);                       // EditSections cleared
    }

    [Fact]
    public async Task A_successful_save_clears_the_dirty_flag()
    {
        var vm = await LoadAsync();
        vm.EnterEditMode();
        var section = Expand(vm, 0);
        section.Segments[0].EditedText = "Good morning, Mr Doe.";

        await vm.SaveEditsAsync(default);

        Assert.Null(vm.SaveError);
        Assert.False(vm.IsEditMode);
        Assert.False(vm.HasUnsavedEdits);                       // the close guard may now proceed
    }

    // Duplicated from ReadViewEditModeTests.cs per the house convention (no cross-file test
    // helper); kept identical so a change to the VM's seams surfaces in both places at once.
    private sealed class FakeSettings : ISettingsService
    {
        public FakeSettings(Settings current) => Current = current;
        public Settings Current { get; private set; }
        public event Action<Settings, Settings>? Changed;
        public Task SaveAsync(Settings updated, CancellationToken ct)
        {
            var old = Current;
            Current = updated;
            Changed?.Invoke(old, updated);
            return Task.CompletedTask;
        }
    }

    private sealed class FakeBin : IRecycleBin
    {
        public void SendToRecycleBin(string path)
        {
            if (Directory.Exists(path)) Directory.Delete(path, recursive: true);
            else if (File.Exists(path)) File.Delete(path);
        }
    }

    private sealed class FakeReporter : IUiErrorReporter
    {
        public List<(string Context, Exception Ex)> Errors { get; } = new();
        public List<string> Infos { get; } = new();
        public void Report(string context, Exception ex) => Errors.Add((context, ex));
        // The trailing `bool privileged = true` is Plan A's shipped interface member - a
        // one-parameter Info(string) does not implement it (CS0535). Every IUiErrorReporter fake in
        // this plan must carry it.
        public void Info(string message, bool privileged = true) => Infos.Add(message);
    }

    private sealed class FakePlayer : IDualAudioPlayer
    {
        public long PositionMs { get; set; }
        public long DurationMs { get; set; }
        public event Action? MediaReady;
        public event Action? MediaEnded;
        public void Load(string? localPath, string? remotePath) { }
        public void Play() { }
        public void Pause() { }
        public void SeekMs(long ms) => PositionMs = ms;
        public void SetLegMuted(bool local, bool muted) { }
        public void SetLegVolume(bool local, double volume) { }
        public void Dispose() { }
        public void RaiseReady() => MediaReady?.Invoke();
        public void RaiseEnded() => MediaEnded?.Invoke();
    }
}
