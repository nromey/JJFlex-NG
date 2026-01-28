namespace Radios
{
    /// <summary>
    /// Flex-6000 series entry point for the Flex-only build.
    /// </summary>
    public class FlexRadio : FlexBase
    {
        public FlexRadio() : base(new OpenParms { ProgramName = "JJFlex" })
        {
        }

        public override int RigID => RadioSelection.RIGIDFlex6300;
    }
}
