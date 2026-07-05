using System;
using LocalScribe.App.ViewModels;
using LocalScribe.Core.Model;
using LocalScribe.Core.Storage;
using Xunit;

namespace LocalScribe.App.Tests;

public class SessionRowSourceTests
{
    // SessionRowViewModel's ctor (SessionListItem, TimeProvider) is public, so this builds the
    // row directly rather than round-tripping through disk/SessionsPageViewModel (see
    // SessionsPageViewModelTests.cs's Rec/Meta helpers for the field pattern this mirrors).
    // Only fields the AppMedium/IsSystemMix derivation touches are set.
    private static SessionRowViewModel MakeRow(AppKind app, bool systemMix)
    {
        var started = new DateTimeOffset(2026, 7, 3, 9, 0, 0, TimeSpan.Zero);
        var session = new SessionRecord
        {
            Id = "s-source", App = app, StartedAtUtc = started,
            EndedAtUtc = started.AddMinutes(30), UtcOffsetMinutes = 0, DurationMs = 1_800_000,
            Model = "small", Backend = "cpu", Language = "en",
            Devices = new DeviceSnapshot
            {
                Remote = new RemoteSnapshot
                {
                    // 3.2: chosen system-mix (RemoteMode.SystemMix), not the fallback flag, is
                    // what this test exercises - see SessionRowViewModel.cs's IsSystemMix.
                    Mode = systemMix ? RemoteMode.SystemMix : RemoteMode.PerProcess,
                },
            },
        };
        var meta = new SessionMeta
        {
            Title = "T",
            Medium = Enum.TryParse(app.ToString(), out Medium m) ? m : Medium.Other,
        };
        var item = new SessionListItem("s-source", session, meta);
        return new SessionRowViewModel(item, TimeProvider.System);
    }

    [Fact]
    public void Source_folds_system_mix_into_the_label()
    {
        var mix = MakeRow(app: AppKind.Webex, systemMix: true);
        var iso = MakeRow(app: AppKind.Webex, systemMix: false);
        Assert.Equal("Webex \u2014 system mix", mix.Source);
        Assert.Equal("Webex \u2014 per-app", iso.Source);
    }

    [Fact]
    public void StartedAtUtc_exposes_the_raw_start_instant_for_age_sorting()
    {
        // The Date grid column sorts by StartedAtUtc (the true instant), so age-sorting is
        // chronological rather than lexicographic over the formatted DateDisplay string.
        var row = MakeRow(app: AppKind.Webex, systemMix: false);
        Assert.Equal(new DateTimeOffset(2026, 7, 3, 9, 0, 0, TimeSpan.Zero), row.StartedAtUtc);
    }

    [Fact]
    public void SourceTooltip_carries_full_text_and_only_system_mix_gets_the_caveat()
    {
        var mix = MakeRow(app: AppKind.Webex, systemMix: true);
        var iso = MakeRow(app: AppKind.Webex, systemMix: false);
        // Per-app: just the accurate full label, so a trimmed cell stays recoverable on hover -
        // never the false "fell back" claim (final-review FIX 2 invariant).
        Assert.Equal("Webex — per-app", iso.SourceTooltip);
        // System-mix: full Source text + the evidentiary caveat on a second line.
        Assert.Equal(
            "Webex — system mix\nSystem mix was the selected capture mode; other app audio may be included",
            mix.SourceTooltip);
    }
}
