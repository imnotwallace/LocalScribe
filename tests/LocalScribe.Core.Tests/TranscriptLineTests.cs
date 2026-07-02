using System.Text.Json;
using LocalScribe.Core.Model;
using LocalScribe.Core.Storage;

public class TranscriptLineTests
{
    [Fact]
    public void Segment_serializes_with_spec_fields_and_camelCase()
    {
        var seg = TranscriptLine.Segment(17, TranscriptSource.Remote, 85320, 89110,
            "I pushed the auth changes last night.", "Them", lang: "en", noSpeechProb: 0.02);
        string json = JsonSerializer.Serialize(seg, LocalScribeJson.Options);

        Assert.Contains("\"seq\": 17", json);
        Assert.Contains("\"kind\": \"segment\"", json);
        Assert.Contains("\"source\": \"Remote\"", json);
        Assert.Contains("\"speakerLabel\": \"Them\"", json);
        Assert.Contains("\"noSpeechProb\": 0.02", json);
    }

    [Fact]
    public void Legacy_line_without_kind_reads_as_segment()
    {
        // The Stage-1 design example line carries no "kind" field.
        string line = "{\"seq\":17,\"source\":\"Remote\",\"startMs\":85320,\"endMs\":89110,"
                    + "\"text\":\"hi\",\"speakerLabel\":\"Them\"}";
        var seg = JsonSerializer.Deserialize<TranscriptLine>(line, LocalScribeJson.Options)!;
        Assert.Equal(TranscriptKind.Segment, seg.Kind);
        Assert.Equal(TranscriptSource.Remote, seg.Source);
    }

    [Fact]
    public void Marker_has_equal_start_end_system_source_and_no_speaker_label()
    {
        var m = TranscriptLine.Marker(40, 91000, Markers.AudioDeviceChanged);
        Assert.Equal(TranscriptKind.Marker, m.Kind);
        Assert.Equal(TranscriptSource.System, m.Source);
        Assert.Equal(91000, m.StartMs);
        Assert.Equal(91000, m.EndMs);
        Assert.Equal("audio device changed", m.Text);

        string json = JsonSerializer.Serialize(m, LocalScribeJson.Options);
        Assert.DoesNotContain("speakerLabel", json);   // null -> omitted
    }

    [Fact]
    public void Pinned_mic_marker_renders_arrow_glyph()
    {
        Assert.Equal("pinned microphone unavailable \u2192 default", Markers.PinnedMicUnavailable);
    }

    [Fact]
    public void RmsDb_roundtrips_and_is_omitted_when_null()
    {
        var with = TranscriptLine.Segment(1, TranscriptSource.Local, 0, 500, "hi", "Me", rmsDb: -23.4);
        string json = JsonSerializer.Serialize(with, LocalScribeJson.Options);
        Assert.Contains("\"rmsDb\": -23.4", json);

        var without = TranscriptLine.Segment(2, TranscriptSource.Local, 0, 500, "hi", "Me");
        Assert.DoesNotContain("rmsDb", JsonSerializer.Serialize(without, LocalScribeJson.Options));

        var back = JsonSerializer.Deserialize<TranscriptLine>(json, LocalScribeJson.Options)!;
        Assert.Equal(-23.4, back.RmsDb);
    }
}
