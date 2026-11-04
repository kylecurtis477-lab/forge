/* SysV structs by value and struct returns: INTEGER class (<= 16 bytes, rax:rdx) and MEMORY (> 16, stack / hidden pointer) */
struct P { long x, y; };                 /* 16: two registers */
struct Q { unsigned char a; unsigned int b; unsigned short c; };  /* 12: two registers, partial tail */
struct S3 { unsigned char a, b, c; };    /* 3: one register */
struct B { long a, b, c; };              /* 24: memory */
long c_pbyval(struct P p) { return p.x * 10 + p.y; }
long c_qbyval(long z, struct Q q, struct S3 s) { return z + q.a * 1000000 + q.b * 1000 + q.c + s.a * 100 + s.b * 10 + s.c; }
long c_bbyval(long a, struct B b, long c) { return a * 1000 + b.a * 100 + b.b * 10 + b.c + c; }
/* a 2-word struct that no longer fits in registers goes on the stack; later scalars still use registers */
long c_spill(long a, long b, long c, long d, long e, struct P p, long f) { return a + b + c + d + e + p.x * 100 + p.y * 10 + f; }
struct P c_pret(long a) { struct P p = { a, a + 1 }; return p; }
struct Q c_qret(void) { struct Q q = { 7, 4000000000u, 65535 }; return q; }
struct B c_bret(long a) { struct B b = { a, a * 2, a * 3 }; return b; }
extern long forge_pbyval(struct P);
extern struct P forge_pret(long);
extern struct B forge_bret(long, struct B);
long c_calls(void) {
  struct P p = { 3, 4 }; struct B b = { 1, 2, 3 };
  struct P r = forge_pret(5); struct B q = forge_bret(10, b);
  return forge_pbyval(p) * 1000000 + r.x * 100000 + r.y * 10000 + q.a * 100 + q.b * 10 + q.c;
}
