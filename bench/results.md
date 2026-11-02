# Benchmarks (best of 5, 2026-11-01 20:05 CST)

## Default (-O), after the int-band fix (commit 447b1d7)
| bench | Forge run | Rust -O3 run | CPython run | Forge compile | Rust compile | Forge bin | Rust bin | output |
|---|---|---|---|---|---|---|---|---|
| fib | 0.010s | 0.006s | 0.18s | 172ms | 233ms | 137,296 B | 11,395,072 B | 2178309 |
| loop | 0.094s | 0.073s | 5.50s | 203ms | 184ms | 137,264 B | 11,395,024 B | 199999997 |
| strbuild | 0.027s | 0.053s | 0.08s | 275ms | 328ms | 137,344 B | 11,399,736 B | 6888890 |
| lists | 0.042s | 0.045s | 2.96s | 190ms | 223ms | 137,384 B | 11,399,120 B | 49950000000 |
| floats | 0.031s | 0.015s | 0.18s | 237ms | 174ms | 137,776 B | 11,390,192 B | 1500122368 |
| strops | 0.018s | 0.017s | 0.04s | 165ms | 324ms | 138,656 B | 11,516,016 B | 38408 977889 22311 |

Self-compile (forge selfhost/forgec.forge): 1.25s.
Band checks (2**62..2**62+3 alias bool/None) are done where a value leaves int arithmetic, not after every add; `v = v + 1` counters starting from a small constant skip jo entirely.

## Previous runs (2026-10-31 22:52 CT)

## Default (-O, optimizing IR backend)
| bench | Forge run | Rust -O3 run | CPython run | Forge compile | Rust compile | Forge bin | Rust bin | output |
|---|---|---|---|---|---|---|---|---|
| fib | 0.008s | 0.006s | 0.19s | 194ms | 211ms | 137,184 B | 11,395,072 B | 2178309 |
| loop | 0.095s | 0.073s | 5.55s | 203ms | 196ms | 137,184 B | 11,395,024 B | 199999997 |
| strbuild | 0.067s | 0.061s | 0.09s | 236ms | 308ms | 137,232 B | 11,399,736 B | 6888890 |
| lists | 0.064s | 0.056s | 3.07s | 204ms | 236ms | 137,272 B | 11,399,120 B | 49950000000 |

## -O0 (direct token codegen, fallback)
| bench | Forge run | Rust -O3 run | CPython run | Forge compile | Rust compile | Forge bin | Rust bin | output |
|---|---|---|---|---|---|---|---|---|
| fib | 0.009s | 0.006s | 0.19s | 237ms | 253ms | 137,208 B | 11,395,072 B | 2178309 |
| loop | 0.097s | 0.072s | 5.53s | 189ms | 206ms | 137,208 B | 11,395,024 B | 199999997 |
| strbuild | 0.069s | 0.057s | 0.10s | 263ms | 345ms | 137,232 B | 11,399,736 B | 6888890 |
| lists | 0.119s | 0.052s | 3.03s | 294ms | 234ms | 137,600 B | 11,399,120 B | 49950000000 |

Self-compile of the compiler (best of 5): 1.89s with -O, 1.96s with -O0.

Note: the IR backend only takes plain int functions with int params, so 1 of 622 compiler functions uses it. fib does not improve because temporaries are not coalesced.
