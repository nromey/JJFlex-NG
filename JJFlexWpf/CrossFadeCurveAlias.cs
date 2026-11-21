// The one ramp moved down to JJPortaudio (Sprint 49 Track P2, #873 phase 2), so
// the lag shifter there fades on it too. This alias keeps every JJFlexWpf caller
// (RxAudioPipeline.GlideGain, RecordingPlayer, AlignedSwitch, PcDenoiseSlot)
// unchanged: the name resolves to the same class, and there is still one curve.
global using CrossFadeCurve = JJPortaudio.CrossFadeCurve;
