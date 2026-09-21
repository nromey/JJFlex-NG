using System.Collections.Generic;
using Radios.KeyOwnership;

namespace Radios.Tests
{
    /// <summary>
    /// Snapshot builders and a fake action recorder for the key-ownership
    /// tests. No window is constructed anywhere here: every snapshot is an
    /// object initialiser.
    /// </summary>
    /// <remarks>
    /// EVERY EXPECTED VALUE IN THESE TESTS IS A HAND-WRITTEN LITERAL taken
    /// from a ruling, never a constant read from the declaration set under
    /// test. A test that derives its expectation from the map it checks cannot
    /// find the map wrong. That is why the recorder below switches on strings
    /// like "home.tune-down" rather than on <c>RuledHomeKeyMap.TuneDown</c>.
    /// </remarks>
    internal static class KeyOwn
    {
        public const string HomeWindow = "window.home";
        public const string DialogWindow = "window.dialog";
        public const long Generation = 7;

        public static readonly GestureDeclarationSet Ruled = RuledHomeKeyMap.CreateSet();

        public static readonly string[] NamedHomeFields =
        {
            "home.frequency", "home.slice", "home.slice-operations",
        };

        public static KeyChord Bare(GestureKey k) => new KeyChord(k);
        public static KeyChord Shifted(GestureKey k, ShiftSide side = ShiftSide.Left) => new KeyChord(k, Shift: side);
        public static KeyChord Ctrl(GestureKey k) => new KeyChord(k, Control: true);
        public static KeyChord AltChord(GestureKey k) => new KeyChord(k, Alt: true);

        public static RadioFacts Radio(char? active = 'A', string slices = "ABC") => new RadioFacts
        {
            Connected = true,
            SessionId = "session-1",
            ActiveSlice = active,
            Slices = slices.ToCharArray(),
        };

        /// <summary>A connected Home in Modern mode, focus on the given field, 1 kHz coarse / 10 Hz fine / 100 Hz Classic digit.</summary>
        public static InputSnapshot OnHome(string field, KeyChord chord) => new InputSnapshot
        {
            Chord = chord,
            ActiveWindow = new WindowContext { Id = HomeWindow, Kind = WindowKind.HomeShell },
            Surface = new FocusedSurface
            {
                WindowId = HomeWindow,
                Role = SurfaceRole.CommandSurface,
                ContextPath = field == "home" ? new[] { "home" } : new[] { field, "home" },
            },
            Mode = OperatingMode.Modern,
            Radio = Radio(),
            Tuning = new TuningContext { CoarseStepHz = 1000, FineStepHz = 10, ClassicDigitHz = 100 },
            ContextGeneration = Generation,
        };

        /// <summary>A dialog is active and focus is on a surface of the given role inside it.</summary>
        public static InputSnapshot InDialog(SurfaceRole role, KeyChord chord, bool modal = true, string context = "dialog.preset") =>
            OnHome("home.frequency", chord) with
            {
                ActiveWindow = new WindowContext
                {
                    Id = DialogWindow,
                    Kind = modal ? WindowKind.ModalDialog : WindowKind.ModelessDialog,
                    OwnerWindowId = HomeWindow,
                },
                ModalBoundaryWindowId = modal ? DialogWindow : null,
                Surface = new FocusedSurface
                {
                    WindowId = DialogWindow,
                    Role = role,
                    ContextPath = new[] { context + ".control", context },
                },
            };

        public static NativeMenuObservation MenuOnHome(NativeMenuState state) => new NativeMenuObservation
        {
            State = state,
            OwnerWindowId = HomeWindow,
            ObservedAtGeneration = Generation,
        };

        public static KeyLayerState JJKeyArmed(string window = HomeWindow) => new KeyLayerState
        {
            Kind = KeyLayerKinds.JJKeyArmed,
            LayerId = "jjkey",
            OriginWindowId = window,
            RadioSessionId = "session-1",
        };

        public static KeyLayerState AudioLayer(string? target, string window = HomeWindow) => new KeyLayerState
        {
            Kind = KeyLayerKinds.ValueLayer,
            LayerId = "audio",
            CurrentTargetId = target,
            OriginWindowId = window,
            RadioSessionId = "session-1",
        };
    }

    /// <summary>
    /// The fake radio. It counts what WOULD have happened, per kind of
    /// effect, so a test can assert the one expected effect AND zero of every
    /// competing one.
    /// </summary>
    internal sealed class FakeActions
    {
        public readonly List<(char? Slice, long? StepHz)> Tunes = new List<(char?, long?)>();
        public readonly List<(char? Slice, int Direction)> SliceGainChanges = new List<(char?, int)>();
        public readonly List<int> PcOutputChanges = new List<int>();
        public readonly List<(char? Slice, int Direction)> PanChanges = new List<(char?, int)>();
        public readonly List<char?> SliceSelections = new List<char?>();
        public readonly List<string> BandChanges = new List<string>();
        public readonly List<string> OtherActions = new List<string>();
        public readonly List<KeyOwnerKind> Handoffs = new List<KeyOwnerKind>();
        public readonly List<RefusalReason> Refusals = new List<RefusalReason>();
        public int TransmitStops;
        public int Unbounds;
        public int NoCommands;

        public KeyDecision Press(InputSnapshot s, GestureDeclarationSet? set = null)
        {
            var d = KeyArbiter.Decide(s, set ?? KeyOwn.Ruled);
            Deliver(d);
            return d;
        }

        public void Deliver(KeyDecision decision)
        {
            switch (decision)
            {
                case ExecuteAction a: Run(a); break;
                case HandToOwner h: Handoffs.Add(h.Owner); break;
                case Refuse r: Refusals.Add(r.Reason); break;
                case Unbound: Unbounds++; break;
                case NoCommandEvent: NoCommands++; break;
            }
        }

        private void Run(ExecuteAction a)
        {
            switch (a.CommandId)
            {
                case "home.tune-up":
                case "home.tune-down":
                    Tunes.Add((a.Target.SliceLetter, a.SignedStepHz));
                    break;
                case "layer.value-increase":
                case "layer.value-decrease":
                    switch (a.Target.LayerTargetId)
                    {
                        case "slice-volume": SliceGainChanges.Add((a.Target.SliceLetter, a.Direction)); break;
                        case "pc-output": PcOutputChanges.Add(a.Direction); break;
                        case "pan": PanChanges.Add((a.Target.SliceLetter, a.Direction)); break;
                        default: OtherActions.Add(a.CommandId + ":" + a.Target.LayerTargetId); break;
                    }
                    break;
                case "slice.jump": SliceSelections.Add(a.Target.SliceLetter); break;
                case "band.up":
                case "band.down": BandChanges.Add(a.CommandId); break;
                case "safety.stop-transmit": TransmitStops++; break;
                default: OtherActions.Add(a.CommandId); break;
            }
        }

        /// <summary>True when no frequency, gain, pan, PC output, band or slice effect of any kind was recorded.</summary>
        public bool RadioUntouched =>
            Tunes.Count == 0 && SliceGainChanges.Count == 0 && PcOutputChanges.Count == 0
            && PanChanges.Count == 0 && SliceSelections.Count == 0 && BandChanges.Count == 0;
    }
}
