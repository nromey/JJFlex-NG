/* rnvad011.c: Xiph RNNoise v0.1.1's voice-activity branch, on its own.

   Built into rnnoise_vad011.dll together with the UNMODIFIED upstream sources
   of xiph/rnnoise at 6cbfd53eb348a8d394e0757b4025c6ded34eb2b6 (tag v0.1.1).
   Recipe, hashes and export list: runtimes/win-x64/native/README.md.

   Why a translation unit that includes upstream .c files: the feature
   analysis (compute_frame_features, biquad) and the layer kernels
   (compute_dense, compute_gru) are static in upstream's denoise.c and rnn.c.
   Including them here reaches them without editing a line of upstream.

   What runs per frame: the same high-pass biquad and feature analysis as
   rnnoise_process_frame, then input_dense, vad_gru and vad_output, which in
   v0.1.1's compute_rnn run BEFORE, and never read, the noise and denoise
   GRUs. The noise GRU, the denoise GRU, the gain output, the pitch filter
   and the synthesis are skipped. The return value therefore equals what
   rnnoise_process_frame returns for the same input history, bit for bit;
   JJFlexWpf.Tests/RnnoiseVoiceDetectorTests proves it.

   No export begins with rnnoise_: upstream's own functions are compiled in
   but not exported (RNNOISE_BUILD and DLL_EXPORT are left undefined), so two
   RNNoise builds in one process can never be bound by the same name. */

#include "denoise.c"
#include "rnn.c"

#define RNVAD011_EXPORT __declspec(dllexport)

/* A detector state. Opaque to callers; it is upstream's DenoiseState. */
RNVAD011_EXPORT void *rnvad011_create(void) {
  return rnnoise_create(NULL);
}

RNVAD011_EXPORT void rnvad011_destroy(void *state) {
  if (state) rnnoise_destroy((DenoiseState *)state);
}

/* Samples per frame: 480 at 48 kHz, as v0.1.1. */
RNVAD011_EXPORT int rnvad011_frame_size(void) {
  return FRAME_SIZE;
}

/* Voice probability for one frame, 0 to 1. Input is 480 samples in the
   16-bit range (as rnnoise_process_frame takes them). A silent frame
   (band energy below upstream's 0.04) returns 0 and leaves the network
   state untouched, exactly as upstream does. */
RNVAD011_EXPORT float rnvad011_process_frame(void *state, const float *in) {
  DenoiseState *st = (DenoiseState *)state;
  kiss_fft_cpx X[FREQ_SIZE];
  kiss_fft_cpx P[WINDOW_SIZE];
  float x[FRAME_SIZE];
  float Ex[NB_BANDS], Ep[NB_BANDS], Exp[NB_BANDS];
  float features[NB_FEATURES];
  float dense_out[MAX_NEURONS];
  float vad = 0;
  static const float a_hp[2] = {-1.99599, 0.99600};
  static const float b_hp[2] = {-2, 1};
  biquad(x, st->mem_hp_x, in, b_hp, a_hp, FRAME_SIZE);
  if (compute_frame_features(st, X, P, Ex, Ep, Exp, features, x)) return 0;
  compute_dense(st->rnn.model->input_dense, dense_out, features);
  compute_gru(st->rnn.model->vad_gru, st->rnn.vad_gru_state, dense_out);
  compute_dense(st->rnn.model->vad_output, &vad, st->rnn.vad_gru_state);
  return vad;
}

/* The FULL v0.1.1 frame, upstream's rnnoise_process_frame unchanged, under a
   name of our own. It exists for the tests' positive control (and for
   offline comparisons), so the detector can be checked against the whole
   network without a second v0.1.1 library in the repository. Never mix the
   two entry points on one state: each advances the state differently. */
RNVAD011_EXPORT float rnvad011_reference_frame(void *state, float *out, const float *in) {
  return rnnoise_process_frame((DenoiseState *)state, out, in);
}
