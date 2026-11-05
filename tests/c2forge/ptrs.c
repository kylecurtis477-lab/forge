/* c2forge step 4 test: pointers, arrays, structs, ->, &, pointer arithmetic, sizeof, initializers */
void put_i64(long v);
void put_u64(unsigned long v);
void nl(void);

struct pt {
    int x;
    long y;
};

struct node {
    int val;
    struct node *next;
    unsigned char tag[3];
};

struct box {
    struct pt lo, hi;
    int n;
};

static long sum(const int *p, int n) {
    long s = 0;
    int i;
    for (i = 0; i < n; i++)
        s += p[i];
    return s;
}

static void swap(int *a, int *b) {
    int t = *a;
    *a = *b;
    *b = t;
}

static void scale(struct pt *p, int k) {
    p->x *= k;
    p->y = p->y * k + 1;
}

static long area(struct box b) {
    return (b.hi.x - b.lo.x) * (b.hi.y - b.lo.y);
}

static struct pt mid(struct pt a, struct pt b) {
    struct pt r;
    r.x = (a.x + b.x) / 2;
    r.y = (a.y + b.y) / 2;
    return r;
}

static int list_len(struct node *n) {
    int k = 0;
    while (n) {
        k++;
        n = n->next;
    }
    return k;
}

static int matsum(int m[][4], int rows) {
    int s = 0;
    for (int r = 0; r < rows; r++)
        for (int c = 0; c < 4; c++)
            s += m[r][c] * (r + 1);
    return s;
}

static void fill(int *dst, int n, int v) {
    int *end = dst + n;
    while (dst < end) {
        *dst = v;
        dst++;
        v += 3;
    }
}

int main(void) {
    int a[6] = {5, -2, 7, 1};
    put_i64(sum(a, 6)); nl();
    swap(&a[0], &a[2]);
    put_i64(a[0] * 10 + a[2]); nl();
    int *p = a + 1;
    *p = 40;
    p[2] += 9;
    put_i64(a[1] + a[3]); nl();
    int *q = &a[5];
    put_i64(q - p); nl();
    put_i64(p < q); put_i64(p == q); put_i64(*(q - 4) == a[1]); nl();
    p++;
    --q;
    put_i64(q - p); nl();

    struct pt s = {3, 4};
    scale(&s, 5);
    put_i64(s.x); put_i64(s.y); nl();
    struct pt *sp = &s;
    sp->y -= 20;
    (*sp).x += 1;
    put_i64(s.x + s.y); nl();

    struct box b = {{1, 2}, {11, 7}, 0};
    put_i64(area(b)); nl();
    struct pt m = mid(b.lo, b.hi);
    put_i64(m.x * 100 + m.y); nl();
    struct pt c;
    c = m;
    c.x = 0;
    put_i64(m.x + c.x + c.y); nl();

    struct node n3 = {30, 0, {1, 2, 3}};
    struct node n2 = {20, &n3};
    struct node n1 = {10, &n2};
    put_i64(list_len(&n1)); put_i64(list_len(n3.next)); nl();
    long tot = 0;
    for (struct node *it = &n1; it != 0; it = it->next)
        tot += it->val;
    put_i64(tot); nl();
    n1.next->next->tag[1] = 250;
    put_u64(n3.tag[1] + n3.tag[2]); nl();
    struct node *none = 0;
    put_i64(!none); put_i64(none == 0); nl();

    int g[3][4];
    for (int r = 0; r < 3; r++)
        for (int k = 0; k < 4; k++)
            g[r][k] = r * 4 + k;
    put_i64(matsum(g, 3)); nl();
    int *row = g[1];
    put_i64(row[3]); nl();

    long big[5];
    fill(list_len(&n1) > 2 ? a : g[0], 6, 1);
    put_i64(sum(a, 6)); nl();
    put_u64(sizeof(struct pt)); put_u64(sizeof(struct node)); put_u64(sizeof big); put_u64(sizeof(int *)); put_u64(sizeof g[0]); nl();
    int **pp = &p;
    **pp = 77;
    put_i64(a[2]); nl();
    return 0;
}
