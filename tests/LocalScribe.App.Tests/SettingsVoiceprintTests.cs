using System.IO;
using LocalScribe.App.Services;
using LocalScribe.App.ViewModels;
using LocalScribe.Core.Audio;
using LocalScribe.Core.Diarisation;
using LocalScribe.Core.Model;
using LocalScribe.Core.People;
using LocalScribe.Core.Storage;
using LocalScribe.Core.Tests;
using Xunit;

namespace LocalScribe.App.Tests;

/// <summary>Settings' Voiceprints section (voiceprint design 2026-07-25, Task 13): the People
/// list, the three deletion levels, the global purge, and the batch backfill scan.
///
/// This is the screen that OWNS deletion, so the purge tests below are the load-bearing ones: a
/// purge can partially fail, and a <c>people.json</c> failure means the saved voiceprints - the
/// most identifying data in the product - SURVIVED a deletion the user asked for. The UI must
/// never render that as success. Both purge shapes are pinned end-to-end against the REAL
/// MaintenanceService (a forward-versioned people.json is exactly what makes its PeopleStore load
/// throw), plus the message formatter directly for the shapes that cannot be forced from disk.
///
/// A QUEUED dispatch fake is used throughout (never the synchronous <c>a =&gt; a()</c>): every
/// observable mutation here is dispatched, and a synchronous fake would collapse the reload into
/// its call site and hide a command that mutates the list without re-reading disk.</summary>
public sealed class SettingsVoiceprintTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "ls-setvp-" + Guid.NewGuid().ToString("N"));
    private readonly FakeSettingsService _settings = new(new Settings());
    private readonly FakeUiErrorReporter _errors = new();
    private readonly QueuedDispatch _dispatcher = new();
    private readonly FakeEmbeddingEngine _engine = new();
    private readonly List<string> _confirmPrompts = new();
    private bool _confirmAnswer = true;

    private StoragePaths Paths => new(Path.Combine(_root, "storage"));

    public SettingsVoiceprintTests() => Directory.CreateDirectory(Path.Combine(_root, "models"));

    public void Dispose() { try { Directory.Delete(_root, recursive: true); } catch { } }

    /// <summary>Records dispatched actions and runs them only when explicitly pumped (the same
    /// ordering microscope SplitSpeakersViewModelVoiceprintTests uses).</summary>
    private sealed class QueuedDispatch
    {
        private readonly Queue<Action> _queue = new();
        public Action<Action> Dispatch => a => _queue.Enqueue(a);
        public void Pump() { while (_queue.Count > 0) _queue.Dequeue()(); }
    }

    private sealed class FakeEmbeddingEngine : IEmbeddingEngine
    {
        public int Calls { get; private set; }
        public List<string> ModelPaths { get; } = new();

        /// <summary>Set to hold the scan open inside the out-of-process embed call - the real
        /// window (minutes, one embed per participant) in which a second voiceprint command could
        /// be fired. Left null, EmbedAsync completes synchronously exactly as before.</summary>
        public TaskCompletionSource? Gate { get; set; }

        public async Task<EmbedResult> EmbedAsync(EmbedRequest request, CancellationToken ct)
        {
            Calls++;
            ModelPaths.Add(request.EmbeddingModelPath);
            if (Gate is not null) await Gate.Task;
            return new EmbedResult([1f, 0f, 0f], EmbeddingMethods.CampPlus);
        }
    }

    private SettingsPageViewModel MakeVm()
    {
        var paths = Paths;
        var maintenance = new MaintenanceService(paths, _settings, new FakeRecycleBin(), TimeProvider.System);
        int n = 0;
        return new SettingsPageViewModel(_settings, maintenance, new FakeLaunchAtLogin(),
            pickFolder: () => null, openFolder: _ => { }, _errors,
            dispatch: _dispatcher.Dispatch, new FakeCaptureDeviceEnumerator(),
            modelsRoot: Path.Combine(_root, "models"),
            assistantHelperProbe: () => null,
            paths: paths,
            people: new PeopleStore(paths.PeopleJson),
            enrollment: new VoiceprintEnrollmentService(paths, TimeProvider.System, () => $"e{++n}"),
            embeddingEngine: _engine,
            resolveModel: fileName => Path.Combine(_root, "models", fileName),
            confirm: message => { _confirmPrompts.Add(message); return _confirmAnswer; });
    }

    private async Task<SettingsPageViewModel> LoadedVmAsync()
    {
        var vm = MakeVm();
        await vm.PeopleLoad;
        _dispatcher.Pump();
        return vm;
    }

    private static VoiceprintEnrollment Enrollment(string id, string method, int dayOffset,
        string sourceSessionId = "s1") => new()
        {
            Id = id,
            Embedding = [1f, 0f, 0f],
            Method = method,
            SourceSessionId = sourceSessionId,
            SourceClusterKey = "Remote:0",
            EnrolledAtUtc = DateTimeOffset.UnixEpoch.AddDays(dayOffset),
        };

    private static Person MakePerson(string id, string name, params VoiceprintEnrollment[] enrollments)
        => new()
        {
            Id = id,
            Name = name,
            CreatedUtc = DateTimeOffset.UnixEpoch,
            Voiceprint = enrollments,
        };

    private Task SavePeopleAsync(params Person[] people)
        => new PeopleStore(Paths.PeopleJson).SaveAsync(new PeopleRegistry { People = people }, default);

    private Task<PeopleRegistry?> LoadPeopleAsync()
        => new PeopleStore(Paths.PeopleJson).LoadAsync(default);

    // ---------- People list ----------

    [Fact]
    public async Task People_list_shows_enrollment_counts()
    {
        await SavePeopleAsync(
            MakePerson("p1", "Sarah Chen",
                Enrollment("e1", EmbeddingMethods.CampPlus, 1),
                Enrollment("e2", EmbeddingMethods.CampPlus, 3, "s7")),
            MakePerson("p2", "No Voice"));

        var vm = await LoadedVmAsync();

        Assert.False(vm.HasNoPeople);
        Assert.Equal(new[] { "No Voice", "Sarah Chen" }, vm.People.Select(p => p.Name));

        var sarah = vm.People.Single(p => p.Id == "p1");
        Assert.Equal(2, sarah.EnrollmentCount);
        Assert.True(sarah.HasEnrollments);
        Assert.False(sarah.NeedsReenroll);
        Assert.Contains("2 voiceprints", sarah.EnrollmentSummary);
        Assert.Contains("1970-01-04", sarah.EnrollmentSummary);   // the LATEST enrollment's date
        Assert.Contains("s7", sarah.EnrollmentSummary);           // ...and the session it came from

        var none = vm.People.Single(p => p.Id == "p2");
        Assert.Equal(0, none.EnrollmentCount);
        Assert.False(none.HasEnrollments);
        Assert.False(none.NeedsReenroll);                         // nothing stored is not "stale"
        Assert.Equal("", none.EnrollmentSummary);
        Assert.Empty(_errors.Reports);
    }

    [Fact]
    public async Task Person_whose_enrollments_are_all_stale_method_needs_reenroll()
    {
        // A non-current Method cannot be compared against anything the current engine produces,
        // so the person can never be suggested - the list has to say so.
        await SavePeopleAsync(
            MakePerson("p1", "Stale Only", Enrollment("e1", "some-older-model", 1)),
            MakePerson("p2", "Mixed",
                Enrollment("e2", "some-older-model", 1),
                Enrollment("e3", EmbeddingMethods.CampPlus, 2)));

        var vm = await LoadedVmAsync();

        Assert.True(vm.People.Single(p => p.Id == "p1").NeedsReenroll);
        Assert.False(vm.People.Single(p => p.Id == "p2").NeedsReenroll);   // one usable is enough
    }

    [Fact]
    public async Task Empty_registry_reports_the_nothing_stored_state()
    {
        var vm = await LoadedVmAsync();
        Assert.Empty(vm.People);
        Assert.True(vm.HasNoPeople);
        Assert.Empty(_errors.Reports);
    }

    [Fact]
    public async Task First_load_with_unreadable_people_json_does_not_claim_nothing_stored()
    {
        // Fix round 1, finding 1: a forward-versioned people.json makes the VERY FIRST load throw
        // (ReloadPeopleAsync runs from the ctor). The truth is unknown, not empty - HasNoPeople
        // staying at some innocent default must never render "Nothing is stored" while enrollments
        // could be sitting on disk.
        Directory.CreateDirectory(Path.GetDirectoryName(Paths.PeopleJson)!);
        await File.WriteAllTextAsync(Paths.PeopleJson, """{"schemaVersion":99,"people":[]}""");

        var vm = await LoadedVmAsync();

        Assert.False(vm.HasNoPeople);
        Assert.True(vm.PeopleUnreadable);
        Assert.Empty(vm.People);
        Assert.Contains(_errors.Reports, r => r.Context == "Voiceprints");
    }

    [Fact]
    public async Task Composition_without_a_people_store_renders_nothing_not_the_empty_claim()
    {
        // Fix round 1, finding 1: the non-voiceprint unit tests (SettingsPageViewModelTests) build
        // the VM without paths/people at all - deliberately inert (design 2026-07-25). HasNoPeople
        // defaulting true would render the definitive "nothing is stored" claim on a screen this
        // composition never wired to know that.
        var paths = Paths;
        var maintenance = new MaintenanceService(paths, _settings, new FakeRecycleBin(), TimeProvider.System);
        var vm = new SettingsPageViewModel(_settings, maintenance, new FakeLaunchAtLogin(),
            pickFolder: () => null, openFolder: _ => { }, _errors,
            dispatch: _dispatcher.Dispatch, new FakeCaptureDeviceEnumerator(),
            modelsRoot: Path.Combine(_root, "models"), assistantHelperProbe: () => null);

        await vm.PeopleLoad;
        _dispatcher.Pump();

        Assert.False(vm.HasNoPeople);
        Assert.False(vm.PeopleUnreadable);
        Assert.Empty(vm.People);
    }

    [Fact]
    public async Task Refresh_picks_up_an_enrollment_made_on_another_surface()
    {
        // The Settings VM is built ONCE at app startup, but voiceprints are enrolled on the
        // Split-speakers dialog. Without a re-read on page navigation, the one screen whose job is
        // to say what is stored would show pre-enrollment counts forever (SettingsPage.Loaded
        // calls this, mirroring RefreshAssistantHelperNote).
        await SavePeopleAsync(MakePerson("p1", "Sarah Chen"));
        var vm = await LoadedVmAsync();
        Assert.Equal(0, vm.People[0].EnrollmentCount);

        await SavePeopleAsync(MakePerson("p1", "Sarah Chen",
            Enrollment("e1", EmbeddingMethods.CampPlus, 1)));

        await vm.RefreshPeopleAsync();
        _dispatcher.Pump();

        Assert.Equal(1, vm.People[0].EnrollmentCount);
    }

    // ---------- Deletion ----------

    [Fact]
    public async Task DeleteEnrollment_removes_only_the_oldest()
    {
        await SavePeopleAsync(MakePerson("p1", "Sarah Chen",
            Enrollment("old", EmbeddingMethods.CampPlus, 1),
            Enrollment("new", EmbeddingMethods.CampPlus, 5)));

        var vm = await LoadedVmAsync();
        await vm.DeleteEnrollmentCommand.ExecuteAsync(vm.People[0]);
        _dispatcher.Pump();

        var person = Assert.Single((await LoadPeopleAsync())!.People);
        Assert.Equal("new", Assert.Single(person.Voiceprint).Id);
        Assert.Equal(1, vm.People[0].EnrollmentCount);            // the list re-read disk
        Assert.Empty(_errors.Reports);
    }

    [Fact]
    public async Task DeleteVoiceprint_clears_enrollments_keeps_person()
    {
        await SavePeopleAsync(MakePerson("p1", "Sarah Chen",
            Enrollment("e1", EmbeddingMethods.CampPlus, 1)));

        var vm = await LoadedVmAsync();
        await vm.DeleteVoiceprintCommand.ExecuteAsync(vm.People[0]);
        _dispatcher.Pump();

        var person = Assert.Single((await LoadPeopleAsync())!.People);
        Assert.Equal("Sarah Chen", person.Name);                  // the person survives, named
        Assert.Empty(person.Voiceprint);
        Assert.Equal(0, vm.People[0].EnrollmentCount);
        Assert.Empty(_confirmPrompts);                            // not confirm-gated
    }

    [Fact]
    public async Task DeletePerson_requires_confirm_and_removes()
    {
        await SavePeopleAsync(MakePerson("p1", "Sarah Chen",
            Enrollment("e1", EmbeddingMethods.CampPlus, 1)));

        _confirmAnswer = false;
        var vm = await LoadedVmAsync();
        await vm.DeletePersonCommand.ExecuteAsync(vm.People[0]);
        _dispatcher.Pump();

        Assert.Single(_confirmPrompts);
        Assert.Contains("Sarah Chen", _confirmPrompts[0]);
        Assert.Single((await LoadPeopleAsync())!.People);          // declined -> unchanged
        Assert.Single(vm.People);

        _confirmAnswer = true;
        await vm.DeletePersonCommand.ExecuteAsync(vm.People[0]);
        _dispatcher.Pump();

        Assert.Empty((await LoadPeopleAsync())!.People);
        Assert.Empty(vm.People);
        Assert.True(vm.HasNoPeople);
        Assert.Empty(_errors.Reports);
    }

    [Fact]
    public async Task A_failing_delete_reports_through_the_error_reporter_and_never_throws()
    {
        await SavePeopleAsync(MakePerson("p1", "Sarah Chen",
            Enrollment("e1", EmbeddingMethods.CampPlus, 1)));
        var vm = await LoadedVmAsync();

        // A forward-versioned people.json makes every load throw from here on.
        await File.WriteAllTextAsync(Paths.PeopleJson, """{"schemaVersion":99,"people":[]}""");

        await vm.DeleteVoiceprintCommand.ExecuteAsync(vm.People[0]);
        _dispatcher.Pump();

        Assert.Contains(_errors.Reports, r => r.Context == "Voiceprints");
        // Fix round 1, finding 1: the reload after the failed mutation could not confirm anything
        // against disk. The pre-mutation row must not linger asserting a count disk no longer
        // agrees with, and the screen must not claim "nothing stored" either - the truth is
        // UNKNOWN, and that has to be a distinct, visible state.
        Assert.Empty(vm.People);
        Assert.False(vm.HasNoPeople);
        Assert.True(vm.PeopleUnreadable);
    }

    // ---------- Purge ----------

    [Fact]
    public async Task Purge_is_confirm_gated_and_states_what_is_not_deleted()
    {
        await SavePeopleAsync(MakePerson("p1", "Sarah Chen",
            Enrollment("e1", EmbeddingMethods.CampPlus, 1)));

        _confirmAnswer = false;
        var vm = await LoadedVmAsync();
        await vm.PurgeVoiceprintsCommand.ExecuteAsync(null);
        _dispatcher.Pump();

        string prompt = Assert.Single(_confirmPrompts);
        Assert.Equal(SettingsPageViewModel.PurgeConfirmMessage, prompt);
        Assert.Contains("keep their names", prompt);
        Assert.Contains("transcripts", prompt);
        Assert.Contains("speaker names", prompt);
        Assert.Contains("audio", prompt);
        Assert.Equal(1, vm.People[0].EnrollmentCount);            // declined -> nothing deleted
        Assert.Equal("", vm.PurgeStatus);
    }

    [Fact]
    public async Task Purge_calls_maintenance_and_reloads_empty_counts()
    {
        await SavePeopleAsync(MakePerson("p1", "Sarah Chen",
            Enrollment("e1", EmbeddingMethods.CampPlus, 1)));

        var vm = await LoadedVmAsync();
        await vm.PurgeVoiceprintsCommand.ExecuteAsync(null);
        _dispatcher.Pump();

        var person = Assert.Single((await LoadPeopleAsync())!.People);
        Assert.Empty(person.Voiceprint);
        Assert.Equal("Sarah Chen", person.Name);
        Assert.Equal(0, vm.People[0].EnrollmentCount);            // no stale count left on screen
        Assert.False(vm.PurgeIncomplete);
        Assert.Contains("Deleted", vm.PurgeStatus);
        Assert.Empty(_errors.Reports);
    }

    [Fact]
    public async Task Purge_that_could_not_strip_people_json_is_never_reported_as_success()
    {
        // THE load-bearing case. MaintenanceService collects a ("people.json", ...) failure when
        // the registry cannot be read - and then it does NOT strip the enrollments. The biometric
        // data is still on disk. Anything short of saying so plainly is a lie to the user.
        Directory.CreateDirectory(Path.GetDirectoryName(Paths.PeopleJson)!);
        await File.WriteAllTextAsync(Paths.PeopleJson,
            """{"schemaVersion":99,"people":[{"id":"p1","name":"Sarah Chen","voiceprint":[]}]}""");

        var vm = await LoadedVmAsync();
        await vm.PurgeVoiceprintsCommand.ExecuteAsync(null);
        _dispatcher.Pump();

        Assert.True(vm.PurgeIncomplete);
        Assert.DoesNotContain("Deleted all", vm.PurgeStatus);
        Assert.Contains("could NOT be deleted", vm.PurgeStatus);
        Assert.Contains("still", vm.PurgeStatus, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("people.json", vm.PurgeStatus);
    }

    [Fact]
    public void Purge_message_distinguishes_a_people_failure_from_a_session_failure()
    {
        var clean = SettingsPageViewModel.DescribePurge(new VoiceprintPurgeResult(3, []));
        Assert.False(clean.Incomplete);
        Assert.Contains("Deleted all saved voiceprints", clean.Message);
        Assert.Contains("3 session", clean.Message);

        // A per-session skip: one session's own voice data survives. The saved voiceprints - the
        // identifying part - are gone, and the message must not scare the user into thinking
        // otherwise, nor claim a clean purge.
        var oneSession = SettingsPageViewModel.DescribePurge(
            new VoiceprintPurgeResult(2, [("2026-07-25-abc", "locked")]));
        Assert.True(oneSession.Incomplete);
        Assert.Contains("saved voiceprints were deleted", oneSession.Message);
        Assert.Contains("1 session", oneSession.Message);
        Assert.Contains("2026-07-25-abc", oneSession.Message);
        Assert.DoesNotContain("could NOT be deleted", oneSession.Message);

        // The People strip was skipped: categorically different, and it must say the voiceprints
        // survived - never "deleted".
        var peopleFailed = SettingsPageViewModel.DescribePurge(
            new VoiceprintPurgeResult(2, [("people.json", "schemaVersion 99")]));
        Assert.True(peopleFailed.Incomplete);
        Assert.Contains("could NOT be deleted", peopleFailed.Message);
        Assert.Contains("still stored on this computer", peopleFailed.Message);
        Assert.DoesNotContain("Deleted all saved voiceprints", peopleFailed.Message);

        // The session sweep could not even be enumerated: SessionsTouched is not a count of
        // anything that actually happened, so it must not be voiced (fix round 1, finding 3) - or
        // "cleared from 0 session(s)" would sit right next to "no session's stored voice
        // measurements were deleted" and flatly contradict it.
        var noSweep = SettingsPageViewModel.DescribePurge(
            new VoiceprintPurgeResult(0, [("<sessions>", "access denied")]));
        Assert.True(noSweep.Incomplete);
        Assert.Contains("sessions folder", noSweep.Message);
        Assert.DoesNotContain("cleared from 0 session", noSweep.Message);

        // Both at once: the people.json wording must survive alongside the session wording, and
        // (fix round 1, finding 2) the sessions that WERE cleared despite the people.json failure
        // must still be mentioned - under-reporting a deletion is safe, but silently dropping the
        // sessions the purge actually did clear is not honest either.
        var both = SettingsPageViewModel.DescribePurge(
            new VoiceprintPurgeResult(1, [("2026-07-25-abc", "locked"), ("people.json", "bad")]));
        Assert.True(both.Incomplete);
        Assert.Contains("could NOT be deleted", both.Message);
        Assert.Contains("2026-07-25-abc", both.Message);
        Assert.Contains("cleared from 1 session(s)", both.Message);
    }

    [Fact]
    public async Task Purge_that_throws_outright_says_the_least_flattering_true_thing()
    {
        // Fix round 1, finding 4: force the registry rewrite ITSELF to throw (not just fail to
        // read) so the VM's outer catch - previously unpinned - actually fires. AtomicFile writes
        // a sibling ".tmp" first; pre-creating that path as a directory makes the write throw
        // before MaintenanceService's own try/catch scope even starts (its people.json load
        // succeeds fine here - only the save at the end is unguarded).
        await SavePeopleAsync(MakePerson("p1", "Sarah Chen",
            Enrollment("e1", EmbeddingMethods.CampPlus, 1)));
        Directory.CreateDirectory(Paths.PeopleJson + ".tmp");

        var vm = await LoadedVmAsync();
        await vm.PurgeVoiceprintsCommand.ExecuteAsync(null);
        _dispatcher.Pump();

        Assert.True(vm.PurgeIncomplete);
        Assert.Equal(
            "The purge did not finish. Some voiceprint data may still be stored on this computer - "
            + "check the error and try again.",
            vm.PurgeStatus);
        Assert.Contains(_errors.Reports, r => r.Context == "Voiceprints");
    }

    // ---------- Backfill ----------

    [Fact]
    public async Task Backfill_that_throws_outright_reports_the_scan_stopped_message()
    {
        // Fix round 1, finding 4: VoiceprintEnrollmentService.BackfillScanAsync's own top-level
        // registry load (unlike its per-session try) is not caught inside the service - a
        // forward-versioned people.json makes it throw before a single session is scanned, so the
        // VM's outer catch (previously unpinned) is the one that has to answer for it.
        await SeedBackfillSessionAsync(Paths, "s1");
        await SavePeopleAsync(MakePerson("p1", "Sarah Chen"));
        var vm = await LoadedVmAsync();

        await File.WriteAllTextAsync(Paths.PeopleJson, """{"schemaVersion":99,"people":[]}""");

        await vm.BackfillScanCommand.ExecuteAsync(null);
        _dispatcher.Pump();

        Assert.Equal(
            "The scan stopped early because of an error. Some sessions were not scanned.",
            vm.BackfillStatus);
        Assert.Contains(_errors.Reports, r => r.Context == "Voiceprints");
    }

    [Fact]
    public async Task Backfill_reports_scan_result()
    {
        await SeedBackfillSessionAsync(Paths, "s1");
        await SavePeopleAsync(MakePerson("p1", "Sarah Chen"));

        var vm = await LoadedVmAsync();
        await vm.BackfillScanCommand.ExecuteAsync(null);
        _dispatcher.Pump();

        Assert.Equal(1, _engine.Calls);
        Assert.Equal(
            Path.Combine(_root, "models", "3dspeaker_speech_campplus_sv_zh_en_16k-common_advanced.onnx"),
            _engine.ModelPaths[0]);
        Assert.Contains("Scanned 1 session", vm.BackfillStatus);
        Assert.Contains("enrolled 1", vm.BackfillStatus);
        Assert.Contains("skipped 0", vm.BackfillStatus);

        var person = Assert.Single((await LoadPeopleAsync())!.People);
        Assert.Equal("s1", Assert.Single(person.Voiceprint).SourceSessionId);
        Assert.Equal(1, vm.People[0].EnrollmentCount);            // the list re-read disk
        Assert.Empty(_errors.Reports);
    }

    // ---------- Re-entrancy ----------

    [Fact]
    public async Task Purge_cannot_start_while_a_backfill_is_in_flight()
    {
        // Final whole-branch review, finding I2. The commands were bare [RelayCommand]s with no
        // CanExecute and no busy flag. A purge fired mid-scan clears people.json and reports
        // "Deleted all saved voiceprints..." - and then the scan's terminal save, which by design
        // re-applies THIS scan's own enrollments onto a fresh reload, puts voiceprints back on disk
        // moments after a purge reported complete success. Deletion is first-class: it may never be
        // silently undone.
        await SeedBackfillSessionAsync(Paths, "s1");
        await SavePeopleAsync(MakePerson("p1", "Sarah Chen"));
        var vm = await LoadedVmAsync();

        _engine.Gate = new TaskCompletionSource();
        var backfill = vm.BackfillScanCommand.ExecuteAsync(null);

        Assert.True(vm.IsVoiceprintBusy);
        Assert.False(vm.PurgeVoiceprintsCommand.CanExecute(null));
        Assert.False(vm.BackfillScanCommand.CanExecute(null));
        Assert.False(vm.DeleteVoiceprintCommand.CanExecute(vm.People[0]));
        Assert.False(vm.DeleteEnrollmentCommand.CanExecute(vm.People[0]));
        Assert.False(vm.DeletePersonCommand.CanExecute(vm.People[0]));
        Assert.False(vm.VoiceprintCommandsEnabled);

        // What the button actually does: WPF's ButtonBase honours CanExecute, so a disabled purge
        // never reaches the VM at all - no confirmation prompt, no registry rewrite.
        if (vm.PurgeVoiceprintsCommand.CanExecute(null))
            await vm.PurgeVoiceprintsCommand.ExecuteAsync(null);
        _dispatcher.Pump();
        Assert.Empty(_confirmPrompts);
        Assert.Equal("", vm.PurgeStatus);

        _engine.Gate.SetResult();
        await backfill;
        _dispatcher.Pump();

        Assert.False(vm.IsVoiceprintBusy);
        Assert.True(vm.PurgeVoiceprintsCommand.CanExecute(null));
        Assert.True(vm.VoiceprintCommandsEnabled);
        Assert.Contains("enrolled 1", vm.BackfillStatus);            // the scan itself still completed
        Assert.Single((await LoadPeopleAsync())!.People[0].Voiceprint);
    }

    [Fact]
    public async Task Purge_confirmation_names_the_voiceprint_data_it_cannot_reach()
    {
        // Final whole-branch review, finding M2: the purge sweeps the sessions folder and
        // people.json. It cannot reach a session folder already sent to the Recycle Bin (its
        // embeddings.json went with it), and it cannot reach an export .zip that was created before
        // exports stopped carrying embeddings.json. Honest deletion copy must not promise more than
        // the action can deliver.
        string message = SettingsPageViewModel.PurgeConfirmMessage;
        Assert.Contains("Recycle Bin", message);
        Assert.Contains("export", message, StringComparison.OrdinalIgnoreCase);
        // ...without walking back what it DOES do, or what it never touches.
        Assert.Contains("keep their names", message);
        Assert.Contains("speaker names", message);

        _confirmAnswer = false;
        var vm = await LoadedVmAsync();
        await vm.PurgeVoiceprintsCommand.ExecuteAsync(null);
        _dispatcher.Pump();
        Assert.Equal(message, Assert.Single(_confirmPrompts));
    }

    /// <summary>A pre-feature session: diarised (speakers.json assignments) with a participant
    /// slot that durably owns the cluster, no embeddings.json, and a retained leg on disk.</summary>
    private static async Task SeedBackfillSessionAsync(StoragePaths paths, string id)
    {
        Directory.CreateDirectory(paths.SessionDir(id));
        await new SessionStore(paths.SessionJson(id)).SaveAsync(new SessionRecord
        {
            Id = id,
            StartedAtUtc = DateTimeOffset.UnixEpoch,
            EndedAtUtc = DateTimeOffset.UnixEpoch.AddMinutes(1),
            RetainedAudioSources = [SourceKind.Remote],
        }, default);
        await new MetadataStore(paths.MetaJson(id)).SaveAsync(new SessionMeta
        {
            LocalCount = 1,
            RemoteCount = 2,
            Participants =
            [
                new SessionParticipant
                {
                    Id = "pp1", Name = "Sarah Chen", Side = SourceKind.Remote, ClusterKey = "Remote:0",
                },
            ],
        }, default);
        await new TranscriptStore(paths.TranscriptJsonl(id)).AppendAsync(
            TranscriptLine.Segment(1, TranscriptSource.Remote, 0, 1000, "hello", "Them"), default);
        await new SpeakersStore(paths.SpeakersJson(id, TranscriptVersions.Root)).SaveAsync(new Speakers
        {
            Assignments = new Dictionary<string, Dictionary<string, string>>
            {
                ["Remote"] = new() { ["1"] = "Remote:0" },
            },
            DiarisedSources = [SourceKind.Remote],
        }, default);
        await File.WriteAllBytesAsync(paths.AudioFile(id, SourceKind.Remote, AudioFormat.Flac), [1, 2, 3]);
    }
}
