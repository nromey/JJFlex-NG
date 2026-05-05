# SmartSDR vendor-binary decompile analysis — round 5 setup

**Date:** 2026-05-04
**Investigator:** Claude (Opus 4.7) running as background research agent
**Bug context:** FlexLib 4.2.18 silent-discovery on Don's 6300 (R1→R4 instrumentation chain)
**Authorization:** Noel approved ILSpy/dnSpy on Flex vendor binaries 2026-05-03 for method comparison only.

## Summary

The intended SmartSDR 4.2.18 decompile could not be performed in this session — the harness denied shell execution (Bash and PowerShell both blocked; ilspycmd cannot be invoked). However, file-system access through Read/Glob/Grep was sufficient to (a) cross-check the three suspect Vita/ files against the existing SmartSDR 4.1.x decompile and (b) compare JJFlexRadio's startup code to SmartSDR 4.1.x's startup code. The investigation **narrows the suspect set significantly**:

- **`VitaPacketBase.cs` is ruled out** — pure DTO/parser base class, no statics, no thread/IO, no module init. Inheritance hierarchy is byte-shuffle only.
- **`HighPriorityTaskScheduler.cs` is unlikely** — only referenced from `Vita/VitaSocket.cs` line 80 (the post-discovery command/data socket), not Discovery itself. Discovery uses raw `UdpClient`. Even if HPTS's `.Instance` field is touched, instance construction does no work; threads are spawned per `QueueTask()`.
- **`MmcssPipelineScheduler.cs` is the strongest remaining suspect** — its private constructor eagerly spawns 4 MMCSS "Pro Audio" persistent threads at first `.Instance` reference. Referenced from `Radio.cs` lines 2072 and 2882 (post-discovery, but reachable via JIT-time type resolution). This is also the *only* code path in FlexLib 4.2.18 that calls `avrt.dll` — the only new native dependency between 4.0.1 and 4.2.18.
- **The biggest single SmartSDR-vs-JJFlexRadio framework delta** is now clear: SmartSDR 4.1.x targets **.NET Framework 4.6.2**, JJFlexRadio is on **.NET 10**. SmartSDR 4.2.18 likely also moved to .NET 8/10 (single-file bundle, can't confirm without decompile), but its wrapper code likely stayed almost as minimal as 4.1.x's — which was confirmed *almost-empty* (no thread priority, no MMCSS, no socket pre-init, no firewall, no AppDomain hooks).

The decompile blocker means we cannot *directly* see the SmartSDR 4.2.18 wrapper. The recommended R5 build is therefore designed to test the **MMCSS suspect** specifically rather than continue to trial-and-error against an unknown wrapper.

## Decompile artifacts

**Status: BLOCKED.** ilspycmd is installed at `C:\Users\nrome\.dotnet\tools\ilspycmd.exe` but the harness denies Bash/PowerShell execution. Cannot run:

```
ilspycmd "C:\dev\smartsdr-v4.2.18-extracted\PFiles64\SmartSDR v4.2.18\SmartSDR.exe" -o C:\dev\smartsdr-v4.2.18-decompiled\ -p
```

**Workaround for next session:** ask Noel to run that command in a regular shell, or invoke this analysis via a Claude Code session with shell exec permitted. The SmartSDR.exe is a **single-file .NET bundle** — runtime + libs are embedded; no sibling DLLs visible. Decompile output will need to be the bundle-extracted form (ilspycmd handles this).

Files investigated this session (existing artifacts):
- `C:\dev\smartsdr-decompiled-4.1.x\SmartSDR.decompiled.cs` — older but useful baseline
- `C:\dev\smartsdr-decompiled-4.1.x\FlexLib\FlexLib.decompiled.cs` — older FlexLib (pre-MMCSS)
- `C:\dev\smartsdr-decompiled-4.1.x\Util\Util.decompiled.cs` — older Util
- `C:\dev\smartsdr-decompiled-4.1.x\Netify\Netify.decompiled.cs` — Network List Manager wrapper

## SmartSDR 4.1.x startup sequence (from existing decompile)

This is what SmartSDR 4.1.x does between process start and `Discovery.Start()`. It's a useful baseline because its startup is almost certainly very similar to 4.2.18's startup (UI plumbing rarely gets rewritten).

1. **`Main()`** (`SmartSDR.decompiled.cs:79776`) — `[STAThread]`, instantiates `App`, calls `InitializeComponent()`, calls `Run()`.
2. **`App()` constructor** (line 79620):
   - Calls `Init()` to parse command-line args (Smartlink host/port, software-rendering flag)
   - Wires `AppDomain.CurrentDomain.UnhandledException`
   - Wires `DispatcherUnhandledException`
   - Wires `TaskScheduler.UnobservedTaskException`
   - Sets `Timeline.DesiredFrameRateProperty` default to 20 fps
3. **`Init()`** (line 79670) — *only* command-line parsing. No socket setup, no thread priority, no MMCSS registration, no firewall.
4. **MainWindow loads → `RadioListViewModel` constructor** (line 37637) — this is the actual gate to discovery:
   - Sets `API.ProgramName = "SmartSDR-Win"` (or "CAT" / "DAX" / "SmartSDR-Maestro" / "SmartSDR-M" / "MaestroStartup" depending on which executable)
   - Sets `API.IsGUI = true`
   - Calls `API.Init()` (which internally calls `Discovery.Start()`)

**No firewall API. No `AvSetMmThreadCharacteristics`. No `Process.PriorityClass`. No `ProcessorAffinity`. No `RegisterApplicationRestart`. No app.manifest difference (SmartSDR uses `asInvoker`, same as JJFlexRadio).** SmartSDR's wrapper is almost trivially thin.

Confirmation grep on the entire 4.1.x decompile:

- `AvSetMmThreadCharacteristics` / `avrt` / `Pro Audio` / `MmcssPipeline` / `HighPriorityTask` — **0 matches**
- `EnableBroadcast` / `MulticastInterface` / `JoinMulticastGroup` — **0 matches**
- `INetFwPolicy` / `HNetCfg` / `firewall` / `Firewall` — **0 matches**
- `requestedExecutionLevel` (manifest) — same `asInvoker` as JJFlexRadio

The SmartSDR.exe target framework attribute at line 110:
```
[assembly: TargetFramework(".NETFramework,Version=v4.6.2", FrameworkDisplayName = ".NET Framework 4.6.2")]
```

This is the **biggest framework delta** between SmartSDR 4.1.x and JJFlexRadio: .NET Framework 4.6.2 vs .NET 10. UDP `Receive`/`ReceiveAsync` go through entirely different stacks at the BCL level. (Whether SmartSDR 4.2.18 also moved to .NET 8/10 is unknown until decompile happens.)

## JJFlexRadio startup sequence (current main, comparable point)

For comparison, JJFlexRadio's path to `Discovery.Start()`:

1. **`MyApplication_Startup`** (`ApplicationEvents.vb:26`) — wires:
   - `NativeLoader.Initialize()` (architecture-aware DLL resolver)
   - `ServicePointManager.SecurityProtocol = Tls12 | Tls13`
   - `Application.ThreadException`, `AppDomain.CurrentDomain.UnhandledException` → CrashReporter
   - `Radios.ScreenReaderOutput.Initialize()` (Tolk/CrossSpeak)
   - `EarconPlayer.Initialize()` (NAudio)
   - `HelpLauncher.Initialize()`
   - `ConnectionProfiler.PurgeOldProfiles()`
   - Creates `ShellForm` + `WpfMainWindow` (WinForms+WPF host)
   - Wires WPF Dispatcher exception handler
   - Many callback delegates (Command Finder, AutoConnect, FreqOutHandlers, etc.)
   - Calls `InitializeApplication()` → `GetConfigInfo()` → eventually `openTheRadio(initialCall:=True)`
2. **`openTheRadio`** (`globals.vb:2018`) → `wpfSelectorProc` → `RigControl.LocalRadios()` (`FlexBase.cs:141`)
3. **`LocalRadios()`** → `apiInit(force:=true)` (`FlexBase.cs:100`):
   - Sets `API.ProgramName = p.ProgramName` (in constructor at line 7390 — happens once when the FlexBase is constructed in step 1; check this is still set)
   - Sets `API.IsGUI = true`
   - Calls `API.Init()` → `Discovery.Start()`

**JJFlexRadio's wrapper does substantially MORE** than SmartSDR's between process start and Discovery.Start():
- Tolk/screen reader load (multiple native DLL loads)
- NAudio engine init (creates audio threads)
- WinForms + WPF dual-shell construction
- Crash reporter wiring
- ConnectionProfiler init
- Personal data / config file load
- Migration path

None of these touch UDP, broadcast, multicast, or the Windows network stack directly. But they DO load lots of assemblies and create WinForms/WPF/dispatcher infrastructure that doesn't exist in SmartSDR 4.1.x's path. **If a regression in .NET 10's UDP-on-receive interacts with WPF dispatcher load order, JJFlexRadio's heavier early-stage construction may amplify it more than SmartSDR's.**

## Per-suspect-file analysis

### `Vita/VitaPacketBase.cs` — RULE OUT

**File:** `C:\dev\jjflex-flexlib-42\FlexLib_API\Vita\VitaPacketBase.cs`

**What it does:** Pure DTO base class containing `Header`, `StreamId`, `ClassId`, `TimestampInt`, `TimestampFrac`, `Trailer` fields and four protected helpers: `ParsePreamble`, `ParseTrailer`, `WriteHeaderBytes`, `WriteTrailerBytes`. All operate on `byte[]` via `BinaryPrimitives.ReadUInt*BigEndian`.

**Static state:** None.
**Module initializer:** None (`ModuleInitializer` grep on entire `FlexLib_API/` returns 0).
**Native imports:** None.
**Thread spawn:** None.
**IO:** None.

**Who calls it in JJFlex:** Subclasses (e.g. `VitaDiscoveryPacket`) inherit and call its protected helpers during packet parsing. Strictly post-receive — only runs after a packet exists.

**Does JJFlexRadio use it?** Indirectly, via subclassed packet types. Yes — but it is on the receive PATH, not the receive STARTUP. It cannot suppress UDP delivery.

**Verdict:** Ruled out. Not the bug.

### `Vita/HighPriorityTaskScheduler.cs` — UNLIKELY (but not ruled out)

**File:** `C:\dev\jjflex-flexlib-42\FlexLib_API\Vita\HighPriorityTaskScheduler.cs`

**What it does:** A `TaskScheduler` subclass that spawns ONE new thread per `QueueTask()`, registers that thread with MMCSS as "Pro Audio" via `AvSetMmThreadCharacteristicsW`, runs the task, then reverts. Each task gets its own short-lived MMCSS-elevated thread.

**Static state:** `public static readonly HighPriorityTaskScheduler Instance = new();` — eager singleton. The instance constructor is the implicit parameterless ctor (does nothing). Construction triggers no work; only `QueueTask()` spawns threads.

**Who calls it in JJFlex (`HighPriorityTaskScheduler.Instance` references):**
- `FlexLib_API/Vita/VitaSocket.cs:80` — `Task.Factory.StartNew(ReceiveLoop, ..., HighPriorityTaskScheduler.Instance)`. This is the post-discovery command/data UDP socket, NOT discovery's socket.

**Critical:** Discovery.cs uses raw `UdpClient` directly. **Discovery does not reference HighPriorityTaskScheduler.** So even if HPTS misbehaves, it doesn't directly disrupt discovery's bound socket.

However, the .Instance field is `public static readonly` — JIT may resolve the type at any point a method that references it is JITted. If anything in the type-loader chain that fires during early FlexLib startup touches VitaSocket's type metadata, HPTS gets type-loaded (but not instantiated-with-side-effects, since the ctor is empty).

**Does SmartSDR 4.1.x use anything like it?** No — zero matches in the 4.1.x decompile for `AvSet*` or `Pro Audio`. This is genuinely new in 4.2.18.

**Verdict:** Unlikely to be the direct cause but cannot be 100% ruled out without a JIT-trace. Lower priority than MmcssPipelineScheduler.

### `Vita/MmcssPipelineScheduler.cs` — STRONGEST REMAINING SUSPECT

**File:** `C:\dev\jjflex-flexlib-42\FlexLib_API\Vita\MmcssPipelineScheduler.cs`

**What it does:** A `TaskScheduler` backed by **a fixed pool of 4 PERSISTENT threads** that are created and MMCSS-registered **inside the private instance constructor**. Each pool thread blocks on `BlockingCollection.GetConsumingEnumerable()` and processes tasks. Also calls `AvSetMmThreadCharacteristicsW("Pro Audio", ...)` per thread.

**Static state:** `public static readonly MmcssPipelineScheduler Instance = new();` — **eager singleton, instance constructor SPAWNS 4 MMCSS threads**.

**Who calls it in JJFlex (`MmcssPipelineScheduler.Instance` references):**
- `FlexLib_API/FlexLib/Radio.cs:2072` — TPL DataFlow `ActionBlock` for command/data UDP packet processing
- `FlexLib_API/FlexLib/Radio.cs:2882` — TPL DataFlow `ActionBlock` for per-stream IF data dispatch (the `DispatchToStreamWorker` path)

Both references are in `Radio` instance methods that *should* only execute post-discovery (when a Radio object has been constructed and a connection is starting). **However**, the JIT can and does resolve type tokens eagerly when a containing method is first JITted, which can happen as soon as a Radio constructor or method is JIT-compiled — even before discovery binding. **First time the JIT resolves the type, the type's static field initializer runs, which constructs `MmcssPipelineScheduler.Instance`, which spawns 4 MMCSS Pro Audio threads.**

**Why this is interesting for the discovery bug:**
1. The 4 persistent MMCSS threads have real-time-priority class. They sit blocked on `BlockingCollection<Task>` until work arrives — which during discovery startup is *never*. So they're idle.
2. But idle MMCSS threads still hold MMCSS reservations against the OS scheduler.
3. .NET 10's UDP receive in some scenarios uses the I/O completion port (IOCP) thread pool. If MMCSS reservations interact with the IOCP scheduler in a way that starves a UDP-receive callback under .NET 10 specifically, packets can be queued by the kernel into the socket's buffer but the .NET runtime's user-mode receive callback never fires.
4. This is not a *normal* failure mode for MMCSS, but the combination of (.NET 10 + idle Pro Audio threads + UdpClient.ReceiveAsync) is genuinely novel territory and matches Suspect #2 + Suspect #4 from the original suspect ranking.

**Why SmartSDR 4.1.x doesn't have this issue:** SmartSDR 4.1.x decompile shows zero MMCSS calls — it never registers any threads with avrt.dll. The 4.1.x FlexLib also has zero `MmcssPipeline` references. So whatever interaction between MMCSS and .NET-10-UDP is happening, it's a 4.2.18-only thing.

**What we need to know about SmartSDR 4.2.18 (decompile-blocked):**
- Does SmartSDR 4.2.18 also reference `MmcssPipelineScheduler.Instance` early in its startup, before Discovery? If yes — and SmartSDR 4.2.18 still discovers Don's radio — then the suspect drops a tier. If no, suspect stays at the top.
- Does SmartSDR 4.2.18 set any process-level priority or affinity that JJFlexRadio doesn't? E.g. `Process.GetCurrentProcess().PriorityClass = ...` or `SetThreadAffinityMask`.
- Does SmartSDR 4.2.18 do any explicit `ThreadPool.SetMinThreads` or IOCP-related warming?

**Verdict:** Top suspect. The R5 diagnostic build should test this directly.

## Identified gaps (SmartSDR 4.1.x → JJFlexRadio)

Ranked by plausibility of explaining a UDP-broadcast-delivery regression on .NET 10:

1. **MMCSS Pro Audio thread pool (FlexLib 4.2.18-new):** SmartSDR doesn't have it because its FlexLib reference predates the MMCSS rewrite. JJFlexRadio inherits it via FlexLib 4.2.18. New native dependency on `avrt.dll`. This is genuinely the biggest delta and aligns with Suspect #1 + #2.
2. **WinForms + WPF dual-shell + dispatcher infrastructure:** SmartSDR is pure WPF. JJFlexRadio creates a WinForms ShellForm hosting WPF via ElementHost, plus a Dispatcher exception handler, plus screen-reader/audio engine/help-launcher init — all before discovery. None of these directly touch UDP, but they create assembly-load and dispatcher state that SmartSDR doesn't.
3. **TLS floor (`ServicePointManager.SecurityProtocol = Tls12 | Tls13`):** JJFlexRadio sets this in `MyApplication_Startup`. SmartSDR doesn't (it doesn't need to since FlexRadio's stack already negotiates TLS 1.2). Highly unlikely to affect UDP — `ServicePointManager` is HTTP/TLS-specific. Probably irrelevant.
4. **NAudio EarconPlayer + Tolk screen reader init:** Both load native DLLs that bind audio devices. NAudio especially can trigger MMCSS-related Windows audio engine state. Worth a quick try-disabling test. SmartSDR has nothing equivalent.
5. **CrashReporter handler chain:** JJFlexRadio wires `Application.ThreadException` and `AppDomain.UnhandledException` to a single static handler that does I/O on dispatch. SmartSDR has the same handlers but with simpler bodies. Unlikely to be relevant.

## Recommended next diagnostic — R5 build shape

**Goal:** Test whether `MmcssPipelineScheduler.Instance` is implicated in the silent-discovery failure.

**The cleanest single experiment:** in the FlexLib 4.2.18 source on `track/flexlib-42`, comment out the eager singleton initialization in `MmcssPipelineScheduler.cs` and replace the `TaskScheduler = MmcssPipelineScheduler.Instance` references in `Radio.cs:2072` and `Radio.cs:2882` with `TaskScheduler = TaskScheduler.Default`. This regresses the post-discovery audio path to the regular ThreadPool (no MMCSS), but leaves Discovery.cs and everything else identical. Build a clean R5 debug, ship to Don, capture trace.

**Outcomes:**
- **If R5 discovers the radio:** MMCSS is implicated. Investigation pivots to either (a) rolling back FlexLib's MMCSS changes for JJFlexRadio (reverting to ThreadPool scheduling for command/data sockets), or (b) reaching out to Flex with a reproduction. Consider whether MMCSS is necessary on PCs without dedicated audio hardware (probably not — JJFlexRadio's audio is RX-only, post-Opus-decode, so realtime priority is overkill).
- **If R5 still fails to discover:** MMCSS is ruled out. Investigation pivots to (a) Suspect #4 (.NET 10 UdpClient broadcast regression — would need a pure-.NET-10 minimal repro), (b) WinForms+WPF dual-shell load order, (c) actually obtaining the SmartSDR 4.2.18 decompile in a session with shell exec.

