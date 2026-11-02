#!/usr/bin/env python3
"""Grammar-based fuzzer for docs/GRAMMAR.md.

Valid programs are generated from the grammar; each must parse (--emit=ast-json), and its
pretty-printed form must parse back to the same AST.  Invalid programs are a valid program
with one statement broken in a way the grammar forbids (unbalanced bracket, dangling
operator, missing ':' in a header, doubled '=', a missing block, a bare keyword); each must
be rejected with a parse diagnostic (E0100-E0105) and never crash or be accepted.
Usage: grammar_fuzz.py [--seed N] [--n COUNT]   (COUNT programs, split valid/invalid)
"""
import json, os, random, re, subprocess, sys

FORGEC = "build/forgec"
NAMES = ["a", "b", "x", "y", "foo", "bar_2", "_t", "item"]
BIN = ["+", "-", "*", "/", "//", "%", "|", "^", "&", "<<", ">>", "**"]
CMP = ["<", ">", "==", "!=", "<=", ">=", "in", "not in", "is", "is not"]
AUG = ["+=", "-=", "*=", "/=", "//=", "%=", "|=", "&=", "^=", "<<=", ">>=", "**="]

class Gen:
    def __init__(self, rnd):
        self.r = rnd
    def name(self):
        return self.r.choice(NAMES)
    def atom(self, d):
        r = self.r.randrange(14 if d < 3 else 5)
        if r == 0: return self.name()
        if r == 1: return str(self.r.randrange(0, 100000))
        if r == 2: return self.r.choice(['"s"', "'t'", '"a b"', '""', "0x1f", "1.5", "2e3"])
        if r == 3: return self.r.choice(["True", "False", "None"])
        if r == 4: return self.name()
        if r == 5: return "(" + self.test(d + 1) + ")"
        if r == 6: return "[" + ", ".join(self.test(d + 1) for _ in range(self.r.randrange(4))) + "]"
        if r == 7: return "(" + ", ".join(self.test(d + 1) for _ in range(self.r.randrange(2, 4))) + ")"
        if r == 8: return "{" + ", ".join(self.test(d + 1) + ": " + self.test(d + 1) for _ in range(self.r.randrange(3))) + "}"
        if r == 9: return "[" + self.test(d + 1) + " for " + self.name() + " in " + self.orx(d + 1) + (" if " + self.orx(d + 1) if self.r.random() < .5 else "") + "]"
        if r == 10: return "{" + self.test(d + 1) + ", " + self.test(d + 1) + "}"
        if r == 11: return "(" + self.test(d + 1) + ",)"
        if r == 12: return "{" + self.name() + ": " + self.test(d + 1) + " for " + self.name() + " in " + self.orx(d + 1) + "}"
        return "()"
    def primary(self, d):
        e = self.atom(d)
        if e[0].isdigit():
            e = "(" + e + ")"  # 1.x lexes as a float, so a number takes trailers only in parentheses
        for _ in range(self.r.randrange(3) if d < 3 else 0):
            r = self.r.randrange(4)
            if r == 0: e += "." + self.name()
            elif r == 1: e += "(" + ", ".join(self.arg(d + 1) for _ in range(self.r.randrange(3))) + ")"
            elif r == 2: e += "[" + self.test(d + 1) + "]"
            else: e += "[" + self.r.choice(["", self.test(d + 1)]) + ":" + self.r.choice(["", self.test(d + 1)]) + "]"
        return e
    def arg(self, d):
        r = self.r.randrange(5)
        if r == 0: return self.name() + "=" + self.test(d)
        if r == 1: return "*" + self.test(d)
        return self.test(d)
    def factor(self, d):
        if self.r.random() < .15: return self.r.choice(["-", "+", "~"]) + self.factor(d + 1)
        return self.primary(d)
    def orx(self, d):
        e = self.factor(d)
        for _ in range(self.r.randrange(3) if d < 4 else 0):
            e += " " + self.r.choice(BIN) + " " + self.factor(d + 1)
        return e
    def cmp(self, d):
        e = self.orx(d)
        if d < 4 and self.r.random() < .3:
            e += " " + self.r.choice(CMP) + " " + self.orx(d + 1)
        return e
    def notx(self, d):
        if self.r.random() < .1: return "not " + self.notx(d + 1)
        return self.cmp(d)
    def test(self, d=0):
        if d > 4: return self.name()
        r = self.r.random()
        if r < .05: return "lambda " + ", ".join(self.name() for _ in range(self.r.randrange(3))) + ": " + self.test(d + 1)
        e = self.notx(d)
        if d < 4 and r < .25: e += " " + self.r.choice(["and", "or"]) + " " + self.notx(d + 1)
        if d < 3 and r > .93: e = e + " if " + self.notx(d + 1) + " else " + self.test(d + 1)
        return e
    def target(self):
        r = self.r.randrange(4)
        if r == 0: return self.name() + "." + self.name()
        if r == 1: return self.name() + "[" + self.test(2) + "]"
        if r == 2: return self.name() + ", " + self.name()
        return self.name()
    def simple(self):
        r = self.r.randrange(14)
        if r == 0: return "pass"
        if r == 1: return self.target() + " = " + self.test()
        if r == 2: return self.name() + " " + self.r.choice(AUG) + " " + self.test()
        if r == 3: return self.name() + ": " + self.r.choice(["int", "list[int]", "dict[str, int]", "u64"]) + " = " + self.test()
        if r == 4: return "assert " + self.test()
        if r == 5: return "del " + self.name()
        if r == 6: return "import " + self.name() + ("." + self.name() if self.r.random() < .5 else "")
        if r == 7: return "from " + self.name() + " import " + self.name() + " as " + self.name()
        if r == 8: return "raise " + self.test()
        return self.test()
    def block(self, ind, d):
        if d > 2 or self.r.random() < .2:
            return ": " + self.simple() + "\n"
        return ":\n" + "".join(self.stmt(ind + 1, d + 1) for _ in range(self.r.randrange(1, 4)))
    def stmt(self, ind, d=0):
        p = "    " * ind
        r = self.r.randrange(12 if d < 3 else 1)
        if r == 1: return p + "if " + self.test() + self.block(ind, d) + (p + "elif " + self.test() + self.block(ind, d) if self.r.random() < .4 else "") + (p + "else" + self.block(ind, d) if self.r.random() < .4 else "")
        if r == 2: return p + "while " + self.test() + self.block(ind, d)
        if r == 3: return p + "for " + self.r.choice([self.name(), self.name() + ", " + self.name()]) + " in " + self.test() + self.block(ind, d)
        if r == 4:
            args = ", ".join(self.name() + self.r.choice(["", ": int", "=" + self.test(3)]) for _ in range(self.r.randrange(3)))
            return p + "def " + self.name() + "(" + args + ")" + self.r.choice(["", " -> int"]) + self.block(ind, d)
        if r == 5: return p + "class " + self.name().capitalize() + self.r.choice(["", "(" + self.name() + ")"]) + self.block(ind, d)
        if r == 6: return p + "try" + self.block(ind, d) + p + "except " + self.name() + " as " + self.name() + self.block(ind, d) + (p + "finally" + self.block(ind, d) if self.r.random() < .5 else "")
        if r == 7: return p + "with " + self.test() + " as " + self.name() + self.block(ind, d)
        return p + self.simple() + "\n"
    def program(self):
        return "".join(self.stmt(0) for _ in range(self.r.randrange(3, 12)))

