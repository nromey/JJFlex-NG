# Changelog

All notable changes to this project will be documented in this file.

## 4.1.8.0
- Added Feature Availability tab in the Radio Info dialog with per-feature status (Diversity, ESC, CW Autotune, NR/ANF variants) and a refresh licenses button. Now, the "radio info" dialog, button renames from rig to radio, has two tabs, the first tells you everything about your radio i.e. serial etc. The Feature availability tab is your window on the world of features that you have access to via your license. If Flex releases a new feature that changes radio operations/adds new goodies and you aren't a subscriber, the system will tell you what features you have and which ones are disabled due to your license. It does not tell you how long you have left on your sdr plus license or what type of license you have, SmartSDR can theoretically do that.
- Added an item under the actions menu which will open the radio info dialog while focussing on and in the feature availability tab.
- cleared the place up a bit from an accessibility perspective. Menus no long have & symbols sprinkled throughout the menu. Though ampersands are cool in their own right, they made menus hard to read with a screen reader. Also added support to tell you if a menu item is checked, unchecked, or unavailable. You'd think that would be easy and straightforward, but no.
- NR/ANF now list individual algorithms. Now, you will know that, for instance, RNN is only available on 8000 series radios. Now you will get a list of all of the supported noise mitigation options and if your radio is not cool for school anymore. Algorithms include Basic NR/ANF, RNN, NRF, NRS, NRL, ANFT, ANFL. See the Flex manual or  www.flexradio.com for more details.
- Now, if you have 1 SFU, you will not see diversity reception or E.S.C.
- Improved audio device handling: if you'd like to change your audio device, select the actions menu and then select "audio device setup ..." If no audio device was selected at setup, JJ Flex will ask you to select a sound device. This should fix reported errors that would occur if no audio device was selected.
- Tweaked labeling/accessibility for the Radio Info entry points.
## 4.1.7.0
I did a cleanup pass here but never shipped it. Think of this as a scratchpad release that I used to squash bugs and keep momentum.
## 4.1.60: Error reporting implemented
- I wired up crash reporting so you no longer need a debug build to send useful crash info. A crash now generates a dump + stack trace that I can actually use to fix things.

## 4.1.5, the subscription aware and wishful thinking update
- I hid controls your radio isn’t licensed for so we don’t tease features you can’t use.
- I tightened up the codebase by removing more JJ Radio leftovers to make things less confusing to maintain.
- Subscribed features now show up both in menus and on the main Flex filters page, so the UI reflects what you actually own.
- I added a Noise Control submenu under Actions (with an eye toward adding shortcuts later).
- Diversity/ESC now disappear when the radio can’t support them, with a clear “not supported” message so it’s obvious why.
- CW Autotune landed in Actions for CW mode; it finds the strongest CW signal using your configured sidetone.
- I added “Daily log trace” because it’s nerdy and useful: it auto-creates daily traces and archives the previous day.

## 4.1.0
- I pulled in FlexLib 4.1.3 so we stay current with upstream bug fixes and API changes.
- Continued the slow, careful work of wiring up the v4 noise/mitigation features.
- Added an ESC dialog (Enhanced Signal Clarity) for radios with enough SCUs.
- Started thinking about subscription-aware UI so we can align with SmartSDR+ gating.
## [4.0.5] - 2025-12-03
- I added the advanced NR/ANF controls (RNN, NRF/NRS/NRL, ANFT/ANFL) and made their gating license-aware with clear tooltips.
- I introduced a `FlexBase.DiversityReady` helper to wrap all the “can we do diversity?” checks in one place (license, antennas, slices, etc.).
- I removed DotNetZip in favor of `System.IO.Compression` and added safe extraction to close a known zip-slip risk.
- I expanded the radio registry so each Flex model maps to `FlexRadio` and we use capability checks instead of hardcoding behavior.
- I bumped version metadata to 4.0.5 to keep installers and release notes aligned.

## [4.0.4] - 2025-xx-xx
- Refactor: Continued migration to FlexLib 4.0 APIs
- Auth: Iterations on GUI auth/SmartLink page behavior

## [4.0.3] - 2025-xx-xx
- Refactor: Initial FlexLib 4.0 adoption across core radio paths
- UI: Stability fixes in Filters and Pan controls

## [4.0.2] - 2025-xx-xx
- Fix: SmartLink connection reliability improvements
- Build: Solution cleanup, reference alignment

## [4.0.1] - 2025-xx-xx
- Base: Start of 4.x line, compatibility with SmartSDR 4.0
- Infra: Project file updates and initial docs for missing features

## Unreleased
- Track FlexLib 4.1.1 and SmartSDR 4.1 updates; evaluate API diffs and incorporate any new DSP/diversity features.
- Added: Feature availability tab now lists per-algorithm NR/ANF statuses (Basic NR/ANF, RNN, NRF, NRS, NRL, ANFT, ANFL) with license/model gating.

