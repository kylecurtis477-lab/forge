#!/usr/bin/env python3
"""Forge (self-hosted compiler) vs Rust (rustc -C opt-level=3) vs CPython. Best-of-5 wall clock."""
import os, subprocess as sp, time
os.chdir(os.path.dirname(os.path.abspath(__file__))); os.makedirs("out", exist_ok=True)
def timed(cmd):
    s = time.perf_counter(); r = sp.run(cmd, capture_output=True, text=True, check=True); return time.perf_counter() - s, r.stdout.strip()
def best(cmd): return min(timed(cmd)[0] for _ in range(5))
print("| bench | Forge run | Rust -O3 run | CPython run | Forge compile | Rust compile | Forge bin | Rust bin | output |")
print("|---|---|---|---|---|---|---|---|---|")
for b in ["fib", "loop", "strbuild", "lists", "floats", "strops"]:
    fc, _ = timed(["../forge", f"{b}.forge", "-o", f"out/{b}-forge"])
    rc, _ = timed(["rustc", "-C", "opt-level=3", "-o", f"out/{b}-rust", f"{b}.rs"])
    outs = {timed(c)[1] for c in ([f"out/{b}-forge"], [f"out/{b}-rust"], ["python3", f"{b}.py"])}
    agree = outs.pop() if len(outs) == 1 else "MISMATCH"
    print(f"| {b} | {best([f'out/{b}-forge']):.3f}s | {best([f'out/{b}-rust']):.3f}s | {best(['python3', f'{b}.py']):.2f}s | "
          f"{fc*1000:.0f}ms | {rc*1000:.0f}ms | {os.path.getsize(f'out/{b}-forge'):,} B | {os.path.getsize(f'out/{b}-rust'):,} B | {agree} |")
