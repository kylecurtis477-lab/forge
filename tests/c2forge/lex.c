/* c2forge lexer test: every token class */
#include <stdint.h>
#define N 16 \
  /* continued */
typedef unsigned long u64; // a comment
static int f(int *p, const char *s) {
    unsigned x = 0x1Fu + 017 + 42UL;
    double d = 1.5e-3 + .25 + 2.f;
    char c = '\n'; char q = '\'';
    x <<= 2; x >>= 1; p->a++; --x;
    return x == 1 && s[0] != 0 || !x ? sizeof(u64) : ~x % 3;
}
const char *m = "a \"b\" \\ c";
