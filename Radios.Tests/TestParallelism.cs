using Xunit;

// ── The suite runs SEQUENTIALLY, on purpose (#421) ──
//
// xUnit runs test collections in parallel by default. This suite cannot, and
// the reason is not theoretical - it was caught red-handed on 2026-08-31 after
// four days of an intermittent failure that moved between test classes and so
// looked like four separate flaky tests:
//
//   UnauthorizedAccessException: Access to the path
//   'C:\dev\JJFlex-NG\IntegrationPassUntrackedProbe.cs' is denied.
//
// RadiosNamespaceShadowingTests.The_untracked_source_reader_can_see_an_untracked_file
// WRITES A REAL FILE into the repository root to prove the untracked-source
// reader can see one, then deletes it. Roughly fifteen other test classes walk
// that same tree reading every .cs file. One of them enumerated the probe, the
// probe test deleted it, and the read threw.
//
// THE EXCEPTION IS THE MILD OUTCOME. The bad one is a scanner that reads the
// probe SUCCESSFULLY and draws a conclusion from a file that is not part of the
// codebase - a silent wrong answer in a test whose whole job is to answer
// correctly. Tolerating vanished files would have fixed the loud half and left
// the quiet half exactly as it was.
//
// The filesystem is not the only shared state here either: RadioConfigStatics
// saves and restores process-wide statics around tests, and the Lexicon is a
// static dictionary loaded lazily. A suite with that much shared mutable state
// was relying on luck to run in parallel.
//
// COST, MEASURED: 3 s parallel, 12 s sequential, over 2,142 tests. Four times
// slower and still trivial - and worth it either way, because a suite that
// fails once in four runs for reasons nobody can reproduce trains people to
// re-run it until it goes green, and that habit is how a real regression ships.
[assembly: CollectionBehavior(DisableTestParallelization = true)]
