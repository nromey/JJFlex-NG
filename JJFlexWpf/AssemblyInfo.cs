using System.Runtime.CompilerServices;

// Sprint 37 Track E (#182): the character-boundary close and the Ctrl
// interrupt keep their moving parts internal — CancellableCwProvider, the
// element builder, the hook's decision function — and the tests that pin
// their invariants live in JJFlexWpf.Tests. Same arrangement Radios has had
// with Radios.Tests all along.
[assembly: InternalsVisibleTo("JJFlexWpf.Tests")]
