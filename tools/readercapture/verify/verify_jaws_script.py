"""Static check of jjflexible.jss against the JAWS built-in function catalogue.

WHY THIS EXISTS
---------------
The JAWS half cannot be compile-checked here: scompile.exe is present on this
machine but running it is not permitted from this session, and running JAWS at
all would take speech away from the operator. So the risk this file addresses
is the specific one that killed five theories on 2026-08-27 in a different
guise: writing something that reads correctly, cannot be executed, and is
therefore assumed to work.

Every JAWS installation ships Scripts/enu/builtin.jsd, a machine-readable
catalogue of every built-in function with its parameter list. This checker
reads that catalogue and asserts that every function the script calls exists
and is called with a legal number of arguments, and that every constant it
names is defined in HJConst.JSH.

IT CARRIES ITS OWN POSITIVE CONTROL. Before it reports anything about the real
script, it runs itself against a fixture containing one call it MUST flag and
one it MUST NOT. If that fixture does not behave, the checker reports itself
broken and refuses to certify anything - because a checker that finds nothing
looks exactly like a script with nothing wrong.

Usage:  python verify_jaws_script.py [path\\to\\jjflexible.jss]
"""

import os
import re
import sys

KEYWORDS = {
    "if", "then", "else", "endif", "while", "endwhile", "for", "endfor",
    "foreach", "endforeach", "function", "endfunction", "script", "endscript",
    "var", "globals", "const", "let", "return", "include", "use", "optional",
    "byref", "new", "not", "and", "or", "int", "string", "void", "object",
    "handle", "collection", "variant", "float", "builtin", "self", "default",
    "case", "endcase", "switch", "endswitch", "break", "continue", "pause",
    "delay", "globalsection", "type",
}


def find_jaws_scripts_dir():
    root = r"C:\ProgramData\Freedom Scientific\JAWS"
    if not os.path.isdir(root):
        return None
    versions = sorted((d for d in os.listdir(root) if d.isdigit()), reverse=True)
    for v in versions:
        for lang in ("enu", "Enu", "ENU"):
            cand = os.path.join(root, v, "Scripts", lang)
            if os.path.isfile(os.path.join(cand, "builtin.jsd")):
                return os.path.join(root, v, "Scripts")
    return None


def parse_builtin_jsd(path):
    """name -> (required, optional). Optional is -1 when the catalogue does not say."""
    funcs = {}
    name = None
    req = 0
    opt = 0
    optional_mode = False
    with open(path, "r", encoding="utf-8", errors="replace") as fh:
        for line in fh:
            line = line.rstrip("\n")
            if line.startswith(":function "):
                if name:
                    funcs[name.lower()] = (req, opt)
                name = line[len(":function "):].strip()
                req = 0
                opt = 0
                optional_mode = False
            elif line.strip().lower() == ":optional":
                # A standalone marker: every :Param after it is optional.
                optional_mode = True
            elif line.startswith(":Param") and name:
                if optional_mode:
                    opt += 1
                else:
                    req += 1
    if name:
        funcs[name.lower()] = (req, opt)
    return funcs


def parse_script_functions(path):
    """Functions and scripts declared in a .jss / .jsm source file."""
    names = set()
    pat = re.compile(r"^\s*(?:[A-Za-z]+\s+)?(?:function|script)\s+([A-Za-z_]\w*)", re.I)
    with open(path, "r", encoding="utf-8", errors="replace") as fh:
        for line in fh:
            if line.lstrip().startswith(";"):
                continue
            m = pat.match(line)
            if m:
                names.add(m.group(1).lower())
    return names


def parse_constants(path):
    """Constants live in multi-line `const` blocks, one NAME = value per line.

    Deliberately permissive. Over-collecting here can only hide a genuinely
    undefined constant, which is the mild failure; under-collecting floods the
    report with noise, which is the failure that gets a checker ignored.
    """
    names = set()
    in_const = False
    assign = re.compile(r"^\s*([A-Za-z_]\w*)\s*=")
    stopper = re.compile(r"^\s*(globals|function|script|void|int|string|object|handle|"
                         r"collection|variant|include|use|endfunction|endscript)\b", re.I)
    with open(path, "r", encoding="utf-8", errors="replace") as fh:
        for raw in fh:
            line = raw.split(";", 1)[0]
            stripped = line.strip()
            if re.match(r"^const\b", stripped, re.I):
                in_const = True
                stripped = stripped[5:].strip()
                if not stripped:
                    continue
            if not in_const:
                continue
            if stopper.match(line):
                in_const = False
                continue
            m = assign.match(line if not line.strip().startswith("const") else "\t" + stripped)
            if m:
                names.add(m.group(1).lower())
    return names


