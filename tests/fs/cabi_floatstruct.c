/* SysV structs with float fields by value: each eightbyte is SSE (only floats: xmm) or INTEGER (gpr);
   mixed structs split across xmm + gpr, returns in xmm0/xmm1/rax/rdx, > 16 bytes via memory */
struct V2 { double x, y; };              /* SSE, SSE */
struct M { long a; double b; };          /* INTEGER, SSE */
struct F4 { float a, b, c, d; };         /* SSE, SSE (two floats per xmm) */
struct F3 { float a, b, c; };            /* SSE, SSE (partial tail) */
struct FI { float a; int b; double c; }; /* INTEGER (mixed eightbyte), SSE */
struct BF { double a, b, c; };           /* 24: memory */
long c_v2(struct V2 v) { return (long)(v.x * 1000) + (long)(v.y * 100); }
long c_m(long k, struct M m, double z) { return k * 100000 + m.a * 1000 + (long)(m.b * 100) + (long)(z * 4); }
long c_f4(struct F4 f) { return (long)((f.a + f.b * 2 + f.c * 3 + f.d * 4) * 10); }
long c_f3(struct F3 f, long n) { return (long)(f.a * 1000 + f.b * 100 + f.c * 10) + n; }
long c_fi(struct FI f) { return (long)(f.a * 100) * 1000 + f.b * 10 + (long)f.c; }
long c_bf(struct BF b, double x) { return (long)(b.a * 1000 + b.b * 100 + b.c * 10 + x * 2); }
/* V2 needs two xmm registers but only xmm7 is left: it goes on the stack and h still takes xmm7 */
long c_xspill(double a, double b, double c, double d, double e, double f, double g, struct V2 v, double h) {
  return (long)((a + b + c + d + e + f + g) * 1000 + v.x * 100 + v.y * 10 + h * 8);
}
struct V2 c_v2ret(double x) { struct V2 v = { x, x * 2 }; return v; }
struct M c_mret(long a) { struct M m = { a, 0.75 }; return m; }
struct F3 c_f3ret(void) { struct F3 f = { 1.0f, 2.0f, 3.0f }; return f; }
struct FI c_firet(void) { struct FI f = { 2.5f, -3, 9.0 }; return f; }
struct BF c_bfret(double x) { struct BF b = { x, x + 1, x + 2 }; return b; }
extern struct V2 forge_v2(struct V2);
extern struct M forge_m(struct M, long);
extern struct FI forge_fi(struct FI);
extern long forge_f4(struct F4);
extern struct BF forge_bf(struct BF);
long c_calls(void) {
  struct V2 v = { 1.0, 3.0 }; struct M m = { 4, 1.5 }; struct FI fi = { 0.5f, 21, 3.0 };
  struct F4 f4 = { 1.0f, 1.0f, 1.0f, 1.0f }; struct BF bf = { 1.0, 2.0, 3.0 };
  struct V2 a = forge_v2(v); struct M b = forge_m(m, 5); struct FI c = forge_fi(fi); struct BF d = forge_bf(bf);
  long s = (long)(a.x * 10 + a.y);                  /* 60 + 1.5 -> 61 */
  s = s * 1000 + b.a * 10 + (long)b.b;              /* 61090 + 4 */
  s = s * 1000 + (long)(c.a * 10) * 100 + c.b + (long)c.c;  /* 1500 + 42 + 2 */
  s = s * 1000 + forge_f4(f4);                      /* 100 */
  return s * 1000 + (long)(d.a * 100 + d.b * 10 + d.c);     /* 321 */
}
