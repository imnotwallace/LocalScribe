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
    string wName = "?", wClass = "?";
    try { wName = w.Name; } catch { }
    try { wClass = w.ClassName; } catch { }
    sb.AppendLine($"===== window: '{wName}' process={pname} class={wClass} =====");
    Console.WriteLine($"walking '{wName}' ({pname}, {wClass})... this can take a while for live meeting windows");
    var swWin = System.Diagnostics.Stopwatch.StartNew();
    try { Dump(w, 0); } catch { sb.AppendLine("  (window walk aborted: poisoned element)"); }
    Console.WriteLine($"  done in {swWin.Elapsed.TotalSeconds:0.0}s ({sb.Length} chars so far)");
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
    string id = "", name = "", cls = "", ct = "?";
    try { id = e.Properties.AutomationId.ValueOrDefault ?? ""; } catch { }
    try { name = e.Properties.Name.ValueOrDefault ?? ""; } catch { }
    try { cls = e.ClassName ?? ""; } catch { }
    // Every read guarded: stale tray icons of dead processes throw COMException 0x80040201
    // ("event was unable to invoke any of the subscribers") on ANY property read - one poisoned
    // element must degrade to a '?' line, never kill the walk (observed live 2026-07-11).
    try { ct = e.Properties.ControlType.ValueOrDefault.ToString(); } catch { }
    sb.AppendLine($"{new string(' ', depth * 2)}[{ct}] id='{id}' name='{name}' class='{cls}'{toggle}");
    AutomationElement[] children;
    try { children = e.FindAllChildren(); } catch { return; }
    foreach (var c in children)
    {
        try { Dump(c, depth + 1); } catch { /* poisoned subtree: skip, keep walking siblings */ }
    }
}
