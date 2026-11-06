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
tools/deskrun/bin/x64/Debug/net10.0-windows/deskrun.exe JJFlexWpf.Tests/JJFlexWpf.Tests.csproj -c Debug -p:Platform=x64 --filter "FullyQualifiedName~DialogEarlyExitTests"
```

- `--timeout SECONDS` before the project sets how long the run may take
  (default 600). After that the whole process tree is killed.
- After the project, only these options are forwarded to `dotnet test`:
  `-c`/`--configuration`, `-p:Platform=` (a plain name and nothing else),
  `--filter`, `--logger` (console or trx only), `--results-directory`,
  `--blame-hang`, `--blame-hang-timeout`, `--no-build`, `--no-restore`,
  `--list-tests`, `--nologo` and `-v`/`--verbosity`. Anything else is refused
  before a desktop is created, and the refusal names it.
- Add `--logger "console;verbosity=detailed"` to see each test's own output,
  including the guard's verdict line.

Exit codes: the child's own on a normal finish; 64 when the arguments or the
project's build files were refused (nothing was created); 70 when nothing was
started (no desktop, no job object, not on the console's window station, or
the child could not be created inside the job); 71 when something was left
behind (a process or the desktop); 124 on timeout.

## What it refuses

- **No project, or a solution.** Exactly one project file is required. A bare
  `dotnet test` builds and runs every test project in the solution, which is
  how windows reached the operator's screen twice in August 2026.
- **Every way to set testhost's environment or settings from the command
  line.** VSTest installs environment variables in testhost after the launcher
  has built dotnet's environment: from `--environment`/`-e`, from a
  `--settings`/`-s` file, from inline settings after a bare `--`, and from
  properties that choose a settings file (`RunSettingsFilePath`,
  `VSTestSetting`, `VSTestCLIRunSettings`). Those, any other `-p:` property,
  response files, data collectors, adapter paths and anything unknown are all
  refused, because the options are an allowlist.
- **A project whose own build files can choose settings.** The project file,
  and every `Directory.Build.props` and `Directory.Build.targets` from its
  folder up to the drive root, must not mention a runsettings file, a VSTest
  property, or a redirection of the build's imports, and must not carry an
  explicit `<Import>` (its contents are not read here). A `Directory.Build.rsp`
  anywhere above the project is refused, because it adds arguments to every
  build. None of these exists today; the check refuses the day one appears.
- **Inherited variables that act as settings or inject code.** MSBuild reads
  every environment variable as a property, so a variable named like a VSTest
  property chooses settings as surely as `-p:` does. The child does not get
  `JJFLEX_TIER1_DESK_FREE`, anything starting `VSTest` or `VSTEST_`,
  `RunSettingsFilePath`, the `CustomBeforeMicrosoft*`/`CustomAfterMicrosoft*`
  and `DirectoryBuild*` import redirections, the startup-hook variable or the
  profiler variables. The launcher says which it removed. It never sets the
  desk-free variable.
- **A desktop it did not create.** If the generated name already exists, it
  stops, because `CreateDesktop` would quietly open the existing one.
- **Starting without a cleanup guarantee.** The child is created already
  inside a kill-on-close job object (`PROC_THREAD_ATTRIBUTE_JOB_LIST`), so
  there is no moment at which it exists outside the job, and it inherits only
  its three pipe ends. Before it resumes, the launcher checks it is in the job;
  if anything about that fails, it is killed unrun. There is no fallback to
  assigning it afterwards: a launcher killed in that gap used to leave a
  suspended child behind.

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

## For lifecycle tests only

`--lifecycle-barrier after-create` or `--lifecycle-barrier after-resume`,
given before the project, makes the launcher print a `BARRIER` line naming
itself and its child and then wait to be killed, so a test can kill it at
exactly that point and check the child went with it. It is a launcher option,
consumed before the project and never forwarded, so nothing after the project
can reach it. If nobody kills the launcher within two minutes it carries on.

## What it cannot close

Code that the test process runs can still set its own environment, or start a
process on another desktop by name, or ask an already-running program (a
browser, Explorer, a COM server) to open a window on its behalf; the last of
those runs outside the job. The guard and the job contain a test run, not a
hostile one.
