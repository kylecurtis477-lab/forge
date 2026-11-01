| bench | Forge run | Rust -O3 run | CPython run | Forge compile | Rust compile | Forge bin | Rust bin | output |
|---|---|---|---|---|---|---|---|---|
| fib | 0.015s | 0.006s | 0.20s | 8ms | 216ms | 10,008 B | 11,395,072 B | 2178309 |
| loop | 0.274s | 0.080s | 5.87s | 12ms | 259ms | 10,008 B | 11,395,024 B | 199999997 |
| strbuild | 0.093s | 0.071s | 0.12s | 9ms | 468ms | 10,072 B | 11,399,736 B | 6888890 |
| lists | 0.189s | 0.136s | 3.22s | 7ms | 258ms | 10,200 B | 11,399,120 B | 49950000000 |
