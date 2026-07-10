// tools/UiaProbe/Program.cs
// Dumps the UIA tree (ControlType, AutomationId, Name, ClassName, TogglePattern state) of every
// top-level window belonging to the given process names, to a timestamped file. Read-only: never
// invokes patterns, never focuses windows. Usage: UiaProbe [processName ...]
// (defaults: CiscoCollabHost ms-teams Teams)
using System.Text;
using FlaUI.Core.AutomationElements;
using FlaUI.UIA3;

string[] targets = args.Length > 0 ? args : new[] { "CiscoCollabHost", "ms-teams", "Teams" };
var sb = new StringBuilder();
using var automation = new UIA3Automation();
var desktop = automation.GetDesktop();
foreach (var w in desktop.FindAllChildren())
{
    string pname;
    try { pname = System.Diagnostics.Process.GetProcessById(w.Properties.ProcessId.Value).ProcessName; }
    catch { continue; }
    if (!targets.Any(t => pname.Contains(t, StringComparison.OrdinalIgnoreCase))) continue;
    sb.AppendLine($"===== window: '{w.Name}' process={pname} class={w.ClassName} =====");
    Dump(w, 0);
}
string outPath = Path.Combine(AppContext.BaseDirectory,
    $"uia-dump-{DateTime.Now:yyyyMMdd-HHmmss}.txt");
File.WriteAllText(outPath, sb.ToString());
Console.WriteLine($"wrote {outPath} ({sb.Length} chars)");

void Dump(AutomationElement e, int depth)
{
    if (depth > 25) return;
    string toggle = "";
    try
    {
        if (e.Patterns.Toggle.IsSupported)
            toggle = $" TOGGLE={e.Patterns.Toggle.Pattern.ToggleState.Value}";
    }
    catch { }
    string id = "", name = "", cls = "";
    try { id = e.Properties.AutomationId.ValueOrDefault ?? ""; } catch { }
    try { name = e.Properties.Name.ValueOrDefault ?? ""; } catch { }
    try { cls = e.ClassName ?? ""; } catch { }
    sb.AppendLine($"{new string(' ', depth * 2)}[{e.Properties.ControlType.ValueOrDefault}] id='{id}' name='{name}' class='{cls}'{toggle}");
    foreach (var c in e.FindAllChildren()) Dump(c, depth + 1);
}
