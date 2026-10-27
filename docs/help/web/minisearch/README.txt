MiniSearch 7.2.0, the HTML help's offline search library.

Licence: MIT. Its licence file is LICENSE.txt, beside this one, and travels
with the library into every help build.

Source: the npm package minisearch@7.2.0,
  https://registry.npmjs.org/minisearch/-/minisearch-7.2.0.tgz
  integrity sha512-dqT2XBYUOZOiC5t2HRnwADjhNS2cecp9u+TJRiJ1Qp/f5qjkeT5APcGPjHw+bz89Ms8Jp+cG4AlE+QZ/QnDglg==
  (checked against the downloaded file, 2026-10-27)
Project: https://github.com/lucaong/minisearch

minisearch-7.2.0.js is the package's dist/umd/index.js, byte for byte. It
sets a global named MiniSearch, so it works from a <script src> with no
module loader, and from pages opened straight from disk.

Pinned to an exact version on purpose. To move to another version, replace
the file with that version's dist/umd/index.js, rename it to match, update
the version here and in help-generate.ps1, and rebuild the help.
