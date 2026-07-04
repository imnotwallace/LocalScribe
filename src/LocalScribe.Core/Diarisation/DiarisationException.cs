namespace LocalScribe.Core.Diarisation;

public enum DiarisationErrorCode { ModelDownloadFailed, BadAudio, HelperCrash }

public sealed class DiarisationException(DiarisationErrorCode code, string message)
    : Exception(message)
{
    public DiarisationErrorCode Code { get; } = code;
}
