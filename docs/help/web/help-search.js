// help-search.js -- the Search page of the JJ Flexible HTML help.
//
// Runs entirely in the page, offline: MiniSearch (MIT, pinned, beside this
// file) indexes window.JJFLEX_HELP_SEARCH, which the help build writes into
// search-data.js. The words come from the page's address (Search.htm?q=...),
// which every page's search box submits to, so a search is an ordinary
// page load and Back returns to it.
//
// Results are computed once per search, never per keystroke, so the screen
// reader is not interrupted mid-word. Focus then moves to the results
// heading, which says how many were found and for what.
(function () {
  "use strict";

  function el(tag, text) {
    var e = document.createElement(tag);
    if (text !== undefined) e.textContent = text;
    return e;
  }

  function queryFromAddress() {
    var m = /[?&]q=([^&#]*)/.exec(window.location.search);
    if (!m) return "";
    try { return decodeURIComponent(m[1].replace(/\+/g, " ")).trim(); } catch (e) { return ""; }
  }

  function run() {
    var out = document.getElementById("help-search-results");
    var box = document.getElementById("help-search-box");
    if (!out) return;
    var q = queryFromAddress();
    if (box) box.value = q;
    if (!q) return;

    var data = window.JJFLEX_HELP_SEARCH || [];
    var mini = new MiniSearch({
      fields: ["heading", "page", "keywords", "text"],
      storeFields: ["href", "page", "heading", "excerpt"]
    });
    mini.addAll(data);
    var results = mini.search(q, {
      prefix: true,
      fuzzy: 0.2,
      boost: { heading: 3, keywords: 3, page: 2 }
    });

    var heading = el("h2");
    heading.id = "help-search-heading";
    heading.setAttribute("tabindex", "-1");
    if (results.length === 0) {
      heading.textContent = "No results for " + q;
      out.appendChild(heading);
      var p = el("p");
      p.appendChild(document.createTextNode("Try other words, or look in the "));
      var a = el("a", "Index");
      a.href = "Index.htm";
      p.appendChild(a);
      p.appendChild(document.createTextNode("."));
      out.appendChild(p);
    } else {
      heading.textContent = results.length + (results.length === 1 ? " result for " : " results for ") + q;
      out.appendChild(heading);
      var list = el("ul");
      list.className = "help-results";
      for (var i = 0; i < results.length; i++) {
        var r = results[i];
        var li = el("li");
        var link = el("a", r.heading ? r.page + ": " + r.heading : r.page);
        link.href = r.href;
        li.appendChild(link);
        if (r.excerpt) li.appendChild(el("p", r.excerpt));
        list.appendChild(li);
      }
      out.appendChild(list);
    }
    heading.focus();
  }

  if (document.readyState === "loading") {
    document.addEventListener("DOMContentLoaded", run);
  } else {
    run();
  }
})();