**Secondary diagnostic worth running in parallel** — a tiny standalone .NET 10 console app that does ONLY:
```
var udp = new UdpClient();
udp.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
udp.Client.Bind(new IPEndPoint(IPAddress.Any, 4992));
while (true) {
    var p = await udp.ReceiveAsync();
    Console.WriteLine($"rx {p.Buffer.Length} from {p.RemoteEndPoint}");
}
```

Run on Don's machine with no FlexLib, no JJFlexRadio, no MMCSS. If THIS sees Don's 6300 broadcasts, the bug is in JJFlexRadio's process state. If it doesn't, the bug is .NET 10 itself or Don's machine network stack and we need a different investigation entirely.

**Decompile blocker resolution:** When a session with shell exec is available, run:

```
ilspycmd "C:\dev\smartsdr-v4.2.18-extracted\PFiles64\SmartSDR v4.2.18\SmartSDR.exe" -o C:\dev\smartsdr-v4.2.18-decompiled\ -p
```

Then grep the result for `MmcssPipelineScheduler|HighPriorityTaskScheduler|AvSetMmThreadCharacteristics`. Whatever SmartSDR 4.2.18 does (or doesn't do) with these classes is the smoking gun if R5 ends up not being decisive.

## Files referenced (absolute paths)

JJFlexRadio main repo (working baseline):
- `C:\dev\jjflex-ng\ApplicationEvents.vb` (startup wiring)
- `C:\dev\jjflex-ng\globals.vb` (openTheRadio, wpfSelectorProc)
- `C:\dev\jjflex-ng\Radios\FlexBase.cs` (apiInit, LocalRadios, FlexBase ctor that sets API.ProgramName)
- `C:\dev\jjflex-ng\Radios\AllRadios.cs` (alternate apiInit path with hardcoded "JJRadio" ProgramName)

JJFlexRadio FlexLib 4.0.1 baseline:
- `C:\dev\jjflex-ng\FlexLib_API\FlexLib\Discovery.cs`
- `C:\dev\jjflex-ng\FlexLib_API\Vita\VitaSocket.cs` (Socket-based, ExclusiveAddressUse=true)

JJFlexRadio FlexLib 4.2.18 candidate:
- `C:\dev\jjflex-flexlib-42\FlexLib_API\FlexLib\Discovery.cs` (with R1-R4 instrumentation in place)
- `C:\dev\jjflex-flexlib-42\FlexLib_API\FlexLib\API.cs`
- `C:\dev\jjflex-flexlib-42\FlexLib_API\FlexLib\Radio.cs` (lines 2072, 2882 — MmcssPipelineScheduler refs)
- `C:\dev\jjflex-flexlib-42\FlexLib_API\Vita\VitaSocket.cs` (UdpClient-based, ExclusiveAddressUse=false, references HighPriorityTaskScheduler at line 80)
- `C:\dev\jjflex-flexlib-42\FlexLib_API\Vita\HighPriorityTaskScheduler.cs`
- `C:\dev\jjflex-flexlib-42\FlexLib_API\Vita\MmcssPipelineScheduler.cs`
- `C:\dev\jjflex-flexlib-42\FlexLib_API\Vita\VitaPacketBase.cs`

SmartSDR 4.1.x baseline decompile (already on disk):
- `C:\dev\smartsdr-decompiled-4.1.x\SmartSDR.decompiled.cs` (App ctor at line 79620, Main at line 79776, RadioListViewModel ctor at line 37637)
- `C:\dev\smartsdr-decompiled-4.1.x\FlexLib\FlexLib.decompiled.cs` (older Discovery at line 5915-5937, no MMCSS references anywhere)
- `C:\dev\smartsdr-decompiled-4.1.x\Util\Util.decompiled.cs` (older SmartSDRNetwork class)
- `C:\dev\smartsdr-decompiled-4.1.x\Netify\Netify.decompiled.cs` (Network List Manager wrapper, used only by Auth0 login UX)

SmartSDR 4.2.18 binaries (NOT YET DECOMPILED — blocked):
- `C:\dev\smartsdr-v4.2.18-extracted\PFiles64\SmartSDR v4.2.18\SmartSDR.exe` (single-file .NET bundle; ~size unknown)
- `C:\dev\smartsdr-v4.2.18-extracted\PFiles64\SmartSDR v4.2.18\CAT.exe`
- `C:\dev\smartsdr-v4.2.18-extracted\PFiles64\SmartSDR v4.2.18\DAX.exe`
