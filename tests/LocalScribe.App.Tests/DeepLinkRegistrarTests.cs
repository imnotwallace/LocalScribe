using LocalScribe.App.Services;
using Xunit;

namespace LocalScribe.App.Tests;

public class DeepLinkRegistrarTests
{
    // The registry write itself is a humble object (RegistryLaunchAtLogin precedent) verified by
    // the smoke runbook; the VALUE composition is pure and pinned here: the exe path AND %1 are
    // both quoted, so paths with spaces and URLs survive shell argument splitting intact.

    [Fact]
    public void RegistrationValues_quote_the_exe_and_the_url_placeholder()
    {
        var (label, command) = DeepLinkRegistrar.RegistrationValues(
            @"C:\Program Files\LocalScribe\LocalScribe.App.exe");
        Assert.Equal("URL:LocalScribe deep link", label);
        Assert.Equal("\"C:\\Program Files\\LocalScribe\\LocalScribe.App.exe\" \"%1\"", command);
    }

    [Fact]
    public void EnsureRegistered_is_a_safe_no_op_on_a_missing_exe_path()
    {
        // Best-effort contract: never throws, never blocks startup.
        DeepLinkRegistrar.EnsureRegistered(null);
        DeepLinkRegistrar.EnsureRegistered("");
    }

    [Fact]
    public void SchemeKeyPath_is_pinned_to_the_HKCU_classes_location()
        // Consent-critical: this is the exact registry location EnsureRegistered writes under
        // (HKCU\Software\Classes\localscribe - never elevates, never touches HKLM). The write
        // path itself is a humble object verified by the smoke runbook; pin the location here.
        => Assert.Equal(@"Software\Classes\localscribe", DeepLinkRegistrar.SchemeKeyPath);
}
