using System;
using System.Collections.Generic;
using System.Linq;
using Radios;
using Radios.Speech;

namespace JJFlexWpf
{
    public sealed partial class NoiseController
    {
        // ────────────────────────────────────────────────────────────────
        //  Favourites (ruled 2026-10-18 17:19, D4, and 2026-11-07 21:12-21:16)
        // ────────────────────────────────────────────────────────────────

        /// <summary>The favourites in bracket order. Empty when none is marked.</summary>
        public IReadOnlyList<NoiseFavourite> Favourites => _config()?.NoiseFavourites ?? new List<NoiseFavourite>();

        /// <summary>A favourite's spoken name: the operator's own, else the engine's name and its model's ("NNR Premium CW").</summary>
        public string FavouriteName(NoiseFavourite f) =>
            f.Name.Length > 0 ? f.Name : f.Model is { Length: > 0 } m ? NameOf(f.EngineId) + " " + ModelName(m) : NameOf(f.EngineId);

        /// <summary>The model an engine is set to now, for a favourite to carry; null for an engine with no model.</summary>
        public string? CurrentModel(string id)
        {
            var e = _catalog.Find(id);
            if (e == null || !e.Settings.Any(d => d.Name == "model")) return null;
            if (SlotFor(e) is { } slot) return slot.GetSetting(id, "model").Choice;
            return e.Settings.First(d => d.Name == "model").Default.Choice;
        }

        /// <summary>Whether an engine (in its current model) is a favourite.</summary>
        public bool IsFavourite(string id) => Favourites.Any(f => f.SameAs(id, CurrentModel(id)));

        /// <summary>
        /// Add the engine, in the model it is set to now, to the end of the
        /// favourites, or take it out if it is already there. Says which.
        /// </summary>
        public void ToggleFavourite(string id, string source)
        {
            var config = _config();
            if (config == null || _catalog.Find(id) == null) return;
            Current = id;
            string? model = CurrentModel(id);
            var existing = config.NoiseFavourites.FirstOrDefault(f => f.SameAs(id, model));
            var entry = existing ?? new NoiseFavourite { EngineId = id, Model = model };
            string name = FavouriteName(entry);
            if (existing != null) config.NoiseFavourites.Remove(existing);
            else config.NoiseFavourites.Add(entry);
            NoiseSettingsJournal.Record(NoiseJournalKind.Favourite, source, id, model ?? "", existing != null ? "favourite" : "", existing != null ? "" : "favourite");
            _persist();
            Say(L(existing != null ? "audio.noise_surface.favourite_removed" : "audio.noise_surface.favourite_added", ("name", name)), SpeechSubject.NoiseFavourite);
        }

        /// <summary>Set a favourite on or off outright (Home's Favourite checkbox, Settings' checklist).</summary>
        public void SetFavourite(string id, bool favourite, string source)
        {
            if (IsFavourite(id) != favourite) ToggleFavourite(id, source);
        }

        /// <summary>Move a favourite to a position (1-based, clamped): Settings' typed position field and its Move up and Move down.</summary>
        public void MoveFavourite(int from, int toPosition)
        {
            var config = _config();
            if (config == null || from < 0 || from >= config.NoiseFavourites.Count) return;
            var item = config.NoiseFavourites[from];
            config.NoiseFavourites.RemoveAt(from);
            int to = Math.Clamp(toPosition - 1, 0, config.NoiseFavourites.Count);
            config.NoiseFavourites.Insert(to, item);
            _persist();
        }

        /// <summary>Rename a favourite (optional; empty puts back the engine-and-model name).</summary>
        public void RenameFavourite(int index, string name)
        {
            var config = _config();
            if (config == null || index < 0 || index >= config.NoiseFavourites.Count) return;
            config.NoiseFavourites[index].Name = (name ?? "").Trim();
            _persist();
        }

        // ── The bracket keys ──

        private int _stepIndex = -1;
        private bool _toldNoFavourites;

        /// <summary>Start a fresh layer session: the step position and the once-per-session sentence reset.</summary>
        public void BeginSession()
        {
            _stepIndex = -1;
            _toldNoFavourites = false;
            Current = null;
            CurrentLeverIndex = 0;
        }

        /// <summary>The list the brackets walk: the favourites, or every engine when none is marked.</summary>
        public IReadOnlyList<NoiseFavourite> StepList() =>
            Favourites.Count > 0 ? Favourites.ToList()
                : Engines().Select(e => new NoiseFavourite { EngineId = e.Id }).ToList();

