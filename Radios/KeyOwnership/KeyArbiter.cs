using System.Collections.Generic;
using System.Linq;

namespace Radios.KeyOwnership
{
    /// <summary>Command identities the arbiter itself produces, rather than reading from a declaration.</summary>
    public static class KeyOwnershipCommands
    {
        /// <summary>Escape while this application holds an active or pending transmit operation.</summary>
        public const string SafetyStop = "safety.stop-transmit";
    }

    /// <summary>
    /// Decides who owns a keystroke. One pure function from a snapshot and a
    /// validated declaration set to a decision; it touches no window, no radio
    /// and no speech, and it performs nothing.
    /// </summary>
    /// <remarks>
    /// <para>
    /// THE ORDER BELOW IS NORMATIVE. It is the whole point of this type, and
    /// the tests pin each step against the one beneath it:
    /// </para>
    /// <para>
    /// 1. establish the actual destination; 2. an already-owned key release,
    /// and Escape as a transmit stop; 3. a native menu owns its navigation;
    /// 4. an explicit keyboard layer owns its declared keys; 5. reserved
    /// application commands beat local shortcuts; 6. editing and ordinary
    /// control interaction stay local; 7. on a command surface, the declared
    /// command; 8. an explicit disposition for everything left.
    /// </para>
    /// <para>
    /// Called "arbiter" and not the word the design document uses for this
    /// role, because the task register already spends that word on an
    /// unrelated question (which account owns a radio).
    /// </para>
    /// </remarks>
    public static class KeyArbiter
    {
        public static KeyDecision Decide(InputSnapshot snapshot, GestureDeclarationSet declarations)
        {
            var s = snapshot;

            // ── 1. The actual destination ───────────────────────────────
            if (s.Destination == InputDestination.OutsideApplication)
                return new HandToOwner { Owner = KeyOwnerKind.OutsideApplication, DecidedAtStep = 1 };

            bool modalIncoherent = s.ModalBoundaryWindowId != null
                && s.ActiveWindow.Id != s.ModalBoundaryWindowId
                && s.ActiveWindow.OwnerWindowId != s.ModalBoundaryWindowId;
            bool destinationUnknown = s.Destination == InputDestination.Unknown || modalIncoherent;

            // A surface in any window but the active one has no claim: not a
            // Home field remembered behind a dialog, not an inactive modeless
            // window.
            FocusedSurface? surface = s.Surface != null && s.Surface.WindowId == s.ActiveWindow.Id ? s.Surface : null;

            // ── 2. An owned release, and the stop request ───────────────
            // These run even when the destination is unknown: refusing to end
            // an operation we started is the one refusal that is not safe.
            if (s.Phase == KeyPhase.Up)
            {
                var op = s.OwnedOperations.FirstOrDefault(o => o.Key == s.Chord.Key);
                if (op != null)
                    return new HandToOwner { Owner = KeyOwnerKind.OperationOwner, OwnerId = op.OperationId, DecidedAtStep = 2 };
            }
            else if (s.Chord.Key == GestureKey.Escape && s.Chord.IsBare
                && (s.LocalTransmitActiveOrPending || s.RadioReportsTransmit))
            {
                return new ExecuteAction
                {
                    CommandId = KeyOwnershipCommands.SafetyStop,
                    Target = TargetFor(ActionTargetKind.Application, s),
                    Layers = LayerDisposition.DropWithoutRestore,
                    DecidedAtStep = 2,
                };
            }

            if (destinationUnknown)
            {
                if (IsNotACommand(s)) return new NoCommandEvent { DecidedAtStep = 1 };
                return new Refuse { Reason = RefusalReason.ContextInTransition, DecidedAtStep = 1 };
            }

            // ── 3. A native menu owns its navigation transaction ────────
            bool staleMenuIgnored = false;
            bool menuUnreconciled = false;
            if (s.Menu.State != NativeMenuState.None)
            {
                if (s.Menu.OwnerWindowId != s.ActiveWindow.Id)
                    staleMenuIgnored = true;        // another window is active: that menu cannot be owning input
                else if (s.Menu.ObservedAtGeneration != s.ContextGeneration)
                    menuUnreconciled = true;        // cannot tell; decide below, but allow no action
                else
                    return DecideInsideMenu(s, declarations);
            }

            KeyDecision decision = DecideOutsideMenu(s, surface, declarations);

            if (menuUnreconciled && decision is ExecuteAction blocked)
                decision = new Refuse
                {
                    Reason = RefusalReason.ContextInTransition,
                    CommandId = blocked.CommandId,
                    Target = blocked.Target,
                    DecidedAtStep = 3,
                    Layers = blocked.Layers,
                };
            return staleMenuIgnored ? decision with { StaleMenuReportIgnored = true } : decision;
        }