def strip_comments_and_strings(text):
    """Blank out string literals and ; comments so the scanner cannot be fooled
    by a function name that only appears inside a message."""
    out = []
    i = 0
    n = len(text)
    in_str = False
    while i < n:
        ch = text[i]
        if in_str:
            if ch == "\\" and i + 1 < n:
                out.append("  ")
                i += 2
                continue
            if ch == '"':
                in_str = False
                out.append('"')
            else:
                out.append(" ")
            i += 1
            continue
        if ch == '"':
            in_str = True
            out.append('"')
            i += 1
            continue
        if ch == ";":
            while i < n and text[i] != "\n":
                out.append(" ")
                i += 1
            continue
        out.append(ch)
        i += 1
    return "".join(out)


def split_args(argtext):
    """Top-level comma split, respecting nesting."""
    argtext = argtext.strip()
    if not argtext:
        return []
    depth = 0
    parts = []
    cur = []
    for ch in argtext:
        if ch in "([":
            depth += 1
        elif ch in ")]":
            depth -= 1
        if ch == "," and depth == 0:
            parts.append("".join(cur))
            cur = []
        else:
            cur.append(ch)
    parts.append("".join(cur))
    return [p.strip() for p in parts if p.strip() != ""]


CALL_RE = re.compile(r"(?P<dot>\.\s*)?(?:(?P<scope>[A-Za-z_]\w*)\s*::\s*)?(?P<name>[A-Za-z_]\w*)\s*\(")


def scan_calls(clean_text):
    """Yield (scope, name, argcount, offset) for every call site."""
    for m in CALL_RE.finditer(clean_text):
        name = m.group("name")
        if name.lower() in KEYWORDS:
            continue
        if m.group("dot"):
            # A COM member call on an object variable (oFSO.OpenTextFile, and
            # so on). Not a JAWS script function, and not this checker's to
            # validate - the object's own type system owns it.
            continue
        # Balance from the opening paren to find the argument text.
        i = m.end() - 1
        depth = 0
        j = i
        while j < len(clean_text):
            if clean_text[j] == "(":
                depth += 1
            elif clean_text[j] == ")":
                depth -= 1
                if depth == 0:
                    break
            j += 1
        args = split_args(clean_text[i + 1:j])
        yield (m.group("scope"), name, len(args), m.start())


OPENERS = {"if": "endif", "while": "endwhile", "for": "endfor",
           "foreach": "endforeach", "function": "endfunction",
           "script": "endscript", "switch": "endswitch"}
CLOSERS = {v: k for k, v in OPENERS.items()}
TOKEN = re.compile(r"\b([A-Za-z]+)\b")


def check_blocks(source_text, source_name="<script>"):
    """Block balance. This is the part of compiling that can be done without a
    compiler, and an unbalanced EndIf is exactly the kind of fault that would
    otherwise only surface as a JAWS error message on a tester's machine."""
    problems = []
    clean = strip_comments_and_strings(source_text)
    stack = []
    for m in TOKEN.finditer(clean):
        word = m.group(1).lower()
        line_no = source_text.count("\n", 0, m.start()) + 1
        if word in OPENERS:
            # "int function Foo ()" opens; "endFunction" is matched below. A
            # bare "for" inside an identifier cannot reach here: \b sees to it.
            stack.append((word, line_no))
        elif word in CLOSERS:
            want = CLOSERS[word]
            if not stack:
                problems.append("%s:%d  %s with nothing open" % (source_name, line_no, word))
                continue
            opened, opened_line = stack.pop()
            if opened != want:
                problems.append(
                    "%s:%d  %s closes a %s opened at line %d"
                    % (source_name, line_no, word, opened, opened_line))
    for opened, opened_line in stack:
        problems.append("%s:%d  %s is never closed" % (source_name, opened_line, opened))
    return problems


def check(source_text, builtins, local_funcs, known_constants, source_name="<script>"):
    problems = []
    clean = strip_comments_and_strings(source_text)

    declared = set()
    for line in source_text.splitlines():
        m = re.match(r"^\s*(?:[A-Za-z]+\s+)?(?:function|script)\s+([A-Za-z_]\w*)", line, re.I)
        if m:
            declared.add(m.group(1).lower())

    for scope, name, argc, off in scan_calls(clean):
        low = name.lower()
        line_no = source_text.count("\n", 0, off) + 1
        if low in declared and scope is None:
            continue
        if low in local_funcs:
            continue
        if low in builtins:
            req, opt = builtins[low]
            if argc < req or argc > req + opt:
                problems.append(
                    "%s:%d  %s called with %d argument(s); catalogue says %d required, %d optional"
                    % (source_name, line_no, name, argc, req, opt))
            continue
        problems.append("%s:%d  unknown function %s%s"
                        % (source_name, line_no, (scope + "::") if scope else "", name))

    # Constants: bare ALL_CAPS identifiers that are not call sites.
    called_names = set(n.lower() for _, n, _, _ in scan_calls(clean))
    declared_consts = set()
    in_const = False
    for line in source_text.splitlines():
        stripped = line.strip()
        if re.match(r"^const\b", stripped, re.I):
            in_const = True
            stripped = stripped[5:]
        elif in_const and (re.match(r"^globals\b", stripped, re.I) or stripped == ""):
            in_const = False
            continue
        if in_const:
            m = re.match(r"^([A-Za-z_]\w*)\s*=", stripped)
            if m:
                declared_consts.add(m.group(1).lower())
    for m in re.finditer(r"\b([A-Z][A-Z0-9_]{2,})\b", clean):
        low = m.group(1).lower()
        if low in called_names or low in declared_consts or low in known_constants:
            continue
        if low in KEYWORDS:
            continue
        line_no = source_text.count("\n", 0, m.start()) + 1
        problems.append("%s:%d  unknown constant %s" % (source_name, line_no, m.group(1)))
    return problems


