Hotkey Editor, while waiting for a new key (#705):

- Tab and Shift+Tab now move focus as they always do, and the editor stops waiting for a key. Before, they were held back and refused with a sentence saying Tab is for navigation.
- Escape still cancels the wait. Enter is still refused, now with a sentence naming Enter rather than listing three keys.

Already applied in docs/help/md/keyboard-reference.md (the opening paragraph on the Hotkey Editor). No binding was added, removed or remapped.
