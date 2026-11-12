/* c2forge: float and double globals with initializers (scalars and 1-D brace lists, zero padded) */
void put_i64(long v);
void nl(void);

static void show(long v) {
    put_i64(v);
    nl();
}

double rate = 0.075;
float tenth = 0.1f;
double neg = -2.5e-3;
double whole = 3;
float folded = -(1 << 4);
double minus_zero = -0.0;
double table[6] = {1.5, -0.25, 1e10, 2, .5};
float coeffs[] = {0.1, 3.4028235e38f, 1e-40f, 16777217, -1};
double tentative;
double tentative = 42.125;

int main(void) {
    float t = 0.1;
    double sum = 0;
    int i;
    show((long)(rate * 1000000));
    show(tenth == t);
    show((long)(neg * 1000000));
    show((long)whole);
    show((long)folded);
    show(minus_zero == 0);
    show(1 / minus_zero < 0);
    for (i = 0; i < 6; i = i + 1)
        sum = sum + table[i] * (i + 1);
    show((long)sum);
    show(table[5] == 0);
    show(sizeof(coeffs) / sizeof(coeffs[0]));
    show(coeffs[0] == t);
    show(coeffs[1] > 3.4e38);
    show(coeffs[2] > 0 && coeffs[2] < 1e-39);
    show((long)coeffs[3]);
    show((long)coeffs[4]);
    show((long)(tentative * 1000));
    return 0;
}
