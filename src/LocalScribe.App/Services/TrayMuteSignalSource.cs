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
                // Guard EACH element read individually: a stale tray icon of a dead process throws
                // COMException 0x80040201 on ANY property read (commit 7034f73 - "UiaProbe survives
                // poisoned UIA elements", observed live 2026-07-11). Without this inner try/catch,
                // one ghost button anywhere in the collection would trip the outer catch and return
                // Unknown for the whole poll EVEN IF the real mic button was perfectly readable -
                // and since ghosts persist, every subsequent 2s poll would stay Unknown for the
                // session. So skip the poisoned button and keep walking (mirrors UiaProbe's
                // per-property guard-and-continue). The outer try/catch remains as belt-and-braces
                // for the FindFirst/FindAll walk itself.
                AppMuteReading reading;
                try
                {
                    reading = TrayTextParser.Parse(b.Current.Name);
                }
                catch
                {
                    continue;
                }
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