        /// <summary>
        /// Re-checks an action that had to wait (behind a menu dismissal)
        /// against the world as it is now. The action is never re-aimed: if its
        /// radio, slice, window or modal boundary changed, it is refused as
        /// stale and nothing runs.
        /// </summary>
        public static KeyDecision Revalidate(ExecuteAction action, InputSnapshot now)
        {
            var t = action.Target;
            bool stale =
                t.RadioSessionId != now.Radio.SessionId
                || t.WindowId != now.ActiveWindow.Id
                || t.ModalBoundaryWindowId != now.ModalBoundaryWindowId
                || (t.Kind == ActionTargetKind.ActiveSlice && t.SliceLetter != now.Radio.ActiveSlice)
                || (t.SliceLetter != null && !now.Radio.Slices.Contains(t.SliceLetter.Value))
                // The menu closing is the one generation change a deferred action expects.
                || (action.Delivery == ActionDelivery.Immediate && t.Generation != now.ContextGeneration);

            if (!stale) return action;
            return new Refuse
            {
                Reason = RefusalReason.StaleTarget,
                CommandId = action.CommandId,
                Target = action.Target,
                DecidedAtStep = action.DecidedAtStep,
            };
        }

        /// <summary>Whether a suspended layer may take the keys back: same window, same radio session, target still there.</summary>
        public static bool CanResume(KeyLayerState layer, InputSnapshot now) =>
            layer.TargetValid
            && layer.OriginWindowId == now.ActiveWindow.Id
            && (layer.RadioSessionId == null || layer.RadioSessionId == now.Radio.SessionId);

        // ────────────────────────────────────────────────────────────────

        private static bool IsNotACommand(InputSnapshot s) => s.Phase == KeyPhase.Up || s.Chord.IsModifierKey;

        private static KeyDecision DecideInsideMenu(InputSnapshot s, GestureDeclarationSet set)
        {
            if (!IsNotACommand(s) && !s.IsTextComposition)
            {
                var transfer = set.Declarations.FirstOrDefault(d =>
                    d.Kind == DeclarationKind.Command && d.Reserved && d.MayLeaveMenu
                    && d.LayerKinds == KeyLayerKinds.None && Live(d, s));
                if (transfer != null)
                    return Resolve(transfer, s, set, 3, ActionDelivery.AfterMenuDismissal, false, modalApplies: true);
            }
            // Arrows, Enter, Escape, first letters, mnemonics, releases and an
            // unknown key alike: the menu's. Never a Home field's.
            return new HandToOwner { Owner = KeyOwnerKind.NativeMenu, OwnerId = s.Menu.OwnerWindowId, DecidedAtStep = 3 };
        }

        private static KeyDecision DecideOutsideMenu(InputSnapshot s, FocusedSurface? surface, GestureDeclarationSet set)
        {
            if (IsNotACommand(s)) return new NoCommandEvent { DecidedAtStep = 8 };

            // ── 4. An explicit keyboard layer ───────────────────────────
            var layers = LayerDisposition.Keep;
            bool continued = false;
            var layer = s.Layer;
            if (layer != null && layer.Kind != KeyLayerKinds.None && !layer.Suspended)
            {
                if (layer.OriginWindowId != s.ActiveWindow.Id)
                {
                    // Stranded in a window it was not opened from. It claims
                    // nothing, and the coordinator is told to drop it.
                    layers = LayerDisposition.DropWithoutRestore;
                }
                else
                {
                    var owned = DecideInLayer(s, layer, set, out continued);
                    if (owned != null) return owned;
                }
            }

            KeyDecision below = DecideBelowLayers(s, surface, set, continued);
            return layers == LayerDisposition.Keep ? below : below with { Layers = layers };
        }

