/* rnv02.c: Xiph RNNoise at the v0.2 tag, under names of our own.

   Built into rnnoise_v02.dll together with the UNMODIFIED upstream sources of
   xiph/rnnoise at 904a876dce1f9ab8860c0a5000ed151f9f6eef58 (tag v0.2) and the
   model its README names (rnnoise_data-0b50c45.tar.gz). Recipe, hashes and
   export list: runtimes/win-x64/native/README.md.

   Why a wrapper: the app runs three RNNoise models (#1019), each in its own
   library, and two RNNoise builds in one process must never be bound by the
   same name (the rule Sprint 49 Track R set). rnnoise.dll (the 70f1d256
   model) exports upstream's rnnoise_* names; this library must not. So
   upstream's functions are compiled in but not exported (DLL_EXPORT is left
   undefined, which leaves RNNOISE_EXPORT empty in the pinned header), and the
   four entry points the engine binds are exported here as rnv02_*.

   Nothing here changes what upstream computes: each export calls exactly one
   upstream function with its arguments as given. No model-buffer entry point
   is exported; loading a model file is the newest library's job, not this
   one's. */

#include "rnnoise.h"

#define RNV02_EXPORT __declspec(dllexport)

/* A denoiser state on the built-in v0.2 model. Opaque to callers. */
RNV02_EXPORT void *rnv02_create(void) {
  return rnnoise_create(NULL);
}

RNV02_EXPORT void rnv02_destroy(void *state) {
  if (state) rnnoise_destroy((DenoiseState *)state);
}

/* Samples per frame: 480 at 48 kHz. */
RNV02_EXPORT int rnv02_frame_size(void) {
  return rnnoise_get_frame_size();
}

/* One frame, upstream's rnnoise_process_frame: 480 samples in the 16-bit range
   in, the denoised frame out (output pointer first), the voice probability as
   the return. As at 70f1d256, the gains computed for a frame are applied to the
   previous one, so the output lags the input by two frames (960 samples). */
RNV02_EXPORT float rnv02_process_frame(void *state, float *out, const float *in) {
  return rnnoise_process_frame((DenoiseState *)state, out, in);
}
