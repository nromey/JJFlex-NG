using System.Runtime.CompilerServices;

// Sprint 37 Track E (#182): the character-boundary close and the Ctrl
// interrupt keep their moving parts internal — CancellableCwProvider, the
// element builder, the hook's decision function — and the tests that pin
// their invariants live in JJFlexWpf.Tests. Same arrangement Radios has had
// with Radios.Tests all along.
[assembly: InternalsVisibleTo("JJFlexWpf.Tests")]

// #855: tools/soundtiming renders each sound from its own recipe (the
// countdown beat, CW elements, the cut-hold cue) to build its matched
// detector and to time synthesis offline, through the same internals the
// tests use. A harness tool, never shipped.
[assembly: InternalsVisibleTo("soundtiming")]
