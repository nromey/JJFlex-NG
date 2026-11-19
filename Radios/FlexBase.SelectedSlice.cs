using System;

namespace Radios
{
    /// <summary>
    /// FlexBase, the selected slice changing (#960, batch 14 repair round 3).
    /// </summary>
    public partial class FlexBase
    {
        /// <summary>
        /// Raised when the radio reports one of OUR slices as the active
        /// (selected) one, after its facts are committed. Selecting an existing
        /// slice raises the slice's Active and the radio's ActiveSlice, never
        /// <see cref="ModeChanged"/>, so a surface built for the selected
        /// slice's mode (the Filter menu's preset rows) listens to both. Raised
        /// on the radio's thread.
        /// </summary>
        public event Action ActiveSliceChanged;

        private void RaiseActiveSliceChanged() => ActiveSliceChanged?.Invoke();
    }
}
