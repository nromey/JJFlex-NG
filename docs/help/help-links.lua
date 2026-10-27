-- help-links.lua -- the JJ Flexible help build's one Lua filter (#826).
--
-- Started from the research prototype (JJFlex-private planning,
-- for-claude/help-toolchain-probe/q1/help-links.lua, tested against every
-- planted defect listed below) and extended for the framework.
--
-- Run by build-help.ps1, once per page:
--   pandoc <md>/Folder/Page.md -f markdown -t html
--          -M help_root=<md> -M help_sidecar=<file.json>
--          [-M help_planned=<Name.md;Name.md;...>]
--          --lua-filter help-links.lua -o <body.htm>
--
-- What it does, per page:
--   * walks help_root, builds a map  lower(bare filename) -> real path, and
--     FAILS if two files share a bare name (any folder, any case);
--   * rewrites [x](Name.md) and [x](Name.md#anchor) to the RELATIVE .htm path
--     from this page's folder to the target's, same folder shape as md/, so
--     one body serves both the compiled help (pages/) and the HTML help;
--   * fails on: unknown name (and says so when the name is a page the outline
--     plans but nobody has written yet), wrong-case name, Folder/Name.md in
--     the wrong folder, a missing #anchor in the target, a missing in-page
--     #anchor, any .htm/.html link, any other relative target;
--   * passes http(s): and mailto: untouched;
--   * takes the page title from its single level-one heading and fails on
--     zero or more than one (dropping --metadata title alone is NOT enough:
--     pandoc then silently titles the page with its filename stem);
--   * writes a sidecar (JSON) the generator reads: title, keywords from the
--     page's leading YAML block, heading ids, the text of each section for
--     search, and every heading id that would break as soon as a heading is
--     added above it (an all-numeric heading's "section", or a "-N" suffix
--     pandoc added to tell two equal headings apart). The build, not this
--     filter, decides whether those fail, because it keeps the exception list.
--
-- Every failure is error("help-links: ..."). Pandoc exits 83 and appends a
-- Lua traceback; build-help.ps1 prints only the help-links: line, or an
-- operator would hear six lines of Lua paths per mistake.

local system = pandoc.system
local P = pandoc.path

local function fail(msg) error("help-links: " .. msg, 0) end

-- ---------- path helpers ----------
local function lower(s) return string.lower(s) end

local function absolute(p)
  p = P.normalize(p)
  if P.is_relative(p) then p = P.normalize(P.join({ system.get_working_directory(), p })) end
  return p
end

-- Relative path from directory `from_dir` to file `to_rel`, both relative to
-- md/, with forward slashes. pandoc.path.make_relative is lexical and will not
-- emit "..", so do it by hand.
local function relative_href(from_dir, to_rel)
  local from = {}
  if from_dir ~= "" and from_dir ~= "." then
    for part in from_dir:gmatch("[^/\\]+") do from[#from + 1] = part end
  end
  local to = {}
  for part in to_rel:gmatch("[^/\\]+") do to[#to + 1] = part end
  local i = 1
  while i <= #from and i < #to and lower(from[i]) == lower(to[i]) do i = i + 1 end
  local out = {}
  for _ = i, #from do out[#out + 1] = ".." end
  for j = i, #to do out[#out + 1] = to[j] end
  return table.concat(out, "/")
end

-- ---------- walk md/ ----------
local function is_dir(p)
  local ok = pcall(system.list_directory, p)
  return ok
end

local function walk(root, rel, into)
  local dir = rel == "" and root or P.join({ root, rel })
  for _, name in ipairs(system.list_directory(dir)) do
    local child_rel = rel == "" and name or (rel .. "/" .. name)
    local full = P.join({ root, child_rel })
    if lower(name):match("%.md$") then
      into[#into + 1] = { name = name, rel = child_rel, dir = rel }
    elseif is_dir(full) then
      walk(root, child_rel, into)
    end
  end
end

local function build_map(root)
  local files = {}
  walk(root, "", files)
  local map, clashes = {}, {}
  for _, f in ipairs(files) do
    local key = lower(f.name)
    if map[key] then
      clashes[#clashes + 1] = string.format("%s and %s", map[key].rel, f.rel)
    else
      map[key] = f
    end
  end
  if #clashes > 0 then
    fail("bare filenames must be unique across md/ (case-insensitive); clashes: "
      .. table.concat(clashes, "; "))
  end
  return map, files
end

-- small edit distance for "nearest names"
local function lev(a, b)
  local la, lb = #a, #b
  local d = {}
  for i = 0, la do d[i] = { [0] = i } end
  for j = 0, lb do d[0][j] = j end
  for i = 1, la do
    for j = 1, lb do
      local c = (a:sub(i, i) == b:sub(j, j)) and 0 or 1
      d[i][j] = math.min(d[i - 1][j] + 1, d[i][j - 1] + 1, d[i - 1][j - 1] + c)
    end
  end
  return d[la][lb]
end

local function nearest(map, wanted, n)
  local scored = {}
  for key, f in pairs(map) do scored[#scored + 1] = { d = lev(lower(wanted), key), f = f } end
  table.sort(scored, function(x, y)
    if x.d ~= y.d then return x.d < y.d end
    return x.f.rel < y.f.rel
  end)
  local out = {}
  for i = 1, math.min(n, #scored) do out[#out + 1] = scored[i].f.rel end
  return table.concat(out, ", ")
end

-- ---------- heading ids of another page (cached) ----------
local id_cache = {}
local function heading_ids(root, f)
  if id_cache[f.rel] then return id_cache[f.rel] end
  local src = system.read_file(P.join({ root, f.rel }))
  local doc = pandoc.read(src, "markdown")
  local ids = {}
  doc:walk({ Header = function(h) ids[h.identifier] = true end })
  id_cache[f.rel] = ids
  return ids
end

-- The id pandoc would give this heading's text if it were the only heading on
-- the page, i.e. before any "-1", "-2" suffix was added to keep ids unique.
local function base_id(h)
  local one = pandoc.Pandoc({ pandoc.Header(1, h.content) })
  local back = pandoc.read(pandoc.write(one, "markdown"), "markdown")
  local first = back.blocks[1]
  if first and first.t == "Header" then return first.identifier end
  return ""
end

local function squash(s)
  return (s:gsub("%s+", " "):gsub("^ ", ""):gsub(" $", ""))
end

local plain_opts = { wrap_text = "none" }
local function block_text(b)
  return squash(pandoc.write(pandoc.Pandoc({ b }), "plain", plain_opts))
end

-- ---------- main ----------
function Pandoc(doc)
  local meta = doc.meta
  if not meta.help_root then fail("pass -M help_root=<md root>") end
  local root = absolute(pandoc.utils.stringify(meta.help_root))

  local input = PANDOC_STATE.input_files[1]
  if not input or input == "-" then fail("the page must be given as a file, not stdin") end
  input = absolute(input)
  if lower(input:sub(1, #root)) ~= lower(root) then
    fail(string.format("input %s is not under help_root %s", input, root))
  end
  local self_rel = input:sub(#root + 2):gsub("\\", "/")
  local self_dir = P.directory(self_rel)
  if self_dir == "." then self_dir = "" end

  local planned = {}
  if meta.help_planned then
    for name in pandoc.utils.stringify(meta.help_planned):gmatch("[^;]+") do
      planned[lower(name)] = name
    end
  end

  local map = build_map(root)

  -- the page's own on-disk name must match what was passed (case included)
  local self_entry = map[lower(P.filename(self_rel))]
  if not self_entry or self_entry.rel ~= self_rel then
    fail(string.format("input path %s does not match the on-disk path %s", self_rel,
      self_entry and self_entry.rel or "(none)"))
  end

  -- own headings: ids, and exactly one h1
  local self_ids, h1s = {}, {}
  doc:walk({ Header = function(h)
    self_ids[h.identifier] = true
    if h.level == 1 then h1s[#h1s + 1] = pandoc.utils.stringify(h) end
  end })
  if #h1s ~= 1 then
    fail(string.format("%s must have exactly one level-one heading, found %d", self_rel, #h1s))
  end
  meta.pagetitle = pandoc.MetaString(h1s[1])
  meta.title = nil -- never let a title block render

  local function resolve(el)
    local t = el.target
    if t:match("^https?://") or t:match("^mailto:") then return el end
    local page, anchor = t:match("^([^#]*)#?(.*)$")
    if page == "" then
      if anchor == "" then fail(string.format("%s: empty link target", self_rel)) end
      if not self_ids[anchor] then
        fail(string.format("%s: no heading with id #%s on this page", self_rel, anchor))
      end
      return el
    end
    if lower(page):match("%.html?$") then
      fail(string.format("%s: link %s names build output; link the .md source", self_rel, t))
    end
    if not lower(page):match("%.md$") then
      fail(string.format("%s: link %s is not a .md page, an in-page #anchor, or an http/mailto URL", self_rel, t))
    end
    local entry
    if page:find("[/\\]") then
      -- folder form: accepted only when exactly right
      local norm = page:gsub("\\", "/")
      local cand = map[lower(P.filename(norm))]
      if not cand or cand.rel ~= norm then
        fail(string.format("%s: link %s names a folder; the file is %s -- link the bare name %s",
          self_rel, t, cand and cand.rel or "nowhere", cand and cand.name or ""))
      end
      entry = cand
    else
      entry = map[lower(page)]
      if not entry then
        if planned[lower(page)] then
          fail(string.format("%s: link %s names a page the outline plans but nobody has written yet",
            self_rel, t))
        end
        fail(string.format("%s: link %s names no page under md/; nearest: %s",
          self_rel, t, nearest(map, page, 3)))
      end
      if entry.name ~= page then
        fail(string.format("%s: link %s has the wrong case; the file is %s", self_rel, t, entry.rel))
      end
    end
    if anchor ~= "" then
      local ids = heading_ids(root, entry)
      if not ids[anchor] then
        local have = {}
        for k in pairs(ids) do have[#have + 1] = k end
        table.sort(have)
        fail(string.format("%s: link %s: %s has no heading with id #%s; it has: %s",
          self_rel, t, entry.rel, anchor, table.concat(have, " ")))
      end
    end
    local href = relative_href(self_dir, (entry.rel:gsub("%.md$", ".htm")))
    el.target = href .. (anchor ~= "" and ("#" .. anchor) or "")
    return el
  end

  -- Images are refused too: no page links one today, and a relative image
  -- would need copying into both outputs. Revisit the rule when one is wanted.
  local function no_images(img)
    if not img.src:match("^https?://") then
      fail(string.format("%s: image %s is not supported in the help yet", self_rel, img.src))
    end
    return img
  end

  -- What's New is the changelog, imported verbatim at build time; it is the
  -- user record, written for GitHub as well as for the help, and its words are
  -- not the build's to change. So on that one page (-M help_imported=true) a
  -- link the help cannot follow keeps its words and loses the link, and the
  -- build prints a notice naming it, instead of failing. Every other page fails.
  local imported = meta.help_imported ~= nil and pandoc.utils.stringify(meta.help_imported) == "true"
  local function resolve_or_unlink(el)
    if not imported then return resolve(el) end
    local ok, res = pcall(resolve, el)
    if ok then return res end
    local why = tostring(res):gsub("^help%-links: ", "")
    io.stderr:write("help-links: notice: " .. why .. "; What's New keeps the words without the link\n")
    return el.content
  end

  doc = doc:walk({ Link = resolve_or_unlink, Image = no_images })

  if meta.help_sidecar then
    -- Heading ids that break the moment a heading is added above them.
    -- Explicit {#ids} are the author's own choice and are never reported.
    -- What's New is exempt: a changelog repeats "Fixes" and "Under the hood"
    -- in every release by nature, its releases carry explicit ids, and its
    -- words are not the build's to change.
    local explicit = {}
    local raw = pandoc.read(system.read_file(input), "markdown-auto_identifiers")
    raw:walk({ Header = function(h)
      if h.identifier ~= "" then explicit[h.identifier] = true end
    end })

    local suspect = {}
    local sections = {}
    local current = nil
    for _, b in ipairs(doc.blocks) do
      if b.t == "Header" then
        local id = b.identifier
        if not explicit[id] and not imported then
          if id:match("^section%-?%d*$") then
            suspect[#suspect + 1] = { id = id, why = "the heading has no letters, so its id says nothing" }
          else
            local base = base_id(b)
            if base ~= id then
              suspect[#suspect + 1] = { id = id,
                why = "pandoc numbered it to tell it from an earlier heading with the same id (" .. base .. ")" }
            end
          end
        end
        current = { id = id, heading = pandoc.utils.stringify(b), level = b.level, text = {} }
        sections[#sections + 1] = current
      elseif current then
        local t = block_text(b)
        if t ~= "" then current.text[#current.text + 1] = t end
      end
    end
    for _, s in ipairs(sections) do s.text = table.concat(s.text, " ") end

    local ids = {}
    for k in pairs(self_ids) do ids[#ids + 1] = k end
    table.sort(ids)
    local kw = {}
    if meta.keywords then
      if pandoc.utils.type(meta.keywords) ~= "List" then
        fail(string.format("%s: keywords must be a YAML list, one keyword per line", self_rel))
      end
      for _, k in ipairs(meta.keywords) do kw[#kw + 1] = pandoc.utils.stringify(k) end
    end
    local side = {
      page = self_rel,
      title = h1s[1],
      keywords = kw,
      ids = ids,
      suspect_ids = suspect,
      sections = sections,
    }
    system.write_file(pandoc.utils.stringify(meta.help_sidecar), pandoc.json.encode(side))
  end

  return pandoc.Pandoc(doc.blocks, meta)
end
