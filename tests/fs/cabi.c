/* C side of tests/fs/cabi.forge: built with gcc -ffreestanding, linked with the Forge object */
typedef unsigned long u64;
extern long forge_mix(long a, unsigned char b, short c, unsigned d, long e, long f);
extern void puts(const char *s);
extern void put_i64(long v);
long c_add6(long a, long b, long c, long d, long e, long f) { return a + 2*b + 3*c + 4*d + 5*e + 6*f; }
unsigned char c_trunc(u64 v) { return (unsigned char)v; }
long c_calls_forge(void) { return forge_mix(1, 255, -2, 4000000000u, 5, 6); }
struct S { unsigned char a; unsigned b; unsigned short c; };
unsigned c_struct_sum(struct S *s) { return s->a + s->b + s->c; }
