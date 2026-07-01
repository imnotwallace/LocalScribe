// src/LocalScribe.Core/Model/Enums.cs
using System.Text.Json.Serialization;
namespace LocalScribe.Core.Model;

public enum AppKind { Teams, Zoom, Webex, Manual, Browser }

public enum Medium
{
    Webex, Zoom, Teams, Phone,
    [JsonStringEnumMemberName("In-person")] InPerson,
    Other,
}

public enum RemoteMode
{
    [JsonStringEnumMemberName("auto")] Auto,
    [JsonStringEnumMemberName("perProcess")] PerProcess,
    [JsonStringEnumMemberName("systemMix")] SystemMix,
}

public enum MicMode
{
    [JsonStringEnumMemberName("followDefault")] FollowDefault,
    [JsonStringEnumMemberName("pinned")] Pinned,
}

public enum Backend
{
    [JsonStringEnumMemberName("auto")] Auto,
    [JsonStringEnumMemberName("cuda")] Cuda,
    [JsonStringEnumMemberName("vulkan")] Vulkan,
    [JsonStringEnumMemberName("cpu")] Cpu,
}

public enum AudioFormat
{
    [JsonStringEnumMemberName("flac")] Flac,
    [JsonStringEnumMemberName("wav")] Wav,
}

public enum TranscriptKind
{
    [JsonStringEnumMemberName("segment")] Segment,
    [JsonStringEnumMemberName("marker")] Marker,
}
