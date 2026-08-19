# rigmeter has moved

Rigmeter no longer lives in this repository. As of Sprint 30 Track G
(2026-08-18) it's its own git repository at `C:\dev\rigmeter`.

Run it from there instead:

```
python C:\dev\rigmeter\rigmeter.py all
```

or, from inside that repo's directory:

```
cd C:\dev\rigmeter
python rigmeter.py all
```

By default it reports on the `C:\dev\JJFlex-NG` checkout. To point it at a
different checkout (a worktree, for example), pass `--repo <path>` AFTER
the subcommand name — e.g. `python rigmeter.py all --repo C:\dev\jjflex-30g`
— or set the `RIGMETER_JJFLEX_ROOT` environment variable.

Nothing else about rigmeter changed in the move: same subcommands, same
NAS snapshot path (`\\nas.macaw-jazz.ts.net\jjflex\historical\stats\`),
same JSON schema, same time series. See `C:\dev\rigmeter\README.md` for
the full tool documentation.

This file exists only as a signpost so a stale path (a doc reference, a
habit, a script) that still points at `tools/rigmeter/` finds its way to
the new location instead of a bare "file not found."
