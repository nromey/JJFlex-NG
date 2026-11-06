# deskrun: run window-building tests on a hidden desktop

`deskrun` starts one test project's `dotnet test` on a newly created desktop
that nobody can see, relays its output, returns its exit code, and tears the
desktop and every process it started down afterwards. Task #906.

## Why it exists

`JJFlexWpf.Tests` builds real application windows, and its guard
(`JJFlexWpf.Tests/Infrastructure/DeskGuard.cs`) refuses to do that unless the
windows would be invisible, the run silent, and the settings throwaway.

The test process used to try to make itself invisible by moving its UI thread
onto a private desktop with `SetThreadDesktop`. Under `dotnet test` that call
always fails with ERROR_BUSY (170): the runtime's OLE initialisation gives the
thread a hidden window before any test code runs, and a thread that owns a
window cannot change desktops. So every window-building class refused, and none
of them could run without a person declaring the desk free.

A desktop can be chosen for a whole process when it starts, through
`STARTUPINFO.lpDesktop`, and the processes it starts inherit it. That is all
this tool does. The guard in the test process then reads from Windows that its
thread is already on a desktop that is not shared and is not the one receiving
input, and accepts that as isolation. Sound and settings are still checked
exactly as before.

## Using it

Build the test project first; `deskrun` always adds `--no-build`.

```
dotnet build JJFlexWpf.Tests/JJFlexWpf.Tests.csproj -c Debug -p:Platform=x64
tools/deskrun/bin/x64/Debug/net10.0-windows/deskrun.exe JJFlexWpf.Tests/JJFlexWpf.Tests.csproj -c Debug -p:Platform=x64 --filter "FullyQualifiedName~HarnessTests"
```

- `--timeout SECONDS` before the project sets how long the run may take
  (default 600). After that the whole process tree is killed.
- Everything after the project goes to `dotnet test` unchanged.
- Add `--logger "console;verbosity=detailed"` to see each test's own output,
  including the guard's verdict line.

Exit codes: the child's own on a normal finish; 64 for a usage error; 70 when
nothing was started (no desktop, no job object, not on the console's window
station); 71 when something was left behind (a process or the desktop); 124 on
timeout.

## What it refuses

- **No project, or a solution.** Exactly one project file is required. A bare
  `dotnet test` builds and runs every test project in the solution, which is
  how windows reached the operator's screen twice in August 2026.
- **`JJFLEX_TIER1_DESK_FREE`.** It is removed from the child's environment in
  every spelling, so a run through this launcher is allowed by isolation or
  not at all. It never sets that variable.
- **A desktop it did not create.** If the generated name already exists, it
  stops, because `CreateDesktop` would quietly open the existing one.
- **Starting without a cleanup guarantee.** The child is created suspended and
  put into a job object before it runs; if that fails, it is killed unrun.

## What it guarantees afterwards

Every process that ever ran in the job is recorded by id and start time. After
the run the launcher waits for them to finish, kills any that have not, checks
the job's own count is zero, checks each recorded process is gone, closes the
desktop and checks the desktop no longer exists. It prints one line for the
process tree and one for the desktop on every run. The job is also set to kill
everything when its handle closes, so a launcher that is itself killed takes
its children with it.

## What it does not do

It does not make a run silent and it does not redirect settings. Those are the
test process's own guards (`QuietRun`, `TestSettingsRoot`), and they are still
required. `JJFLEX_TIER1_PRIVATE_DESKTOP` is passed through untouched, so if
someone set it to 0 the run refuses rather than the launcher overriding them.

The child is told the desktop's name in `JJFLEX_DESKRUN_DESKTOP`, for whoever
reads the log. The guard never reads it; a test forges it to prove that.
