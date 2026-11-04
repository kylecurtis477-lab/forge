#!/usr/bin/env python3
"""Scripted LSP session against `forge lsp` (Phase 7.5). Prints PASS/FAIL lines; exit 1 on failure."""
import json, os, subprocess, sys, shutil, tempfile, select, time

D = os.path.dirname(os.path.abspath(__file__))
ROOT = os.path.dirname(os.path.dirname(D))
tmp = tempfile.mkdtemp()
proj = os.path.join(tmp, "proj")
shutil.copytree(os.path.join(D, "proj"), proj)
MAIN = os.path.join(proj, "main.forge")
UTIL = os.path.join(proj, "util.forge")
uri = lambda p: "file://" + p
fails = 0

srv = subprocess.Popen([os.path.join(ROOT, "forge"), "lsp"], stdin=subprocess.PIPE, stdout=subprocess.PIPE)
buf = b""
nid = [0]

def send(obj):
    b = json.dumps(obj).encode()
    srv.stdin.write(b"Content-Length: %d\r\n\r\n" % len(b) + b)
    srv.stdin.flush()

def recv(timeout=20):
    global buf
    end = time.time() + timeout
    while True:
        i = buf.find(b"\r\n\r\n")
        if i >= 0:
            hdr = buf[:i].decode()
            n = int([l for l in hdr.split("\r\n") if l.lower().startswith("content-length")][0].split(":")[1])
            if len(buf) >= i + 4 + n:
                body = buf[i + 4:i + 4 + n]
                buf = buf[i + 4 + n:]
                return json.loads(body)
        left = end - time.time()
        if left <= 0 or not select.select([srv.stdout], [], [], left)[0]:
            raise TimeoutError("no message from server")
        chunk = os.read(srv.stdout.fileno(), 65536)
        if not chunk:
            raise EOFError("server closed stdout")
        buf += chunk

def request(method, params):
    nid[0] += 1
    send({"jsonrpc": "2.0", "id": nid[0], "method": method, "params": params})
    while True:
        m = recv()
        if m.get("id") == nid[0]:
            return m

def notify(method, params):
    send({"jsonrpc": "2.0", "method": method, "params": params})

def diags_for(u):
    while True:
        m = recv()
        if m.get("method") == "textDocument/publishDiagnostics" and m["params"]["uri"] == u:
            return m["params"]["diagnostics"]

def check(name, cond, detail=""):
    global fails
    if cond:
        print("PASS lsp/" + name)
    else:
        fails += 1
        print("FAIL lsp/" + name + " " + str(detail)[:400])

