# JJ Flex Planning Docs (Core)

Last rebuilt: 2026-01-31

This folder contains the long-lived, high-level planning and design documents for the JJ Flex “Modern/Enhanced” redesign.
Agile sprint files live separately under `docs/planning/agile/` (not included in this core bundle).

## Start here
- `JJFlex-Plan.md` — vision, principles, architecture direction, and feature roadmap
- `JJFlex-TODO.md` — rolling backlog / near-term task list

## Key design proposals
- `JJFlex-Keyboard-Proposal.md` — hotkey philosophy (single-step + leader), command finder (F12), conflict handling
- `JJFlex-Menu-Layout.md` — proposed top-level menu structure (slice-centric)
- `Slice-Menu-and-Status.md` — replace “alphabet soup” status with plain-English status + slice menu
- `Audio-Mixer-and-Routing.md` — audio management, recording/playback, “parrot”, and DAX considerations
- `Licensing-Premium.md` — optional premium feature strategy compatible with open source

## Guiding principles
- **Modern mode is default** for new installs; Classic mode preserved for legacy workflows.
- **No key sequence longer than 2 steps** (single-step or leader+key).
- **Slice-centric UX:** most operations are properties/actions on the active slice.
- **Discoverability:** Command Finder (F12) and contextual help reduce the need for external manuals.
