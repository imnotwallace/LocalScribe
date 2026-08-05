using System;
using LocalScribe.App.Services;
using Xunit;

namespace LocalScribe.App.Tests;

/// <summary>ShutdownFlush (Tier 1 plan A, 2026-08-05, fix round 1) is a plain constant, not a WPF
/// type, specifically so the actual ceiling value has a real unit test rather than only the
/// source-text pins in DiagnosticsWiringTests that check App.xaml.cs/TrayIconHost.cs reference
/// it.</summary>
public sealed class ShutdownFlushTests
{
    [Fact]
    public void Timeout_is_two_seconds()
    {
        Assert.Equal(TimeSpan.FromSeconds(2), ShutdownFlush.Timeout);
    }
}
