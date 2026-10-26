JJ Flexible Home, period and comma (bench ruling 2026-10-25):

- Period creates a slice and comma releases the current one from every Home field. Until now they did so only on the Slice field.
- On the Frequency field they go by context. Before a digit is typed they are the slice keys. Once a digit is typed, number entry owns them: period is the decimal point and comma is a thousands separator, so "14,225.000" enters 14.225 megahertz. A period with no digit before it is never a decimal, so a frequency under one megahertz is typed with its zero first.

Already applied in docs/help/md/keyboard-reference.md (the universal Home keys, and the Slice Field Keys section) and docs/help/md/jj-flexible-home.md (Universal Keys). No global or leader binding was added, removed or remapped.
