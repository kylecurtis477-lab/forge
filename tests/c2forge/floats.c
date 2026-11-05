/* tests/c2forge/floats.c: c2forge step 5 -- float/double, C's usual arithmetic conversions, casts, void *.
   Prints integers derived from the floats plus the raw bits of doubles; must equal the gcc build. */
void put_i64(long v);
void put_u64(unsigned long v);
void nl(void);

struct pt { int x; double w; };

static double sq(double x) { return x * x; }
static float halve(float f) { return f / 2.0f; }
static double poly(double x) { double r = 0.0; int i; for (i = 0; i < 5; i++) r = r * x + (i + 1); return r; }
static unsigned long bits(double d) { return *(unsigned long *)&d; }
static void *pick(void *a, void *b, int c) { return c ? a : b; }

int main(void) {
    double d = 3.75, e = 1e9, z;
    float f = 2.5f;
    int i = 7;
    long big = 123456789012L;
    put_i64((long)d); nl();
    put_i64((long)(d * i)); nl();
    put_i64((int)-d); nl();
    put_i64((long)(e / 7)); nl();
    put_i64((long)(f * 4)); nl();
    put_i64((long)halve(9.0f) * 10); nl();
    put_i64((long)(sq(1.5) * 100)); nl();
    put_i64((long)poly(2.)); nl();
    put_u64(bits(d)); nl();
    put_u64(bits(.5 + 0.25)); nl();
    put_u64(bits((double)big)); nl();
    put_u64(bits(i / 2)); nl();
    put_u64(bits(i / 2.0)); nl();
    put_u64(bits(-0.0)); nl();
    put_i64(d > 3.5); nl();
    put_i64(d == 3.75 && f < 3); nl();
    z = 0;
    if (!z) put_i64(1); else put_i64(2);
    nl();
    z = i > 3 ? d : f;
    put_u64(bits(z)); nl();
    {
        unsigned char u = (unsigned char)200.7;
        short s = (short)-12.9;
        unsigned int w = (unsigned int)4000000000.0;
        put_i64(u); nl(); put_i64(s); nl(); put_u64(w); nl();
        put_i64((char)i + (unsigned char)511); nl();
    }
    {
        struct pt a[3];
        double acc = 0;
        int k;
        void *vp;
        struct pt *pp;
        for (k = 0; k < 3; k++) { a[k].x = k; a[k].w = k * 0.5; }
        vp = &a[1];
        pp = vp;
        acc += pp->w + pp[1].w;
        put_i64((long)(acc * 1000)); nl();
        pp = (struct pt *)pick(&a[0], &a[2], 0);
        put_i64(pp->x); nl();
        put_i64(pick(a, a, 1) == (void *)a); nl();
        put_i64((long)((char *)&a[2] - (char *)&a[0])); nl();
        put_i64((unsigned long)(void *)0 == 0); nl();
        put_i64((int *)(unsigned long)&a[1].x == &a[1].x); nl();
        (void)acc;
        (void)k;
    }
    return 0;
}
