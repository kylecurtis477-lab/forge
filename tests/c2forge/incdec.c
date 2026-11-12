/* c2forge: ++/-- inside expressions (well-defined cases only) */
void put_i64(long v);
void nl(void);

static void show(long v) {
    put_i64(v);
    nl();
}

int main(void) {
    int x = 10;
    int y;
    y = ++x;
    show(y);
    show(x);
    y = x++;
    show(y);
    show(x);
    show(++x + 2);
    show(x);
    int a[3];
    int i;
    a[0] = 0;
    a[1] = 0;
    a[2] = 0;
    i = 0;
    a[i++] = 7;
    a[++i] = 8;
    show(a[0]);
    show(a[1]);
    show(a[2]);
    show(i);
    int p[2];
    int *q;
    p[0] = 3;
    p[1] = 4;
    q = p;
    show(*q++);
    show(*q);
    show(++*p);
    q = p;
    q++;
    show(*q);
    return 0;
}
