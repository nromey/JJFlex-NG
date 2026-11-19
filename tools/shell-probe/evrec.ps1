# WinEvent recorder for the shell probe (#550, Step 0). PASSIVE: sends nothing, presses nothing.
#
# Records, for every process whose name matches -ProcessName:
#   EVENT_SYSTEM_FOREGROUND, MENUSTART/END, MENUPOPUPSTART/END, DIALOGSTART/END,
#   EVENT_OBJECT_SHOW (windows only), EVENT_OBJECT_FOCUS and EVENT_OBJECT_NAMECHANGE (windows only),
# each with the window handle, its class, its title, and the MSAA name and role of the event's
# object (AccessibleObjectFromEvent, the call a screen reader makes). Plus UIA focus-changed.
# Every line carries the wall clock and Environment.TickCount, the clock JAWS's capture and the
# probe's own trace also use.
#
# Round 2 adds UIA MenuOpened/MenuClosed (and MenuModeStart/End where the managed client knows them),
# and each UIA focus event's window handle and runtime id.
#
# Built from the 2026-11-10 bench's evlog.ps1, adding the MSAA name and role and NAMECHANGE that
# the #550 cause report asked for. Its positive control is the --hybrid probe: on a menu Escape
# it must show OBJ_FOCUS on the ElementHost's window with MSAA name "JJ Shell Probe host".
param([string]$ProcessName = 'jjshellprobe', [int]$Seconds = 600, [Parameter(Mandatory)][string]$Out)
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName UIAutomationClient, UIAutomationTypes
if (-not ('EvRec' -as [type])) {
Add-Type -ReferencedAssemblies UIAutomationClient,UIAutomationTypes,WindowsBase,System.Collections.Concurrent,System.Threading.Thread,System.Threading,System.Runtime,System.Runtime.InteropServices,System.Collections,System.Diagnostics.Process,System.ComponentModel.Primitives,System.Private.CoreLib -TypeDefinition @"
using System; using System.Text; using System.Threading; using System.Runtime.InteropServices; using System.Collections.Concurrent;
using System.Collections.Generic; using System.Reflection; using System.Windows.Automation;
public static class EvRec {
  delegate void WinEventProc(IntPtr h, uint ev, IntPtr hwnd, int idObj, int idChild, uint thr, uint time);
  [DllImport("user32.dll")] static extern IntPtr SetWinEventHook(uint min, uint max, IntPtr mod, WinEventProc p, uint pid, uint tid, uint flags);
  [DllImport("user32.dll")] static extern bool UnhookWinEvent(IntPtr h);
  [DllImport("user32.dll")] static extern int GetMessage(out MSG m, IntPtr h, uint a, uint b);
  [DllImport("user32.dll")] static extern bool PostThreadMessage(uint tid, uint msg, IntPtr w, IntPtr l);
  [DllImport("kernel32.dll")] static extern uint GetCurrentThreadId();
  [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(IntPtr h, out uint p);
  [DllImport("user32.dll", CharSet=CharSet.Unicode)] static extern int GetWindowText(IntPtr h, StringBuilder s, int n);
  [DllImport("user32.dll", CharSet=CharSet.Unicode)] static extern int GetClassName(IntPtr h, StringBuilder s, int n);
  [DllImport("oleacc.dll")] static extern int AccessibleObjectFromEvent(IntPtr hwnd, int idObj, int idChild,
      [MarshalAs(UnmanagedType.IDispatch)] out object acc, [MarshalAs(UnmanagedType.Struct)] out object child);
  [StructLayout(LayoutKind.Sequential)] struct MSG { public IntPtr h; public uint m; public IntPtr w; public IntPtr l; public uint t; public int x; public int y; }
  public static ConcurrentQueue<string> Q = new ConcurrentQueue<string>();
  static string _name; static uint _tid; static WinEventProc _proc; static Thread _t;
  static Dictionary<uint,bool> _ours = new Dictionary<uint,bool>();
  static string Now(){ return DateTime.Now.ToString("HH:mm:ss.fff") + " tick=" + Environment.TickCount; }
  static bool Ours(uint pid){ bool b; if (_ours.TryGetValue(pid, out b)) return b;
    try { b = string.Equals(System.Diagnostics.Process.GetProcessById((int)pid).ProcessName, _name, StringComparison.OrdinalIgnoreCase); } catch { b = false; }
    _ours[pid] = b; return b; }
  static string Win(IntPtr h){ var s=new StringBuilder(256); GetWindowText(h,s,256); var c=new StringBuilder(128); GetClassName(h,c,128); return "hwnd=0x"+h.ToString("X")+" class="+c+" title=\""+s+"\""; }
  static string Msaa(IntPtr hwnd, int idObj, int idChild){
    try { object acc, child; int hr = AccessibleObjectFromEvent(hwnd, idObj, idChild, out acc, out child);
      if (hr != 0 || acc == null) return " msaa=(hr 0x"+hr.ToString("X")+")";
      object[] a = new object[]{ child };
      string name = null; object role = null;
      try { name = acc.GetType().InvokeMember("accName", BindingFlags.GetProperty, null, acc, a) as string; } catch (Exception x) { name = "(" + x.GetType().Name + ")"; }
      try { role = acc.GetType().InvokeMember("accRole", BindingFlags.GetProperty, null, acc, a); } catch (Exception x) { role = "(" + x.GetType().Name + ")"; }
      return " msaaName=\""+name+"\" msaaRole="+role;
    } catch (Exception x) { return " msaa=(" + x.GetType().Name + ")"; } }
  static string EvName(uint ev){ switch(ev){
    case 0x3: return "FOREGROUND"; case 0x4: return "MENUSTART"; case 0x5: return "MENUEND";
    case 0x6: return "MENUPOPUPSTART"; case 0x7: return "MENUPOPUPEND"; case 0x10: return "DIALOGSTART"; case 0x11: return "DIALOGEND";
    case 0x8002: return "OBJ_SHOW"; case 0x8005: return "OBJ_FOCUS"; case 0x800C: return "OBJ_NAMECHANGE"; default: return "ev"+ev.ToString("X"); } }
  static void Cb(IntPtr hk, uint ev, IntPtr hwnd, int idObj, int idChild, uint thr, uint time){
    try {
      uint p; GetWindowThreadProcessId(hwnd, out p);
      if (!Ours(p)) { if (ev==3) Q.Enqueue(Now()+" WIN FOREGROUND (other pid "+p+") "+Win(hwnd)); return; }
      if ((ev==0x8002 || ev==0x800C) && idObj!=0) return; // windows only for show and name change
      string extra = (ev==0x8005 || ev==3 || ev==0x800C || ev==4 || ev==6) ? Msaa(hwnd, idObj, idChild) : "";
      Q.Enqueue(Now()+" WIN "+EvName(ev)+" pid="+p+" idObj="+idObj+" idChild="+idChild+" "+Win(hwnd)+extra);
    } catch (Exception x) { Q.Enqueue(Now()+" WIN callback error "+x.GetType().Name); } }
  static string Rid(AutomationElement el){ try { var r = el.GetRuntimeId(); return r == null ? "(none)" : string.Join(".", Array.ConvertAll(r, i => i.ToString("X"))); } catch { return "(err)"; } }
  public static void Start(string name){
    _name=name; _proc=Cb;
    _t=new Thread(()=>{ _tid=GetCurrentThreadId();
      var hs = new List<IntPtr>();
      hs.Add(SetWinEventHook(0x0003,0x0007,IntPtr.Zero,_proc,0,0,0));
      hs.Add(SetWinEventHook(0x0010,0x0011,IntPtr.Zero,_proc,0,0,0));
      hs.Add(SetWinEventHook(0x8002,0x8002,IntPtr.Zero,_proc,0,0,0));
      hs.Add(SetWinEventHook(0x8005,0x8005,IntPtr.Zero,_proc,0,0,0));
      hs.Add(SetWinEventHook(0x800C,0x800C,IntPtr.Zero,_proc,0,0,0));
      MSG m; while(GetMessage(out m,IntPtr.Zero,0,0)>0){}
      foreach (var h in hs) UnhookWinEvent(h); });
    _t.SetApartmentState(ApartmentState.STA); _t.IsBackground=true; _t.Start();
    Automation.AddAutomationFocusChangedEventHandler((s,e)=>{ try{ var el=(AutomationElement)s; if(!Ours((uint)el.Current.ProcessId)) return;
      Q.Enqueue(Now()+" UIA FOCUS name=\""+el.Current.Name+"\" type="+el.Current.ControlType.ProgrammaticName+" class="+el.Current.ClassName+" id="+el.Current.AutomationId+" fw="+el.Current.FrameworkId+" hwnd=0x"+el.Current.NativeWindowHandle.ToString("X")+" rid="+Rid(el)); }catch(Exception x){ Q.Enqueue(Now()+" UIA FOCUS (gone) "+x.GetType().Name); } });
    // Round 2: UIA's menu events (MenuOpened 20003, MenuClosed 20007, MenuModeStart 20018, MenuModeEnd 20019).
    var known = new AutomationEvent[]{ AutomationElement.MenuOpenedEvent, AutomationElement.MenuClosedEvent };
    foreach (int id in new int[]{20003,20007,20018,20019}) {
      AutomationEvent ev = AutomationEvent.LookupById(id);
      if (ev == null) { Q.Enqueue(Now()+" UIA event "+id+" not available to the managed UIA client; not recorded"); continue; }
      int cid = id;
      Automation.AddAutomationEventHandler(ev, AutomationElement.RootElement, TreeScope.Subtree, (s,e)=>{ try{ var el=(AutomationElement)s; if(!Ours((uint)el.Current.ProcessId)) return;
        Q.Enqueue(Now()+" UIA EVENT "+cid+" name=\""+el.Current.Name+"\" type="+el.Current.ControlType.ProgrammaticName+" fw="+el.Current.FrameworkId); }catch(Exception x){ Q.Enqueue(Now()+" UIA EVENT "+cid+" (gone) "+x.GetType().Name); } });
      Q.Enqueue(Now()+" UIA event "+id+" ("+ev.ProgrammaticName+") recorded");
    }
    Q.Enqueue(Now()+" recorder started for process name "+name);
  }
  public static void Stop(){ PostThreadMessage(_tid,0x0012,IntPtr.Zero,IntPtr.Zero); Automation.RemoveAllEventHandlers(); Q.Enqueue(Now()+" recorder stopped"); }
}
"@
}
[EvRec]::Start($ProcessName)
$end = (Get-Date).AddSeconds($Seconds)
$stop = "$Out.stop"
while ((Get-Date) -lt $end -and -not (Test-Path $stop)) {
  Start-Sleep -Milliseconds 200
  $s = $null; while ([EvRec]::Q.TryDequeue([ref]$s)) { Add-Content -Path $Out -Value $s -Encoding utf8 }
}
[EvRec]::Stop(); Start-Sleep -Milliseconds 300
$s = $null; while ([EvRec]::Q.TryDequeue([ref]$s)) { Add-Content -Path $Out -Value $s -Encoding utf8 }
if (Test-Path $stop) { Remove-Item $stop }
