using LocalScribe.App.Services;
using LocalScribe.Core.Diagnostics;
using LocalScribe.Core.Model;
using LocalScribe.Core.Storage;

namespace LocalScribe.App.Tests;

/// <summary>Synchronous ISettingsService: SaveAsync swaps Current and raises Changed inline,
/// so VM commits are deterministic in tests (no SpinWait needed on Current).</summary>
public sealed class FakeSettingsService : ISettingsService
{
    public FakeSettingsService(Settings? initial = null) => Current = initial ?? new Settings();
    public Settings Current { get; private set; }
    public int SaveCount { get; private set; }
    public event Action<Settings, Settings>? Changed;

    public Task SaveAsync(Settings updated, CancellationToken ct)
    {
        var old = Current;
        Current = updated;
        SaveCount++;
        Changed?.Invoke(old, updated);
        return Task.CompletedTask;
    }
}

public sealed class FakeUiErrorReporter : IUiErrorReporter
{
    public readonly List<(string Context, Exception Ex)> Reports = new();
    public readonly List<string> Infos = new();
    /// <summary>Severity of Infos[i], same index (Tier 1 plan D, T1-5). The 23 per-file private
    /// reporter fakes deliberately stay narrow - they already carry Plan A's
    /// Info(string message, bool privileged = true), and the interface's default method forwards
    /// the severity overload to that one for them, so their existing Infos assertions are
    /// unaffected and none of them needs a SECOND edit.</summary>
    public readonly List<NoticeSeverity> InfoSeverities = new();

    public void Report(string context, Exception ex) => Reports.Add((context, ex));
    // Plan A's shipped member - the trailing `bool privileged = true` is required to implement the
    // interface (CS0535 without it) and every existing one-argument Info("...") call still binds
    // here via the default.
    public void Info(string message, bool privileged = true) => Info(message, NoticeSeverity.Informational);
    public void Info(string message, NoticeSeverity severity)
    {
        Infos.Add(message);
        InfoSeverities.Add(severity);
    }
}

public sealed class FakeRecycleBin : IRecycleBin
{
    public readonly List<string> Recycled = new();
    public void SendToRecycleBin(string path) => Recycled.Add(path);
}

public sealed class FakeLaunchAtLogin : ILaunchAtLogin
{
    public bool Enabled = true;
    public readonly List<bool> SetCalls = new();
    public bool IsEnabled() => Enabled;
    public void SetEnabled(bool on) { Enabled = on; SetCalls.Add(on); }
}

/// <summary>Records diagnostic lines in memory. Lives in this shared file rather than being
/// re-declared per test class - the "no cross-file test helper" convention covers fakes ONE class
/// needs, and four separate classes need this one (Tier 1 plan A, 2026-08-05).
///
/// F15 (final whole-branch review, 2026-08-05): a <c>Flushes</c> counter used to sit here, with a
/// doc claiming "an exit-path test can prove the flush happened". No test anywhere ever read it -
/// it was a leftover from an approach that source-text pins replaced, because BOTH exit paths live
/// in files with no unit coverage at all (App.xaml.cs and TrayIconHost.cs), so the flush is pinned
/// by DiagnosticsWiringTests reading their source instead. DELETED rather than wired up: a fake
/// that advertises coverage nobody has is worse than no fake at all.</summary>
public sealed class FakeDiagnosticLog : IDiagnosticLog
{
    public readonly List<(string Level, string Source, string Message, string? Detail)> Entries = new();

    public void Write(string level, string source, string message, string? detail = null)
        => Entries.Add((level, source, message, detail));

    public Task FlushAsync(CancellationToken ct) => Task.CompletedTask;
}
