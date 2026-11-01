| bench | Forge run | Rust -O3 run | CPython run | Forge compile | Rust compile | Forge bin | Rust bin | output |
|---|---|---|---|---|---|---|---|---|
| fib | 0.008s | 0.006s | 0.19s | 259ms | 314ms | 137,208 B | 11,395,072 B | 2178309 |
| loop | 0.097s | 0.073s | 5.58s | 278ms | 246ms | 137,208 B | 11,395,024 B | 199999997 |
| strbuild | 0.067s | 0.058s | 0.10s | 304ms | 383ms | 137,232 B | 11,399,736 B | 6888890 |
| lists | 0.103s | 0.066s | 3.06s | 258ms | 235ms | 137,600 B | 11,399,120 B | 49950000000 |
