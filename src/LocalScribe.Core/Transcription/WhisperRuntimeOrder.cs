using LocalScribe.Core.Model;
using Whisper.net.LibraryLoader;

namespace LocalScribe.Core.Transcription;

/// <summary>Maps the persisted <see cref="Backend"/> setting to the whisper.cpp native load order
/// (2026-08-11). Before this existed the setting constrained NOTHING: all three hosts assigned
/// RuntimeOptions.RuntimeLibraryOrder an unconditional [Cuda, Vulkan, Cpu] literal and
/// BackendPlan.Backend only ever chose the weights FILE and the CPU thread count, so picking "cpu"
/// on a CUDA box recorded "cpu" into session.json and the engine chip while whisper.cpp ran CUDA.
/// A backend line that ships in export provenance has to be true.
///
/// APPLIED ONCE PER PROCESS, BY THE HOST, BEFORE ANY ENGINE EXISTS. Whisper.net documents that
/// RuntimeOptions only takes effect "before any WhisperFactory is created" and that once a library
/// is loaded it serves all subsequent processing. Re-applying this per session would therefore
/// silently no-op after the first engine while LOOKING like it worked - the worst available
/// outcome - so changing the setting requires a restart, and Settings says so.
///
/// CPU stays reachable from an explicit GPU choice. "Recording always wins" outranks honouring a
/// picker: a machine whose GPU driver vanishes must still produce a transcript. The rule this
/// preserves is not "always obey the picker" but "never fall silently" - which is why what
/// actually loaded is reported rather than inferred from this order.</summary>
public static class WhisperRuntimeOrder
{
    /// <summary>Returns a fresh <see cref="List{T}"/> because that is the exact type
    /// RuntimeOptions.RuntimeLibraryOrder exposes - a new list per call, so a caller mutating what
    /// it was given cannot reach back into this table.</summary>
    public static List<RuntimeLibrary> For(Backend backend) => backend switch
    {
        // Vulkan excluded: an explicit CUDA choice must not quietly land on a different GPU stack.
        Backend.Cuda => [RuntimeLibrary.Cuda, RuntimeLibrary.Cpu],
        Backend.Vulkan => [RuntimeLibrary.Vulkan, RuntimeLibrary.Cpu],
        // No GPU entry at all - the case the old literal got wrong.
        Backend.Cpu => [RuntimeLibrary.Cpu],
        // Auto keeps the documented spec-section-3 cascade.
        _ => [RuntimeLibrary.Cuda, RuntimeLibrary.Vulkan, RuntimeLibrary.Cpu],
    };
}