def pos_of(text, needle, nth=0, off=0):
    # LSP position (UTF-16 code units) of the nth occurrence of needle, plus off characters
    i = -1
    for _ in range(nth + 1):
        i = text.index(needle, i + 1)
    i += off
    line = text.count("\n", 0, i)
    pre = text[text.rfind("\n", 0, i) + 1:i]
    return {"line": line, "character": len(pre.encode("utf-16-le")) // 2}

def rng(text, needle, nth=0, off=0, n=None):
    a = pos_of(text, needle, nth, off)
    return {"start": a, "end": {"line": a["line"], "character": a["character"] + (n if n is not None else len(needle) - off)}}

def stats():
    return request("forge/stats", None)["result"]["compiles"]

try:
    r = request("initialize", {"processId": None, "rootUri": uri(proj), "capabilities": {}})
    caps = r.get("result", {}).get("capabilities", {})
    check("initialize", caps.get("definitionProvider") and caps.get("hoverProvider") and caps.get("textDocumentSync", {}).get("change") == 2 and caps.get("referencesProvider") and caps.get("renameProvider") and "." in caps.get("completionProvider", {}).get("triggerCharacters", []), r)
    notify("initialized", {})
    text = open(MAIN).read()
    notify("textDocument/didOpen", {"textDocument": {"uri": uri(MAIN), "languageId": "forge", "version": 1, "text": text}})
    d = diags_for(uri(MAIN))
    check("clean-file-no-diagnostics", d == [], d)

    # a type error in the unsaved buffer
    bad = text.replace("total = a + b", "total: int = \"oops\"")
    notify("textDocument/didChange", {"textDocument": {"uri": uri(MAIN), "version": 2}, "contentChanges": [{"text": bad}]})
    d = diags_for(uri(MAIN))
    want = pos_of(bad, "\"oops\"")
    check("type-error-diagnostic", len(d) == 1 and d[0]["code"] == "E0302" and d[0]["range"]["start"] == want and d[0]["severity"] == 1 and "incompatible types" in d[0]["message"], d)
    check("no-temp-files-left", sorted(os.listdir(proj)) == ["main.forge", "util.forge"], os.listdir(proj))
    check("disk-file-untouched", open(MAIN).read() == text)

    # a syntax error, then an undefined name
    bad = text.replace("def add(a: int, b: int)", "def add(: int, b: int)")
    notify("textDocument/didChange", {"textDocument": {"uri": uri(MAIN), "version": 3}, "contentChanges": [{"text": bad}]})
    d = diags_for(uri(MAIN))
    check("syntax-error-diagnostic", len(d) == 1 and d[0]["code"] == "E0105" and d[0]["range"]["start"] == pos_of(bad, ": int, b"), d)
    bad = text.replace("print(add(i, 1))", "print(addd(i, 1))")
    notify("textDocument/didChange", {"textDocument": {"uri": uri(MAIN), "version": 4}, "contentChanges": [{"text": bad}]})
    d = diags_for(uri(MAIN))
    check("undefined-name-diagnostic", len(d) == 1 and d[0]["code"] == "E0201" and d[0]["range"]["start"]["line"] == pos_of(bad, "addd")["line"], d)
    notify("textDocument/didChange", {"textDocument": {"uri": uri(MAIN), "version": 5}, "contentChanges": [{"text": text}]})
    d = diags_for(uri(MAIN))
    check("fixed-clears-diagnostics", d == [], d)

    def defn(needle, nth=0, off=1):
        return request("textDocument/definition", {"textDocument": {"uri": uri(MAIN)}, "position": pos_of(text, needle, nth, off)}).get("result")

    r = defn("add(i", 0, 1)
    check("definition-function", r and r["uri"] == uri(MAIN) and r["range"]["start"] == pos_of(text, "add(a"), r)
    r = defn("total", 1, 2)
    check("definition-local-variable", r and r["range"]["start"] == pos_of(text, "total ="), r)
    r = defn("a + b", 0, 0)
    check("definition-parameter", r and r["range"]["start"] == pos_of(text, "a: int"), r)
    r = defn("i, 1", 0, 0)
    check("definition-loop-variable", r and r["range"]["start"] == pos_of(text, "i in"), r)
    ut = open(UTIL).read()
    r = defn("shout(\"hi", 0, 2)
    check("definition-from-import", r and r["uri"] == uri(UTIL) and r["range"]["start"] == pos_of(ut, "shout("), r)
    r = defn("shout(\"x", 0, 2)
    check("definition-module-attribute", r and r["uri"] == uri(UTIL) and r["range"]["start"] == pos_of(ut, "shout("), r)
    r = defn("Counter()", 0, 3)
    check("definition-class", r and r["uri"] == uri(UTIL) and r["range"]["start"] == pos_of(ut, "Counter:"), r)
    r = defn("print(add", 0, 2)
    check("definition-builtin-none", r is None, r)

    def hover(needle, nth=0, off=1):
        r = request("textDocument/hover", {"textDocument": {"uri": uri(MAIN)}, "position": pos_of(text, needle, nth, off)}).get("result")
        return r and r["contents"]["value"]

    h = hover("add(i")
    check("hover-function-signature-doc", h and "def add(a: int, b: int) -> int" in h and "Add two ints." in h, h)
    h = hover("shout(\"hi", 0, 2)
    check("hover-imported-function", h and "def shout(s: str) -> str" in h and "Upper-case s" in h and "util.forge:1" in h, h)
    h = hover("a + b", 0, 0)
    check("hover-parameter-type", h and "(parameter) a: int" in h, h)

    # attributes: self.x / self.m inside a class, obj.m on an annotated parameter or C() variable
    r = defn("inc(2", 0, 1)
    check("definition-attr-constructed-var", r and r["uri"] == uri(UTIL) and r["range"]["start"] == pos_of(ut, "inc("), r)
    r = defn("inc(1", 0, 1)
    check("definition-attr-annotated-param", r and r["uri"] == uri(UTIL) and r["range"]["start"] == pos_of(ut, "inc("), r)
    h = hover("inc(2", 0, 1)
    check("hover-method", h and "def inc(self, k: int) -> int" in h and "Add k and return" in h, h)
    notify("textDocument/didOpen", {"textDocument": {"uri": uri(UTIL), "languageId": "forge", "version": 1, "text": ut}})
    d = diags_for(uri(UTIL))
    r = request("textDocument/definition", {"textDocument": {"uri": uri(UTIL)}, "position": pos_of(ut, "self.n + k", 0, 5)}).get("result")
    check("definition-self-attribute", r and r["uri"] == uri(UTIL) and r["range"]["start"] == pos_of(ut, "n = 0"), r)

    # UTF-16 positions: a name after non-ASCII text (é = 1 unit / 2 bytes, emoji = 2 units / 4 bytes)
    r = defn("add(1, 2", 0, 1)
    check("utf16-definition-after-emoji", r and r["range"]["start"] == pos_of(text, "add(a"), r)
    r = request("textDocument/references", {"textDocument": {"uri": uri(MAIN)}, "position": pos_of(text, "greet,", 0, 1), "context": {"includeDeclaration": True}}).get("result")
    check("utf16-reference-ranges", r and sorted(x["range"]["start"]["character"] for x in r) == [0, pos_of(text, "greet,")["character"]] and all(x["range"]["end"]["character"] - x["range"]["start"]["character"] == 5 for x in r), r)

    # references and rename across modules
    r = request("textDocument/references", {"textDocument": {"uri": uri(MAIN)}, "position": pos_of(text, "add(i", 0, 1), "context": {"includeDeclaration": True}}).get("result")
    got = sorted((x["uri"], x["range"]["start"]["line"], x["range"]["start"]["character"]) for x in (r or []))
    want = sorted((uri(MAIN), p["line"], p["character"]) for p in [pos_of(text, "add(a"), pos_of(text, "add(i"), pos_of(text, "add(1, 2")])
    check("references-function", got == want, r)
    r = request("textDocument/references", {"textDocument": {"uri": uri(MAIN)}, "position": pos_of(text, "add(i", 0, 1), "context": {"includeDeclaration": False}}).get("result")
    check("references-exclude-declaration", r is not None and len(r) == 2, r)
    r = request("textDocument/references", {"textDocument": {"uri": uri(MAIN)}, "position": pos_of(text, "inc(2", 0, 1), "context": {"includeDeclaration": True}}).get("result")
    got = sorted((x["uri"], x["range"]["start"]["line"]) for x in (r or []))
    want = sorted([(uri(UTIL), pos_of(ut, "inc(")["line"]), (uri(MAIN), pos_of(text, "inc(2")["line"]), (uri(MAIN), pos_of(text, "inc(1")["line"])])
    check("references-method-cross-module", got == want, r)
    r = request("textDocument/references", {"textDocument": {"uri": uri(MAIN)}, "position": pos_of(text, "a + b", 0, 0), "context": {"includeDeclaration": True}}).get("result")
    check("references-parameter-scoped", r and sorted(x["range"]["start"]["line"] for x in r) == [pos_of(text, "a: int")["line"], pos_of(text, "a + b")["line"]], r)
    r = request("textDocument/rename", {"textDocument": {"uri": uri(MAIN)}, "position": pos_of(text, "shout(\"hi", 0, 2), "newName": "yell"}).get("result")
    ch = (r or {}).get("changes", {})
    check("rename-cross-module", len(ch.get(uri(MAIN), [])) == 3 and len(ch.get(uri(UTIL), [])) == 1 and all(e["newText"] == "yell" for v in ch.values() for e in v) and {"range": rng(ut, "shout", 0, 0), "newText": "yell"} in ch.get(uri(UTIL), []), r)
    r = request("textDocument/rename", {"textDocument": {"uri": uri(MAIN)}, "position": pos_of(text, "add(i", 0, 1), "newName": "9bad"})
    check("rename-invalid-name-error", r.get("error", {}).get("code") == -32602, r)

    # completion
    def comp(needle, off):
        r = request("textDocument/completion", {"textDocument": {"uri": uri(MAIN)}, "position": pos_of(text, needle, 0, off)}).get("result")
        return [i["label"] for i in (r or {}).get("items", [])]
    c = comp("util.shout(", 5)
    check("completion-module-members", "shout" in c and "Counter" in c and "add" not in c, c)
    c = comp("inc(2", 0)
    check("completion-instance-members", "inc" in c and "n" in c and "__init__" in c, c)
    c = comp("total = a", 2)
    check("completion-scope-prefix", "total" in c and "to" not in c and all(x.startswith("to") for x in c), c)
    c = comp("x.inc(1", 0)
    check("completion-params-and-globals", "x" in c and "add" in c and "bump" in c and "util" in c and "print" in c, c)

    # incremental sync: range edits; incremental re-check: unchanged text is not recompiled
    n0 = stats()
    notify("textDocument/didChange", {"textDocument": {"uri": uri(MAIN), "version": 6}, "contentChanges": [{"range": rng(text, "add(i, 1)", 0, 0, 3), "text": "addd"}]})
    d = diags_for(uri(MAIN))
    check("incremental-edit-diagnostic", len(d) == 1 and d[0]["code"] == "E0201" and d[0]["range"]["start"]["line"] == pos_of(text, "add(i")["line"], d)
    t2 = text.replace("print(add(i, 1))", "print(addd(i, 1))")
    e = pos_of(t2, "greet,")
    notify("textDocument/didChange", {"textDocument": {"uri": uri(MAIN), "version": 7}, "contentChanges": [{"range": {"start": e, "end": {"line": e["line"], "character": e["character"] + 5}}, "text": "greet"}, {"range": rng(t2, "addd(i", 0, 0, 4), "text": "add"}]})
    d = diags_for(uri(MAIN))
    check("incremental-multi-edit-utf16", d == [], d)
    n1 = stats()
    check("recheck-once-per-changed-version", n1 - n0 == 2, (n0, n1))
    notify("textDocument/didChange", {"textDocument": {"uri": uri(MAIN), "version": 8}, "contentChanges": [{"range": rng(text, "add(i", 0, 0, 3), "text": "add"}]})
    d = diags_for(uri(MAIN))
    check("recheck-skips-identical-text", d == [] and stats() == n1, (d, stats()))
    notify("textDocument/didSave", {"textDocument": {"uri": uri(MAIN)}})
    d = diags_for(uri(MAIN))
    check("save-unchanged-no-recompile", d == [] and stats() == n1, stats())
    r = defn("shout(\"x", 0, 2)
    r = defn("Counter()", 0, 3)
    check("module-ast-cached", stats() == n1, stats())
    # saving an imported module rechecks its open importers
    with open(UTIL, "w") as f:
        f.write(ut.replace("def shout(", "def holler("))
    notify("textDocument/didSave", {"textDocument": {"uri": uri(UTIL)}, "text": ut.replace("def shout(", "def holler(")})
    seen = {}
    while len(seen) < 2:
        m = recv()
        if m.get("method") == "textDocument/publishDiagnostics":
            seen[m["params"]["uri"]] = m["params"]["diagnostics"]
    check("dependent-rechecked-on-save", seen.get(uri(UTIL)) == [] and len(seen.get(uri(MAIN), [])) >= 1 and stats() == n1 + 2, (seen, stats()))
    with open(UTIL, "w") as f:
        f.write(ut)

    r = request("textDocument/formatting", {"textDocument": {"uri": uri(MAIN)}, "options": {}})
    check("unknown-method-error", r.get("error", {}).get("code") == -32601, r)
    r = request("shutdown", None)
    check("shutdown", "result" in r and r["result"] is None, r)
    notify("exit", None)
    rc = srv.wait(timeout=10)
    check("exit-code-0", rc == 0, rc)
except Exception as e:
    check("session", False, repr(e))
    srv.kill()
shutil.rmtree(tmp)
sys.exit(1 if fails else 0)
