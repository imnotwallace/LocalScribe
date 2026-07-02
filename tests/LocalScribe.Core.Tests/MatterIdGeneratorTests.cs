using LocalScribe.Core.Model;
using LocalScribe.Core.Storage;

public sealed class MatterIdGeneratorTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"ls_midgen_{Guid.NewGuid():N}");
    private readonly string _mattersDir;

    public MatterIdGeneratorTests()
    {
        _mattersDir = Path.Combine(_root, "matters");
        Directory.CreateDirectory(_mattersDir);
    }

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, true);
    }

    private static MattersIndex Index(params string[] ids) => new()
    {
        Matters = ids.Select(id => new MattersIndexEntry { Id = id, Name = id }).ToList(),
    };

    [Fact]
    public void Empty_index_and_dir_mints_001()
        => Assert.Equal("M-2026-001", MatterIdGenerator.Next(Index(), _mattersDir, 2026));

    [Fact]
    public void Increments_past_the_years_max()
        => Assert.Equal("M-2026-004",
            MatterIdGenerator.Next(Index("M-2026-001", "M-2026-003"), _mattersDir, 2026));

    [Fact]
    public void Year_rollover_restarts_at_001()
        => Assert.Equal("M-2026-001",
            MatterIdGenerator.Next(Index("M-2025-007", "M-2025-008"), _mattersDir, 2026));

    [Fact]
    public void Orphan_folder_missing_from_index_is_skipped()
    {
        // Folder exists but the index lost its entry (the crash window MatterStore.cs
        // documents): the id must not be reissued - it doubles as the folder name.
        Directory.CreateDirectory(Path.Combine(_mattersDir, "M-2026-001"));
        Assert.Equal("M-2026-002", MatterIdGenerator.Next(Index(), _mattersDir, 2026));
    }

    [Fact]
    public void Increments_until_both_index_and_folder_are_free()
    {
        Directory.CreateDirectory(Path.Combine(_mattersDir, "M-2026-002"));
        Assert.Equal("M-2026-003",
            MatterIdGenerator.Next(Index("M-2026-001"), _mattersDir, 2026));
    }

    [Fact]
    public void Malformed_and_foreign_ids_are_ignored()
        => Assert.Equal("M-2026-001",
            MatterIdGenerator.Next(Index("M-2026-xyz", "CASE-9", "M-2026-"), _mattersDir, 2026));
}
