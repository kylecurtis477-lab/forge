# Benchmarks (Phase 2, 2026-10-31 19:11 CDT)

Best of 3 wall clock. Rust: rustc -C opt-level=3 (unstripped binaries). Phase 1 Forge: fib 0.015s, loop 0.274s, strbuild 0.093s, lists 0.189s.

| bench | Forge run | Rust -O3 run | CPython run | Forge compile | Rust compile | Forge bin | Rust bin | output |
|---|---|---|---|---|---|---|---|---|
| fib | 0.016s | 0.007s | 0.20s | 189ms | 259ms | 20,960 B | 11,395,072 B | 2178309 |
| loop | 0.099s | 0.077s | 5.99s | 116ms | 227ms | 20,984 B | 11,395,024 B | 199999997 |
| strbuild | 0.097s | 0.070s | 0.12s | 116ms | 435ms | 21,064 B | 11,399,736 B | 6888890 |
| lists | 0.200s | 0.092s | 3.22s | 107ms | 275ms | 21,248 B | 11,399,120 B | 49950000000 |

## Phase 3 (mark-sweep GC on), 2026-10-31

| bench | Forge run | Rust -O3 run | CPython run | Forge compile | Rust compile | Forge bin | Rust bin | output |
|---|---|---|---|---|---|---|---|---|
| fib | 0.015s | 0.006s | 0.19s | 173ms | 271ms | 85,104 B | 11,395,072 B | 2178309 |
| loop | 0.088s | 0.073s | 5.57s | 187ms | 200ms | 85,136 B | 11,395,024 B | 199999997 |
| strbuild | 0.090s | 0.070s | 0.11s | 285ms | 332ms | 85,216 B | 11,399,736 B | 6888890 |
| lists | 0.176s | 0.061s | 3.10s | 237ms | 241ms | 85,552 B | 11,399,120 B | 49950000000 |
