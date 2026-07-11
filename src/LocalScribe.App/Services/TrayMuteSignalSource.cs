using System.Windows.Automation;
namespace LocalScribe.App.Services;

/// <summary>Reads the Windows 11 call-mute tray signal (design 2026-07-11 section 2.1): finds
/// the taskbar mic NotifyItemIcon and parses its UIA Name. Fail-open by contract: any absence,
/// walk failure, or unparseable text is an Unknown reading - a UIA hiccup must never affect
/// recording. Smoke-verified via the runbook procedure; the parser carries the unit tests.</summary>
public sealed class TrayMuteSignalSource : IAppMuteSignalSource
{
    public AppMuteReading Read()
    {
        try
        {
            var tray = AutomationElement.RootElement.FindFirst(TreeScope.Children,
                new PropertyCondition(AutomationElement.ClassNameProperty, "Shell_TrayWnd"));
            if (tray is null) return new(AppMuteState.Unknown, null);
            var buttons = tray.FindAll(TreeScope.Descendants,
                new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Button));
            foreach (AutomationElement b in buttons)
            {
                var reading = TrayTextParser.Parse(b.Current.Name);
                if (reading.State != AppMuteState.Unknown) return reading;
            }
            return new(AppMuteState.Unknown, null);
        }
        catch
        {
            return new(AppMuteState.Unknown, null);
        }
    }
}
