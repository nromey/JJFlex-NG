// help.js -- loaded by every page of the JJ Flexible HTML help.
//
// Moves focus to the section the address names (#section), when the page
// loads and whenever the address changes, so a screen reader starts reading
// there. The browser's own scroll to an anchor moves the view, not the
// reader; focusing the element moves both. This is the technique the
// JJ Flexible Fix window uses, and NVDA follows it there (#373).
//
// An element that cannot take focus is given tabindex="-1" first. One that
// already can (a link, a button) is left alone, because giving a button
// tabindex="-1" removes it from the Tab order (#449).
(function () {
  "use strict";

  function canTakeFocus(el) {
    if (el.hasAttribute("tabindex")) return true;
    var tag = el.tagName.toLowerCase();
    if (tag === "a" || tag === "area") return el.hasAttribute("href");
    return tag === "button" || tag === "input" || tag === "select" || tag === "textarea";
  }

  function focusAddressTarget() {
    var hash = window.location.hash;
    if (!hash || hash.length < 2) return;
    var id;
    try { id = decodeURIComponent(hash.substring(1)); } catch (e) { id = hash.substring(1); }
    var el = document.getElementById(id);
    if (!el) return;
    if (!canTakeFocus(el)) el.setAttribute("tabindex", "-1");
    el.focus();
  }

  if (document.readyState === "loading") {
    document.addEventListener("DOMContentLoaded", focusAddressTarget);
  } else {
    focusAddressTarget();
  }
  window.addEventListener("hashchange", focusAddressTarget);
})();
