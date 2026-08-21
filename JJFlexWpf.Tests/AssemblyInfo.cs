using Xunit;

// WPF has thread affinity and this suite drives one STA thread with one
// Dispatcher for the whole assembly. Running collections in parallel would put
// two tests on that thread at once and turn every failure into a mystery.
[assembly: CollectionBehavior(DisableTestParallelization = true)]
