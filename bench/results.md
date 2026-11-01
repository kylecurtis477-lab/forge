# Benchmarks (best of 5, 2026-10-31 22:52 CT)

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
