/* c2forge step 3 test: for, do/while, break/continue, switch (fall-through, default anywhere), ?: */
void put_i64(long v);
void nl(void);

static void show(long v) {
    put_i64(v);
    nl();
}

static int sq(int x) {
    return x * x;
}

int sum_odd(int n) {
    int s = 0;
    for (int i = 0; i < n; i++) {
        if (i % 2 == 0) continue;
        if (i > 15) break;
        s += i;
    }
    return s;
}

int digits(unsigned v) {
    int n = 0;
    do {
        n++;
        v /= 10;
    } while (v != 0);
    return n;
}

int do_continue(void) {
    int i = 0, s = 0;
    do {
        i++;
        if (i == 3) continue;
        s += i;
    } while (i < 6);
    return s;
}

int kind(int c) {
    int r = 0;
    switch (c) {
    case 1:
    case 2:
        r = 10;
        break;
    default:
        r = -1;
        break;
    case 3:
        r = 30;
    case 4:
        r += 4;
        break;
    case 5: {
        r = 50;
        break;
    }
    }
    return r;
}

int nested(int n) {
    int count = 0, i, j;
    for (i = 0; i < n; i++)
        for (j = 0; j < n; j++) {
            if (j > i) break;
            switch (i - j) {
            case 0: count += 100; break;
            case 1: count += 10; break;
            default: count += 1;
            }
        }
    return count;
}

int tern(int x) {
    int a = x > 0 ? x : -x;
    int b = x > 10 ? 2 : x > 5 ? 1 : 0;
    long c = x ? sq(x) : 7L;
    return a * 10000 + b * 1000 + (x != 0 && (x > 3 ? sq(x) > 20 : 0)) * 100 + c % 100;
}

int main(void) {
    int i;
    long k = 0;
    show(sum_odd(100));
    show(digits(0) * 100 + digits(12345));
    show(do_continue());
    for (i = 0; i <= 6; i++) k = k * 100 + kind(i) + 1;
    show(k);
    show(nested(5));
    show(tern(-4));
    show(tern(7));
    show(tern(12));
    show(tern(0));
    for (;;) {
        k++;
        if (k % 7 == 0) break;
    }
    show(k % 1000);
    while (1) { k--; if (k % 5 == 0) break; }
    show(k % 1000);
    return 0;
}
