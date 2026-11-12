/* c2forge step 2 test: integer types, promotions, conversions, precedence, calls, if/else, while */
#include <stdint.h>
void put_i64(long v);
void put_u64(unsigned long v);
void nl(void);
static int gcd(int a, int b);

/* 0.15.2: file-scope variables (integer constant initializers, wrapped as C converts; the rest zero) */
static int counter = -3;
unsigned char wrapped = 300, small = -1;
long gbig = 0x7fffffffffffffffL;
unsigned long gall = -1;
short gneg = -40000;
static unsigned hist[4];
double acc;

static void show(long v) {
    put_i64(v);
    nl();
}

static int gcd(int a, int b) {
    while (b != 0) {
        int t = a % b;
        a = b;
        b = t;
    }
    return a;
}

uint32_t rotl(uint32_t x, int k) {
    return (x << k) | (x >> (32 - k));
}

int collatz(long n) {
    int steps = 0;
    while (n != 1) {
        if (n % 2 == 0)
            n /= 2;
        else
            n = 3 * n + 1;
        steps++;
    }
    return steps;
}

int classify(int x) {
    if (x < 0) return -1;
    else if (x == 0) return 0;
    else if (x < 10 && !(x == 5)) return 1;
    return 2;
}

static void bump(int k) {
    hist[k] = hist[k] + 1;
    counter++;
}

int main(void) {
    unsigned char a = 200, b = 100;
    int s = a + b;
    unsigned char w = a + b;
    short h = 30000;
    unsigned u = 0;
    int neg = -17;
    uint64_t big = 0xFFFFFFFFFFFFFFFFu;
    int64_t sb = -1;
    char c = 'A';
    show(s);
    show(w);
    h += 10000;
    show(h);
    u -= 1;
    put_u64(u); nl();
    show(neg / 5);
    show(neg % 5);
    show(neg < u);
    show(-1 < 0u);
    show(sb < 1);
    put_u64(big >> 60); nl();
    show(1 + 2 * 3 - 8 / 4 % 3);
    show(1 << 4 | 3 & 5 ^ 6);
    show((7 > 3) + (2 == 2) + !0 + !5);
    show(~0 + ~a);
    show(c + 1);
    show('\n' + '\x41' + '\101');
    show(gcd(1071, 462));
    put_u64(rotl(0x80000001u, 4)); nl();
    show(collatz(27));
    show(classify(-4) * 1000 + classify(0) * 100 + classify(5) * 10 + classify(3));
    show(017 + 0x10 + 10L);
    show(2147483647 + 1L);
    counter = counter + 10;
    bump(2);
    bump(2);
    bump(3);
    show(counter);
    show(wrapped * 1000 + small);
    show(gbig);
    put_u64(gall >> 60); nl();
    show(gneg);
    show(hist[0] * 100 + hist[2] * 10 + hist[3]);
    acc = acc + 2.5;
    show((long)(acc * 4.0));
    return 0;
}
