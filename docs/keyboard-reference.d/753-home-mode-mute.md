JJ Flexible Home, slice row (#753):

- New Mode field, after S Meter, in both Classic and Modern tuning. It shows the active slice's mode. Space or Up: next mode. Down: previous mode. Both go through the same path as Alt+M and Alt+Shift+M.
- The Mute field is now on the Modern row too, after XIT, as it already was in Classic. Space or M toggles mute; arriving on it reads the state without changing it.
- The Volume field is now on the Modern row too, right after Mute, as it already was in Classic. Up and Down change the volume; arriving on it reads the level.
- The universal M key still toggles mute from every Home field.

Already applied in docs/help/md/keyboard-reference.md (new "Mode Field Keys" section; the Mute and Volume section no longer says either field is Classic only). No global or leader binding was added, removed or remapped.
