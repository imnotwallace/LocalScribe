using System.IO;
using LocalScribe.Core.Live;
using LocalScribe.Core.Model;
using Xunit;

namespace LocalScribe.Core.Tests;

/// <summary>Task 3 (Fix #1): Start fail-fasts when the resolved model isn't present, instead of
/// creating a session that will later fault lazily on the worker and get lost as dead air. Uses
/// LiveTestDoubles.MakeController's injectable Func&lt;IReadOnlySet&lt;string&gt;&gt; seam rather
/// than the LOCALSCRIBE_MODELS env var: that var is process-global and xUnit runs test classes in
/// parallel, so two classes touching it would race and cross-contaminate each other's models dir.</summary>
public sealed class SessionControllerModelTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "ls-scm-" + Guid.NewGuid().ToString("N"));
    public void Dispose() { try { Directory.Delete(_root, true); } catch { } }

    [Fact]
    public async Task Start_refuses_when_the_resolved_model_is_absent()
    {
        // Explicit pick "base.en", but the injected available-models set is empty -> must refuse, no session.
        var settings = new Settings { Model = "base.en" };
        var (c, _, paths, _) = LiveTestDoubles.MakeController(_root, settings,
            availableModels: new HashSet<string>());
        string? notice = null;
        c.Notice += n => notice = n;

        string? id = await c.StartAsync(LiveTestDoubles.Options(), CancellationToken.None);

        Assert.Null(id);                                  // refused
        Assert.Equal(SessionState.Idle, c.State);
        Assert.False(Directory.Exists(paths.SessionsDir) && Directory.EnumerateDirectories(paths.SessionsDir).Any());
        Assert.NotNull(notice);
        Assert.Contains("not downloaded", notice!);
    }

    [Fact]
    public async Task Start_proceeds_and_notices_a_downgrade_when_present()
    {
        // Auto on a CUDA box wants small.en; only base.en present -> downgrade + record.
        var settings = new Settings { Model = "auto", Backend = Backend.Cuda };
        var (c, _, _, _) = LiveTestDoubles.MakeController(_root, settings,
            availableModels: new HashSet<string> { "base.en" });   // fake provider + fake engine (no real model load)
        string? notice = null;
        c.Notice += n => notice ??= n?.Contains("better accuracy") == true ? n : notice;

        string? id = await c.StartAsync(LiveTestDoubles.Options(), CancellationToken.None);
        await c.StopAsync(CancellationToken.None);

        Assert.NotNull(id);                               // recorded
        Assert.NotNull(notice);                           // downgrade notice emitted
    }
}
