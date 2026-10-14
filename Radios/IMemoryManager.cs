using System.Collections.Generic;
using Flex.Smoothlake.FlexLib;

namespace Radios
{
    /// <summary>
    /// Minimal element interface for iterating memories.
    /// Sprint 10 Phase 10.3: Decouples FlexBase from FlexMemories.MemoryElement.
    /// </summary>
    public interface IMemoryElement
    {
        /// <summary>The underlying FlexLib Memory object.</summary>
        Memory Value { get; }
    }

    /// <summary>
    /// Interface for rig memory channel operations.
    ///
    /// Sprint 10 Phase 10.3: Extracted from FlexMemories to decouple FlexBase.cs
    /// from the concrete WinForms Form type. FlexBase calls these members via
    /// the memoryHandling property; any implementation must provide them.
    /// </summary>
    public interface IMemoryManager
    {
        /// <summary>
        /// Current memory channel index, or -1 if none.
        /// </summary>
        int CurrentMemoryChannel { get; set; }

        /// <summary>
        /// Total number of memories available.
        /// </summary>
        int NumberOfMemories { get; }

        /// <summary>
        /// Tune the radio to the current memory channel.
        /// </summary>
        bool SelectMemory();

        /// <summary>
        /// Find and select the memory with the given full name.
        /// </summary>
        bool SelectMemoryByName(string name);

        /// <summary>
        /// Get sorted list of full memory names (group.name format).
        /// </summary>
        List<string> MemoryNames();

        /// <summary>
        /// Sorted collection of memory elements for enumeration.
        /// </summary>
        IReadOnlyList<IMemoryElement> SortedMemories { get; }

        /// <summary>
        /// Ask the radio for a new memory, wait for it to confirm, and seed it
        /// with a frequency and mode so it sorts and displays. Null when the
        /// radio did not answer within <paramref name="timeoutMs"/>.
        /// </summary>
        /// <remarks>
        /// Sprint 48 Track D (#700). The Memories dialog's Add button had
        /// invoked a hook nothing assigned since Sprint 11; this is the radio
        /// side of wiring it. Creation is asynchronous in FlexLib - "memory
        /// create" goes out and the radio answers through MemoryAdded - and a
        /// memory the radio has just created has no frequency and no mode,
        /// which <see cref="SortedMemories"/> skips as a null element. So the
        /// new memory is seeded before anyone lists it.
        /// </remarks>
        Memory AddMemory(double freqMHz, string mode, int timeoutMs);

        /// <summary>
        /// Remove a memory from the radio and wait, up to the timeout, for the
        /// radio to drop it from its list before re-sorting.
        /// </summary>
        void RemoveMemory(Memory memory, int timeoutMs);

        /// <summary>
        /// Re-read and re-sort the radio's memory list. A rename or a group
        /// change moves a memory in the sorted list, and the list is what every
        /// consumer reads.
        /// </summary>
        void Refresh();
    }
}
