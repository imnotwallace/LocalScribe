using LocalScribe.Core.Audio;
using LocalScribe.Core.Model;
using LocalScribe.Core.Storage;

namespace LocalScribe.Core.Tests;

public class MetadataStoreTests
{
    [Fact]
    public async Task Roundtrips_participants_and_matter_tags()
    {
        var meta = new SessionMeta
        {
            Title = "Doe intake \u2014 Webex",
            Description = "Initial client interview; custody status.",
            Medium = Medium.Webex,
            MatterIds = new[] { "M-2026-014" },
            Participants = new[]
            {
                new SessionParticipant { Id = "p-self", Name = "Sam", Side = SourceKind.Local, Role = "Attorney", IsSelf = true },
                new SessionParticipant { Id = "p-alice", Name = "Alice Client", Side = SourceKind.Remote, Role = "Client" },
            },
            LocalCount = 1, RemoteCount = 1,
        };
        string path = Path.Combine(Path.GetTempPath(), $"ls_{Guid.NewGuid():N}", "meta.json");
        try
        {
            var store = new MetadataStore(path);
            await store.SaveAsync(meta, default);
            string json = await File.ReadAllTextAsync(path);
            Assert.Contains("\"side\": \"Local\"", json);
            Assert.Contains("\"isSelf\": true", json);
            Assert.DoesNotContain("clusterKey", json);          // null -> omitted

            var back = await store.LoadAsync(default);
            Assert.Equal("M-2026-014", back!.MatterIds[0]);
            Assert.Equal(2, back.Participants.Count);
            Assert.True(back.Participants[0].IsSelf);
        }
        finally { CleanParent(path); }
    }

    [Fact]
    public void CreateDefault_derives_title_medium_and_self_participant()
    {
        var self = new SessionParticipant { Id = "p-self", Name = "Sam", Side = SourceKind.Local, IsSelf = true };
        var startedLocal = new DateTimeOffset(2026, 7, 2, 14, 32, 0, TimeSpan.FromHours(8));
        var meta = SessionMeta.CreateDefault(AppKind.Webex, startedLocal, self);

        Assert.Equal("Webex \u2014 2026-07-02 14:32", meta.Title);
        Assert.Equal(Medium.Webex, meta.Medium);
        Assert.Single(meta.Participants);
        Assert.True(meta.Participants[0].IsSelf);
        Assert.Equal(1, meta.LocalCount);
        Assert.Equal(1, meta.RemoteCount);
    }

    [Fact]
    public void CreateDefault_falls_back_to_Other_medium_for_non_medium_app()
    {
        var meta = SessionMeta.CreateDefault(AppKind.Manual, new DateTimeOffset(2026, 7, 2, 9, 0, 0, TimeSpan.Zero), self: null);
        Assert.Equal(Medium.Other, meta.Medium);
        Assert.Empty(meta.Participants);
    }

    private static void CleanParent(string path)
    {
        string? dir = Path.GetDirectoryName(path);
        if (dir is not null && Directory.Exists(dir)) Directory.Delete(dir, true);
    }
}