        /// <summary>
        /// ] and [: turn the next (or previous) favourite on. The engine
        /// stepped FROM turns off only when both are in the same subject
        /// (Noel 2026-11-07 21:12-21:15): reducer to reducer, or blanker or
        /// notch to blanker or notch; between the two subjects nothing turns
        /// off. Two favourites of ONE engine change its model in place, never
        /// turning it off. An unavailable favourite says why, and the next
        /// press moves on.
        /// </summary>
        public void Step(int direction, string source)
        {
            var list = StepList();
            if (list.Count == 0) return;
            if (Favourites.Count == 0 && !_toldNoFavourites)
            {
                _toldNoFavourites = true;
                Say(Lexicon.Get("audio.noise_surface.no_favourites", VerbosityLevel.Chatty), SpeechSubject.NoiseFavourite, VerbosityLevel.Chatty);
            }
            if (_stepIndex < 0 || _stepIndex >= list.Count)
                _stepIndex = Current == null ? -1 : list.ToList().FindIndex(f => f.EngineId == Current);
            NoiseFavourite? from = _stepIndex >= 0 && _stepIndex < list.Count ? list[_stepIndex] : null;
            int next = _stepIndex < 0 ? (direction > 0 ? 0 : list.Count - 1) : ((_stepIndex + Math.Sign(direction)) % list.Count + list.Count) % list.Count;
            _stepIndex = next;
            var to = list[next];
            var e = _catalog.Find(to.EngineId);
            if (e == null) return;
            Current = e.Id; CurrentLeverIndex = 0;

            if (!CanRun(e, out Action speakWhy)) { speakWhy(); return; }

            if (from != null && from.EngineId == to.EngineId)
            {
                // The same engine in another model: changed in place.
                if (to.Model != null && e.Side == DenoiseSide.Pc && SlotFor(e) is { } slot)
                {
                    slot.TrySetSetting(e.Id, "model", new EngineSettingValue(0, to.Model));
                    if (!IsOn(e.Id)) TurnComputerOn(e, source, speak: false, model: to.Model);
                }
                else if (!IsOn(e.Id)) TurnOn(e, to.Model, source);
                Say(L("audio.noise_surface.on", ("engine", FavouriteName(to))), SpeechSubject.NoiseEngineSwitch);
                return;
            }

            var fromEntry = from == null ? null : _catalog.Find(from.EngineId);
            bool turnOffFrom = fromEntry != null && fromEntry.Group == e.Group && IsOn(fromEntry.Id);
            string? replaced = TurnOn(e, to.Model, source);
            if (turnOffFrom && fromEntry!.Id != replaced) TurnOff(fromEntry, source);
            string? offName = turnOffFrom ? FavouriteName(from!) : replaced != null ? NameOf(replaced) : null;
            Say(offName == null ? L("audio.noise_surface.on", ("engine", FavouriteName(to)))
                                : L("audio.noise_surface.on_replacing", ("engine", FavouriteName(to)), ("other", offName)),
                SpeechSubject.NoiseEngineSwitch);
        }

        /// <summary>Turn an engine on without speaking; returns the computer engine it replaced in its slot, if any.</summary>
        private string? TurnOn(DenoiseEngineEntry e, string? model, string source)
        {
            if (e.Side == DenoiseSide.Radio)
            {
                if (_radio() is { } r && !IsOn(e.Id)) WriteRadioSwitch(e, r, true, source, byApp: false);
                return null;
            }
            if (IsOn(e.Id)) { if (model != null && SlotFor(e) is { } s) SetModelQuietly(e, s, model); return null; }
            return TurnComputerOn(e, source, speak: false, model: model);
        }

        private void TurnOff(DenoiseEngineEntry e, string source)
        {
            if (e.Side == DenoiseSide.Radio) { if (_radio() is { } r) WriteRadioSwitch(e, r, false, source, byApp: false); return; }
            TurnComputerOff(e, source, speak: false);
        }

        /// <summary>Whether stepping onto an engine can turn it on here; when not, the action that says why (Terse form).</summary>
        private bool CanRun(DenoiseEngineEntry e, out Action speakWhy)
        {
            speakWhy = () => { };
            if (e.Side == DenoiseSide.Radio)
            {
                var r = _radio();
                if (r == null) { speakWhy = NoRadio; return false; }
                if (RadioRefusal(e, r, out _) != null) { speakWhy = () => RefuseRadio(e, r); return false; }
                return true;
            }
            if (SlotFor(e) == null) { speakWhy = NoRadio; return false; }
            if (e.Factory == null)
            {
                speakWhy = () => Say(L("audio.noise_surface.not_in_version", ("engine", Lexicon.Get(e.LabelKey))), SpeechSubject.NoiseUnavailable);
                return false;
            }
            var reason = SafeAvailability(e);
            if (reason is Unavailability.None or Unavailability.HelperNotRunning) return true; // not running yet: choosing it starts it
            speakWhy = () => SpeakUnavailable(e, reason, kept: false);
            return false;
        }
    }
}
