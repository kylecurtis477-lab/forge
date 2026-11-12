/* c2forge: string literals as values, char s[] = "...", adjacent concatenation */
void put_i64(long v);
void nl(void);

static void show(long v) {
    put_i64(v);
    nl();
}

char gmsg[] = "hi";
char gpad[8] = "hi";

int main(void) {
    const char *s;
    const char *t;
    const char *e;
    int n;
    s = "hi";
    show(s[0]);
    show(s[1]);
    show(s[2]);
    show((long)sizeof("hi"));
    {
        char a[] = "hi";
        show((long)sizeof(a));
        show(a[0]);
        a[0] = 'H';
        show(a[0]);
        show(s[0]);
    }
    {
        char b[8] = "hi";
        show((long)sizeof(b));
        show(b[0]);
        show(b[2]);
        show(b[7]);
    }
    {
        char c[2] = "hi";
        show((long)sizeof(c));
        show(c[0]);
        show(c[1]);
    }
    t = "hello" " " "world";
    n = 0;
    while (t[n] != 0) {
        n = n + 1;
    }
    show(n);
    show(t[5]);
    show(gmsg[0]);
    show(gmsg[2]);
    show((long)sizeof(gmsg));
    show(gpad[0]);
    show((long)sizeof(gpad));
    show((long)"A"[0]);
    e = "\n\t\\";
    show(e[0]);
    show(e[1]);
    show(e[2]);
    show(e[3]);
    e = "\x41\060";
    show(e[0]);
    show(e[1]);
    return 0;
}
