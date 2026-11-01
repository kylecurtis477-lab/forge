# Benchmarks (Phase 2, 2026-10-31 19:11 CDT)

Best of 3 wall clock. Rust: rustc -C opt-level=3 (unstripped binaries). Phase 1 Forge: fib 0.015s, loop 0.274s, strbuild 0.093s, lists 0.189s.

| bench | Forge run | Rust -O3 run | CPython run | Forge compile | Rust compile | Forge bin | Rust bin | output |
|---|---|---|---|---|---|---|---|---|
| fib | 0.016s | 0.007s | 0.20s | 189ms | 259ms | 20,960 B | 11,395,072 B | 2178309 |
| loop | 0.099s | 0.077s | 5.99s | 116ms | 227ms | 20,984 B | 11,395,024 B | 199999997 |
| strbuild | 0.097s | 0.070s | 0.12s | 116ms | 435ms | 21,064 B | 11,399,736 B | 6888890 |
| lists | 0.200s | 0.092s | 3.22s | 107ms | 275ms | 21,248 B | 11,399,120 B | 49950000000 |