GOOD_FIXTURE = """
void function FixtureGood ()
CopyToClipboard ("hello")
endFunction
"""

BAD_FIXTURE = """
void function FixtureBad ()
ThisFunctionCertainlyDoesNotExist ("hello")
CopyToClipboard ()
SayMessage (OT_NO_SUCH_OUTPUT_TYPE_AT_ALL, "hello")
endFunction
"""


UNBALANCED_FIXTURE = """
void function FixtureUnbalanced ()
if (TRUE) then
CopyToClipboard ("x")
endFunction
"""


def self_check(builtins, local_funcs, known_constants):
    """The checker's own positive control. A checker that finds nothing looks
    exactly like a clean script, so prove it can find something first."""
    failures = []
    good = check(GOOD_FIXTURE, builtins, local_funcs, known_constants, "good-fixture")
    if good:
        failures.append("checker flagged a known-good fixture: %s" % good)
    bad = check(BAD_FIXTURE, builtins, local_funcs, known_constants, "bad-fixture")
    if not any("ThisFunctionCertainlyDoesNotExist" in p for p in bad):
        failures.append("checker did NOT flag an invented function name")
    if not any("CopyToClipboard called with 0" in p for p in bad):
        failures.append("checker did NOT flag a wrong argument count")
    if not any("OT_NO_SUCH_OUTPUT_TYPE_AT_ALL" in p for p in bad):
        failures.append("checker did NOT flag an invented constant")
    if check_blocks(GOOD_FIXTURE, "good-fixture"):
        failures.append("block checker flagged a balanced fixture")
    if not check_blocks(UNBALANCED_FIXTURE, "unbalanced-fixture"):
        failures.append("block checker did NOT flag an unclosed If")
    return failures


def main(argv):
    here = os.path.dirname(os.path.abspath(__file__))
    target = argv[1] if len(argv) > 1 else os.path.join(here, "..", "jaws", "jjflexible.jss")
    target = os.path.abspath(target)

    scripts_dir = find_jaws_scripts_dir()
    if not scripts_dir:
        print("SKIP: no JAWS installation found, so the built-in catalogue is "
              "unavailable and nothing can be verified. This is not a pass.")
        return 2

    lang_dir = None
    for lang in ("enu", "Enu", "ENU"):
        cand = os.path.join(scripts_dir, lang)
        if os.path.isfile(os.path.join(cand, "builtin.jsd")):
            lang_dir = cand
            break
    builtins = parse_builtin_jsd(os.path.join(lang_dir, "builtin.jsd"))

    local_funcs = set()
    for fname in ("Default.JSS", "common.jsm", "HJGlobal.JSH"):
        p = os.path.join(scripts_dir, fname)
        if os.path.isfile(p):
            local_funcs |= parse_script_functions(p)

    known_constants = set()
    for fname in ("HJConst.JSH", "common.jsm", "HJGlobal.JSH"):
        p = os.path.join(scripts_dir, fname)
        if os.path.isfile(p):
            known_constants |= parse_constants(p)

    print("Catalogue: %d built-in functions, %d default-script functions, %d constants."
          % (len(builtins), len(local_funcs), len(known_constants)))

    failures = self_check(builtins, local_funcs, known_constants)
    if failures:
        print("POSITIVE CONTROL FAILED for the checker itself:")
        for f in failures:
            print("  " + f)
        print("Refusing to certify %s. Fix the checker first." % target)
        return 3
    print("Positive control passed: the checker flags an invented name, a wrong "
          "argument count, an invented constant and an unclosed block, and "
          "leaves a valid, balanced fixture alone.")

    with open(target, "r", encoding="utf-8", errors="replace") as fh:
        text = fh.read()
    problems = check_blocks(text, os.path.basename(target))
    problems += check(text, builtins, local_funcs, known_constants, os.path.basename(target))
    if problems:
        print("\n%d problem(s) in %s:" % (len(problems), target))
        for p in problems:
            print("  " + p)
        return 1
    print("\nBlocks balance, and there are no unknown functions, argument-count "
          "errors or unknown constants in %s." % os.path.basename(target))
    print("This is a NAME, ARITY AND BLOCK check only. It does not prove the script "
          "compiles, and it cannot prove the BrailleString override actually "
          "intercepts RunFunction. Only running JAWS proves that.")
    return 0


if __name__ == "__main__":
    sys.exit(main(sys.argv))
