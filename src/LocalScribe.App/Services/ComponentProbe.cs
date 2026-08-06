using System.IO;
using System.Linq;
using LocalScribe.Core.Assistant;
using LocalScribe.Core.Import;

namespace LocalScribe.App.Services;

/// <summary>One row of the Settings Components panel (Tier 1 plan D, T1-10, 2026-08-05). Pin is
/// null for a component that is not downloadable in-app - the panel then shows Detail as the
/// remedy instead of a Download button that could not work.</summary>
public sealed record ComponentState(string Id, string Name, bool Installed, long Bytes,
    string? Detail, ComponentPin? Pin);

/// <summary>Assembles installed/missing state for every component the product depends on
/// (Tier 1 plan D, T1-10, 2026-08-05). It INVENTS no detection: every probe below already
/// existed and is reached through an injected delegate, both so the panel and the feature agree
/// about what "installed" means and so this class never reads the developer's real machine
/// during a test run.
///
/// ffmpeg, the diarizer helper and the assistant helper are PROBE-ONLY ROWS. ffmpeg comes from
/// tools/fetch-ffmpeg.ps1 and the two helpers are published by build.ps1 into the installer, so
/// there is no pinned blob to fetch for the ROW itself and offering a Download button on it would
/// be a lie. The assistant's WEIGHTS are pinned separately and appear as their own downloadable
/// rows - see AssistantChatPinId below.</summary>
public sealed class ComponentProbe(
    Func<string, string> resolveModel,
    Func<string?> findFfmpeg,
    Func<string?> findAssistant,
    string diarizerExe,
    Func<string, long?> fileBytes)
{
    /// <summary>The manifest id of the assistant's chat model (Tier 1 plan D, T1-10, 2026-08-05).
    /// tools/fetch-models.ps1 -WriteComponentManifest writes this id; the assistant row looks the
    /// pin up by it rather than naming a .gguf here, so a model swap is a one-line change in the
    /// script and not a silent disagreement between the panel and the fetch tooling.</summary>
    public const string AssistantChatPinId = "assistant-chat";

    public IReadOnlyList<ComponentState> Probe(IReadOnlyList<ComponentPin> pins)
    {
        var rows = new List<ComponentState>();

        foreach (var pin in pins)
        {
            long? bytes = fileBytes(resolveModel(pin.File));
            // Installed rows show the MEASURED size; missing rows show the manifest figure, so a
            // user can decide whether to spend it before starting.
            rows.Add(new ComponentState(pin.Id, pin.Name, bytes is > 0, bytes ?? pin.Bytes,
                Detail: null, Pin: pin));
        }

        string? ffmpeg = findFfmpeg();
        rows.Add(new ComponentState("ffmpeg", "ffmpeg / ffprobe (audio import)",
            ffmpeg is not null, 0,
            ffmpeg is null ? FfmpegLocator.MissingMessage : null, Pin: null));

        // DiarisationAvailability.Probe returns a user-facing reason or null; it also covers the
        // two sherpa models, so this single row answers "can Split Speakers run at all".
        string? diarisation = DiarisationAvailability.Probe(resolveModel, diarizerExe);
        rows.Add(new ComponentState("diarizer", "Speaker detection (diarizer + models)",
            diarisation is null, 0, diarisation, Pin: null));

        // The assistant needs BOTH halves. build.ps1 publishes the helper into the installer, but
        // its ~2.5 GB chat model is deliberately NOT bundled - that is precisely what this panel
        // exists for - so probing the exe ALONE would paint a green "installed" row for a feature
        // that cannot answer a single question on a clean machine. The model is located through
        // the PIN, never a file name hardcoded here.
        string? assistant = findAssistant();
        var chatPin = pins.FirstOrDefault(p => p.Id == AssistantChatPinId);
        bool chatModel = chatPin is not null && fileBytes(resolveModel(chatPin.File)) is > 0;
        rows.Add(new ComponentState("assistant", "Assistant helper",
            assistant is not null && chatModel, 0,
            AssistantDetail(assistant, chatPin, chatModel), Pin: null));

        return rows;
    }

    /// <summary>The assistant row's remedy text - never null while something is missing, so the
    /// panel never shows a blank cell beside a red row. Says WHICH half is absent: "the helper is
    /// missing" and "the model is missing" have completely different fixes, and only one of them
    /// is a button in this panel.</summary>
    private static string? AssistantDetail(string? helper, ComponentPin? chatPin, bool chatModel)
    {
        if (helper is null) return AssistantHelperLocator.MissingMessage;
        if (chatModel) return null;
        return chatPin is null
            ? "The assistant helper is installed but its language model is not, and this build "
              + "carries no component list - run tools\\fetch-models.ps1 -Assistant."
            : "The assistant helper is installed but its language model is not - download \""
              + chatPin.Name + "\" below.";
    }

    /// <summary>Production file-size probe: absent, unreadable or ZERO-BYTE all read as absent -
    /// a truncated download is not a usable component, which is the same test
    /// DiarisationAvailability and tools/verify-*.ps1 already apply.</summary>
    public static long? MeasureFile(string path)
    {
        try
        {
            var info = new FileInfo(path);
            return info.Exists && info.Length > 0 ? info.Length : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return null; }
    }
}