        private static KeyDecision? DecideInLayer(InputSnapshot s, KeyLayerState layer, GestureDeclarationSet set, out bool continued)
        {
            continued = false;
            var match = s.IsTextComposition ? null : set.Declarations
                .Where(d => (d.LayerKinds & layer.Kind) != KeyLayerKinds.None
                    && (d.LayerId == null || d.LayerId == layer.LayerId)
                    && Live(d, s))
                .OrderBy(d => d.LayerId == null ? 1 : 0)
                .FirstOrDefault();

            if (match == null)
            {
                if (layer.Kind == KeyLayerKinds.HelpArmed) return null;   // owns only its documented keys
                if (layer.Kind == KeyLayerKinds.LayerHelpList)
                    return new HandToOwner { Owner = KeyOwnerKind.LayerHelpList, OwnerId = layer.LayerId, DecidedAtStep = 4 };
                // Not the layer's key. It gets the layer's own answer; it does
                // NOT fall through to tuning or to anything else beneath.
                return new Unbound { ContextKind = UnboundContextKind.Layer, ContextId = layer.LayerId, DecidedAtStep = 4 };
            }

            if (match.Kind == DeclarationKind.LayerPassThrough)
            {
                continued = true;
                return null;
            }
            return Resolve(match, s, set, 4, ActionDelivery.Immediate, false, modalApplies: true);
        }

        private static KeyDecision DecideBelowLayers(InputSnapshot s, FocusedSurface? surface, GestureDeclarationSet set, bool continued)
        {
            SurfaceRole role = surface?.Role ?? SurfaceRole.Unknown;
            bool classified = surface == null || (role != SurfaceRole.Unknown && role != SurfaceRole.ForeignEmbedded);

            // ── 5. Reserved application commands ────────────────────────
            // Not over an unclassified or foreign surface: those stay local,
            // and a reserved chord that cannot reach them is a declared
            // coverage limit, not something to take by force.
            if (classified && !s.IsTextComposition)
            {
                var reserved = set.Declarations.FirstOrDefault(d =>
                    d.Kind == DeclarationKind.Command && d.Reserved && d.LayerKinds == KeyLayerKinds.None && Live(d, s));
                if (reserved != null)
                    return Resolve(reserved, s, set, 5, ActionDelivery.Immediate, continued, modalApplies: true);
            }

            if (surface == null)
            {
                var wide = s.IsTextComposition ? null : BestSurfaceDeclaration(s, null, set);
                if (wide != null) return Resolve(wide, s, set, 7, ActionDelivery.Immediate, continued, modalApplies: true);
                return new Unbound { ContextKind = UnboundContextKind.NoFocusedControl, ContextId = s.ActiveWindow.Id, DecidedAtStep = 8 };
            }

            // ── 6. Editing and ordinary control interaction stay local ──
            if (role != SurfaceRole.CommandSurface)
                return DecideLocal(s, surface, role, set, continued);

            // ── 7. The declared command on a command surface ────────────
            var chosen = s.IsTextComposition ? null : BestSurfaceDeclaration(s, surface, set);
            if (chosen != null)
                return Resolve(chosen, s, set, 7, ActionDelivery.Immediate, continued,
                    modalApplies: chosen.Reach == InvocationReach.AcrossApplication);

            // ── 8. An explicit disposition ──────────────────────────────
            return new Unbound
            {
                ContextKind = UnboundContextKind.CommandSurface,
                ContextId = surface.ContextPath.Count > 0 ? surface.ContextPath[0] : s.ActiveWindow.Id,
                DecidedAtStep = 8,
            };
        }

        private static KeyDecision DecideLocal(InputSnapshot s, FocusedSurface surface, SurfaceRole role,
            GestureDeclarationSet set, bool continued)
        {
            string? ownerId = surface.ContextPath.Count > 0 ? surface.ContextPath[0] : null;

            if (role == SurfaceRole.Unknown || role == SurfaceRole.ForeignEmbedded)
                return new HandToOwner { Owner = KeyOwnerKind.LocalUnclassified, OwnerId = ownerId, DecidedAtStep = 6, ContinuedBelowLayer = continued };

            KeyOwnerKind controlOwner = role == SurfaceRole.TextEditor ? KeyOwnerKind.TextEditor : KeyOwnerKind.StandardControl;

            // Editing first for editing gestures, control navigation next …
            if (s.IsTextComposition || IsControlsOwnGesture(s.Chord, surface, role))
                return new HandToOwner { Owner = controlOwner, OwnerId = ownerId, DecidedAtStep = 6, ContinuedBelowLayer = continued };

            // … declared window shortcuts next, then the application's
            // non-reserved commands …
            var declared = BestSurfaceDeclaration(s, surface, set);
            if (declared != null)
                return Resolve(declared, s, set, 6, ActionDelivery.Immediate, continued,
                    modalApplies: declared.Reach == InvocationReach.AcrossApplication);

            // … and default Enter, Escape and Tab last.
            if (s.Chord.Key is GestureKey.Enter or GestureKey.Escape or GestureKey.Tab && !s.Chord.Control && !s.Chord.Alt)
                return new HandToOwner { Owner = KeyOwnerKind.WindowDefault, OwnerId = s.ActiveWindow.Id, DecidedAtStep = 6, ContinuedBelowLayer = continued };

            return new HandToOwner
            {
                Owner = controlOwner,
                OwnerId = ownerId,
                ReportIfUnhandled = s.Chord.Control || s.Chord.Alt || s.Chord.Windows,
                DecidedAtStep = 6,
                ContinuedBelowLayer = continued,
            };
        }

