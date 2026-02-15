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
    }
}
