/* c2forge: 1-D integer brace lists at file scope, and array sizes taken from the initializer (T a[] = {...}) */
void put_i64(long v);
void nl(void);

static void show(long v) {
    put_i64(v);
    nl();
}

int primes[6] = {2, 3, 5, 7, 11, 13};
short wrapped[4] = {40000, -40000, 1 << 4};
unsigned char bytes[] = {300, -1, 'A', 0x7F + 1,};
long big[] = {1L << 40, -(1L << 33), ~0};
int sizes[sizeof(long) / 2] = {sizeof(char), sizeof(short), sizeof(int), sizeof(long)};

int main(void) {
    int squares[] = {0, 1, 4, 9, 16};
    int grid[][3] = {{1, 2, 3}, {4, 5, 6}, {7}};
    double weights[5] = {1.5, -2, .25, 3e2};
    float halves[] = {0.5f, -1.5, 2};
    double acc = 0;
    long s = 0;
    int i;
    for (i = 0; i < 6; i = i + 1)
        s = s + primes[i] * (i + 1);
    show(s);
    for (i = 0; i < 4; i = i + 1)
        show(wrapped[i]);
    for (i = 0; i < 4; i = i + 1)
        show(bytes[i]);
    show((long)(sizeof(bytes)));
    for (i = 0; i < 3; i = i + 1)
        show(big[i]);
    show((long)(sizeof(big) / sizeof(big[0])));
    for (i = 0; i < 5; i = i + 1)
        acc = acc + weights[i] * (i + 1);
    show((long)(acc * 100));
    acc = 0;
    for (i = 0; i < 3; i = i + 1)
        acc = acc + halves[i] * 10;
    show((long)acc);
    show((long)(sizeof(halves)));
    for (i = 0; i < 4; i = i + 1)
        show(sizes[i]);
    s = 0;
    for (i = 0; i < 5; i = i + 1)
        s = s + squares[i];
    show(s);
    show((long)(sizeof(squares) / sizeof(squares[0])));
    show((long)(sizeof(grid) / sizeof(grid[0])));
    show(grid[1][2] + grid[2][0] + grid[2][2]);
    return 0;
}