        /// <summary>Gestures a control keeps for itself ahead of any declared window shortcut.</summary>
        private static bool IsControlsOwnGesture(KeyChord c, FocusedSurface surface, SurfaceRole role)
        {
            if (c.Windows || c.Alt) return false;
            GestureKey k = c.Key;
            bool function = k >= GestureKey.F1 && k <= GestureKey.F12;

            if (k == GestureKey.Escape) return !c.Control && surface.HasOpenPopup;   // a combo popup closes before its dialog

            if (role == SurfaceRole.TextEditor)
            {
                if (!c.Control)
                {
                    if (k == GestureKey.Enter) return surface.AcceptsReturn;
                    return k != GestureKey.Tab && !function;       // typing, caret, selection
                }
                return k is GestureKey.A or GestureKey.C or GestureKey.V or GestureKey.X or GestureKey.Y or GestureKey.Z
                    or GestureKey.Left or GestureKey.Right or GestureKey.Up or GestureKey.Down
                    or GestureKey.Home or GestureKey.End or GestureKey.Back or GestureKey.Delete or GestureKey.Insert;
            }

            // A standard control: navigation, activation and type-ahead.
            if (c.Control) return false;
            return k != GestureKey.Enter && k != GestureKey.Tab && !function;
        }

        /// <summary>
        /// The one declaration a surface's key resolves to, by DECLARED
        /// precedence rather than by which handler happened to run first:
        /// authoritative, then a permitted registry override, then the nearest
        /// context, then the application-wide fallback.
        /// </summary>
        private static GestureDeclaration? BestSurfaceDeclaration(InputSnapshot s, FocusedSurface? surface, GestureDeclarationSet set)
        {
            GestureDeclaration? best = null;
            int bestRank = int.MaxValue;
            foreach (var d in set.Declarations)
            {
                if (d.LayerKinds != KeyLayerKinds.None || d.Reserved || d.Kind == DeclarationKind.LayerPassThrough) continue;
                if (!Live(d, s)) continue;

                int depth;
                if (d.Reach == InvocationReach.AcrossApplication) depth = 1000;
                else
                {
                    if (surface == null) continue;
                    depth = IndexOf(surface.ContextPath, d.ContextId);
                    if (depth < 0) continue;
                    if (d.Reach == InvocationReach.OwnSurface && depth != 0) continue;
                }

                int rank = d.Precedence switch
                {
                    DeclarationPrecedence.Authoritative => -2_000_000,
                    DeclarationPrecedence.OverridesLocal => -1_000_000,
                    _ => 0,
                } + depth;

                if (rank < bestRank) { best = d; bestRank = rank; }
            }
            return best;
        }

        private static int IndexOf(IReadOnlyList<string> path, string? id)
        {
            for (int i = 0; i < path.Count; i++) if (path[i] == id) return i;
            return -1;
        }

        /// <summary>Live = this exact chord, in this mode. No modifier is ever dropped to make a match.</summary>
        private static bool Live(GestureDeclaration d, InputSnapshot s) =>
            d.Chord.Matches(s.Chord) && GestureDeclarationSet.ScopeMatchesMode(d.Scope, s.Mode);

