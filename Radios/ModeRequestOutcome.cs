namespace Radios
{
    /// <summary>
    /// What became of a request made through <see cref="FlexBase.RequestMode"/>
    /// (#312, repair round one). Only <see cref="Sent"/> means anything left;
    /// even that is not proof the radio applied it.
    /// </summary>
    public enum ModeRequestOutcome
    {
        /// <summary>Queued to our slice. Read the slice back before saying it changed.</summary>
        Sent,

        /// <summary>Not requested: the radio is transmitting, and the mode is never changed under a carrier.</summary>
        RefusedTransmitting,

        /// <summary>Not requested: there is no slice of ours to write.</summary>
        RefusedNoSlice,

        /// <summary>Not requested: the radio's command loop is not running, so the write would be dropped.</summary>
        RefusedNotRunning,
    }
}
