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

    private static readonly DateOnly Day1 = new(2026, 1, 1);
    private static readonly DateOnly Day2 = new(2026, 1, 2);

    [Fact]
    public void Empty_index_and_dir_mints_001()
        => Assert.Equal("M-20260101-001", MatterIdGenerator.Next(Index(), _mattersDir, Day1));

    [Fact]
    public void Increments_past_the_days_max()
        => Assert.Equal("M-20260101-004",
            MatterIdGenerator.Next(Index("M-20260101-001", "M-20260101-003"), _mattersDir, Day1));

    [Fact]
    public void Day_rollover_restarts_at_001()
        => Assert.Equal("M-20260102-001",
            MatterIdGenerator.Next(Index("M-20260101-007", "M-20260101-008"), _mattersDir, Day2));

    [Fact]
    public void Orphan_folder_missing_from_index_is_skipped()
    {
        // Folder exists but the index lost its entry (the crash window MatterStore.cs
        // documents): the id must not be reissued - it doubles as the folder name.
        Directory.CreateDirectory(Path.Combine(_mattersDir, "M-20260101-001"));
        Assert.Equal("M-20260101-002", MatterIdGenerator.Next(Index(), _mattersDir, Day1));
    }

    [Fact]
    public void Increments_until_both_index_and_folder_are_free()
    {
        Directory.CreateDirectory(Path.Combine(_mattersDir, "M-20260101-002"));
        Assert.Equal("M-20260101-003",
            MatterIdGenerator.Next(Index("M-20260101-001"), _mattersDir, Day1));
    }

    [Fact]
    public void Malformed_and_foreign_ids_are_ignored()
        => Assert.Equal("M-20260101-001",
            MatterIdGenerator.Next(Index("M-20260101-xyz", "CASE-9", "M-20260101-"), _mattersDir, Day1));

    [Fact]
    public void Next_is_per_day_and_resets_across_days()
    {
        var day = new DateOnly(2026, 7, 5);
        var index = new MattersIndex { Matters = new List<MattersIndexEntry>
        {
            new() { Id = "M-20260705-001", Name = "A" },
            new() { Id = "M-20260705-002", Name = "B" },
            new() { Id = "M-2026-050",     Name = "legacy" },   // year-format id must NOT interfere
        }};
        Assert.Equal("M-20260705-003", MatterIdGenerator.Next(index, _mattersDir, day));
        Assert.Equal("M-20260706-001", MatterIdGenerator.Next(index, _mattersDir, new DateOnly(2026, 7, 6)));
    }
}