        /// <summary>
        /// Turns the chosen declaration into its result. Whatever that result
        /// is, the declaration REMAINS the owner: an unavailable command is
        /// refused with its reason and never falls through to something else
        /// on the same key.
        /// </summary>
        private static KeyDecision Resolve(GestureDeclaration d, InputSnapshot s, GestureDeclarationSet set, int step,
            ActionDelivery delivery, bool continued, bool modalApplies)
        {
            if (d.Kind == DeclarationKind.Retired)
                return new Refuse { Reason = RefusalReason.Relocated, CommandId = d.CommandId, Relocation = d.Relocation, DecidedAtStep = step };

            if (s.Phase == KeyPhase.Repeat && !d.Repeats)
                return new NoCommandEvent { Consume = true, DecidedAtStep = step };

            var layer = s.Layer;
            var target = TargetFor(ActionTargetKind.Application, s);
            bool wrongAxis = false, noTargetChosen = false;

            switch (d.Target)
            {
                case TargetRule.ActiveSlice:
                    target = target with { Kind = ActionTargetKind.ActiveSlice, SliceLetter = s.Radio.ActiveSlice };
                    break;
                case TargetRule.SliceNamedByKey:
                    target = target with { Kind = ActionTargetKind.NamedSlice, SliceLetter = (char)('A' + (s.Chord.Key - GestureKey.A)) };
                    break;
                case TargetRule.LayerTargetSelected:
                    target = target with { Kind = ActionTargetKind.LayerValue, LayerId = d.LayerId, LayerTargetId = d.SelectsLayerTargetId };
                    break;
                case TargetRule.LayerCurrentTarget:
                    var chosen = layer?.CurrentTargetId == null ? null
                        : set.LayerTargets.FirstOrDefault(t => t.LayerId == layer.LayerId && t.TargetId == layer.CurrentTargetId);
                    if (chosen == null) { noTargetChosen = true; break; }
                    wrongAxis = d.Axis != ValueLayerAxes.None && (chosen.Axes & d.Axis) == ValueLayerAxes.None;
                    target = target with
                    {
                        Kind = ActionTargetKind.LayerValue,
                        LayerId = chosen.LayerId,
                        LayerTargetId = chosen.TargetId,
                        SliceLetter = chosen.PerSlice ? s.Radio.ActiveSlice : null,
                    };
                    break;
            }

            if (noTargetChosen || wrongAxis)
                return new Unbound { ContextKind = UnboundContextKind.Layer, ContextId = layer?.LayerId ?? "", WrongAxis = wrongAxis, DecidedAtStep = step };

            RefusalReason? refusal = null;
            bool layerValue = target.Kind == ActionTargetKind.LayerValue;
            bool perSlice = layerValue && set.LayerTargets.Any(t =>
                t.LayerId == target.LayerId && t.TargetId == target.LayerTargetId && t.PerSlice);

            if (modalApplies && s.ModalBoundaryWindowId != null && d.Modal == ModalPolicy.RefusedInModal)
                refusal = RefusalReason.ModalRestriction;
            else if (layerValue && layer != null
                && (!layer.TargetValid || (layer.RadioSessionId != null && layer.RadioSessionId != s.Radio.SessionId)))
                refusal = RefusalReason.StaleTarget;
            else if ((d.Requires != CommandRequirements.None || perSlice) && !s.Radio.Connected)
                refusal = RefusalReason.NoRadio;
            else if (((d.Requires & CommandRequirements.ActiveSlice) != 0 || perSlice) && s.Radio.ActiveSlice == null)
                refusal = RefusalReason.MissingSlice;
            else if ((d.Requires & CommandRequirements.NamedSlice) != 0
                && (target.SliceLetter == null || !s.Radio.Slices.Contains(target.SliceLetter.Value)))
                refusal = RefusalReason.MissingSlice;
            else if (s.CommandsWithoutExecutor.Contains(d.CommandId))
                refusal = RefusalReason.NoExecutor;

            if (refusal != null)
                return new Refuse { Reason = refusal.Value, CommandId = d.CommandId, Target = target, DecidedAtStep = step };

            long? stepHz = d.Step switch
            {
                StepSource.None => null,
                _ when s.Mode == OperatingMode.Classic => d.Direction * s.Tuning.ClassicDigitHz,
                StepSource.TuningFine => d.Direction * s.Tuning.FineStepHz,
                _ => d.Direction * s.Tuning.CoarseStepHz,
            };

            return new ExecuteAction
            {
                CommandId = d.CommandId,
                RegistryCommand = d.RegistryCommand,
                Target = target,
                Repeats = d.Repeats,
                Direction = d.Direction,
                SignedStepHz = stepHz,
                Fine = d.Fine,
                Delivery = delivery,
                LayerEffect = d.LayerEffect,
                OpensLayerId = d.OpensLayerId,
                ContinuedBelowLayer = continued,
                DecidedAtStep = step,
            };
        }

        private static ActionTarget TargetFor(ActionTargetKind kind, InputSnapshot s) => new ActionTarget
        {
            Kind = kind,
            RadioSessionId = s.Radio.SessionId,
            WindowId = s.ActiveWindow.Id,
            ModalBoundaryWindowId = s.ModalBoundaryWindowId,
            Generation = s.ContextGeneration,
        };
    }
}