def break_program(rnd, src):
    """Break exactly one top-level line so that it is outside the grammar."""
    lines = src.split("\n")
    idx = [i for i, l in enumerate(lines) if l and not l.startswith(" ")]
    i = rnd.choice(idx)
    l = lines[i]
    kinds = ["unbalanced", "dangling", "doubleeq", "badkw", "header"]
    if l.endswith(":") and re.match(r"(if|while|for|def|class|try|with)\b", l):
        kinds.append("nocolon")
    k = rnd.choice(kinds)
    if k == "unbalanced": l = l.rstrip(":") + " (" if not l.endswith(":") else l[:-1] + " (:"
    elif k == "dangling": l = (l[:-1] + " +:") if l.endswith(":") else l + " *"
    elif k == "doubleeq": l = "a = = " + str(rnd.randrange(9))
    elif k == "badkw": l = rnd.choice(["import", "global", "from", "del", "while", "class", "def", "lambda: =", "with"])
    elif k == "header": l = rnd.choice(["if x:", "while y:", "def f():", "else:", "except:", "finally:"]); lines = lines[:i] + [l] + ["x = 1"]; return "\n".join(lines) + "\n", k
    elif k == "nocolon":
        l = l[:-1]  # the header's own ':' (its suite follows on the next lines)
        if l.strip() == "try": l = l + " ="
    lines[i] = l
    return "\n".join(lines), k

def run(args):
    return subprocess.run([FORGEC] + args, capture_output=True, text=True)

def strip_lines(j):
    d = json.loads(j)
    def s(n):
        n.pop("l", None)
        for c in n.get("c", []): s(c)
    s(d["root"]); d.pop("file")
    return d

def main():
    seed, n = 1, 200
    a = sys.argv[1:]
    if "--seed" in a: seed = int(a[a.index("--seed") + 1])
    if "--n" in a: n = int(a[a.index("--n") + 1])
    rnd = random.Random(seed)
    g = Gen(rnd)
    os.makedirs("build/fuzz", exist_ok=True)
    bad = 0; nv = 0; ni = 0
    for k in range(n):
        src = g.program()
        f = "build/fuzz/v%d.forge" % k
        if k % 2 == 0:
            nv += 1
            open(f, "w").write(src)
            r = run([f, "build/fuzz/a.json", "--emit=ast-json"])
            if r.returncode != 0:
                print("FAIL grammar-fuzz valid program rejected: %s: %s" % (f, r.stderr.strip()[:300])); bad += 1; continue
            run([f, "build/fuzz/p.forge", "--emit=pretty"])
            r2 = run(["build/fuzz/p.forge", "build/fuzz/b.json", "--emit=ast-json"])
            if r2.returncode != 0 or strip_lines(open("build/fuzz/a.json").read()) != strip_lines(open("build/fuzz/b.json").read()):
                print("FAIL grammar-fuzz round trip differs: %s" % f); bad += 1; continue
        else:
            ni += 1
            s2, kind = break_program(rnd, src)
            f = "build/fuzz/i%d.forge" % k
            open(f, "w").write(s2)
            r = run([f, "build/fuzz/a.json", "--emit=ast-json", "--diagnostics=json"])
            if r.returncode != 1 or not re.search(r'"code":"E010[0-5]"', r.stderr):
                print("FAIL grammar-fuzz invalid program (%s) not rejected cleanly: %s (exit %d) %s" % (kind, f, r.returncode, r.stderr[:200])); bad += 1; continue
    if bad == 0:
        print("PASS grammar-fuzz (seed %d: %d valid round-tripped, %d invalid rejected)" % (seed, nv, ni))
    return 1 if bad else 0

if __name__ == "__main__":
    sys.exit(main())
