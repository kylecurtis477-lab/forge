/* c2forge: array sizes that are integer constant expressions */
void put_i64(long v);
void nl(void);

static void show(long v) {
    put_i64(v);
    nl();
}

int main(void) {
    int a[2 + 2];
    int b[sizeof(int)];
    int c[sizeof(char) * 8];
    int d[(1 + 2) * 3];
    int e[0x10 - 6];
    unsigned char buf[sizeof(int) + sizeof(long)];
    int f[7 / 2];
    int g[(0 - 7) % 4 + 5];
    int h[-7 / 2 + 6];
    int i;
    int sa = 0;
    int sb = 0;
    int sc = 0;
    int sd = 0;
    int se = 0;
    int sbuf = 0;
    for (i = 0; i < 4; i = i + 1)
        a[i] = i + 1;
    for (i = 0; i < 4; i = i + 1)
        b[i] = i + 10;
    for (i = 0; i < 8; i = i + 1)
        c[i] = i;
    for (i = 0; i < 9; i = i + 1)
        d[i] = i;
    for (i = 0; i < 10; i = i + 1)
        e[i] = i;
    for (i = 0; i < 12; i = i + 1)
        buf[i] = (unsigned char)i;
    for (i = 0; i < 4; i = i + 1)
        sa = sa + a[i];
    for (i = 0; i < 4; i = i + 1)
        sb = sb + b[i];
    for (i = 0; i < 8; i = i + 1)
        sc = sc + c[i];
    for (i = 0; i < 9; i = i + 1)
        sd = sd + d[i];
    for (i = 0; i < 10; i = i + 1)
        se = se + e[i];
    for (i = 0; i < 12; i = i + 1)
        sbuf = sbuf + buf[i];
    show(sa);
    show(sb);
    show(sc);
    show(sd);
    show(se);
    show(sbuf);
    show((long)(sizeof(a) / sizeof(a[0])));
    show((long)(sizeof(b) / sizeof(b[0])));
    show((long)(sizeof(c) / sizeof(c[0])));
    show((long)(sizeof(buf)));
    /* C division truncates toward zero and % takes the dividend's sign: 3, 2, 3 */
    show((long)(sizeof(f) / sizeof(f[0])));
    show((long)(sizeof(g) / sizeof(g[0])));
    show((long)(sizeof(h) / sizeof(h[0])));
    return 0;
}
