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
    i = -1
    for _ in range(nth + 1):
        i = text.index(needle, i + 1)
    i += off
    line = text.count("\n", 0, i)
    return {"line": line, "character": i - (text.rfind("\n", 0, i) + 1)}

try:
    r = request("initialize", {"processId": None, "rootUri": uri(proj), "capabilities": {}})
    caps = r.get("result", {}).get("capabilities", {})
    check("initialize", caps.get("definitionProvider") and caps.get("hoverProvider") and caps.get("textDocumentSync", {}).get("change") == 1, r)
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
