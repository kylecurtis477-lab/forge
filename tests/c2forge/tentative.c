/* c2forge: tentative definitions and extern declarations of the same global merge into one definition */
void put_i64(long v);
void nl(void);

static void show(long v) {
    put_i64(v);
    nl();
}

int counter;
extern int limit;
long total;
int table[4];

static int bump(void) {
    counter = counter + 1;
    return counter;
}

int counter;
int limit = 7;
int table[4] = {10, 20, 30};
extern long total;
long total;
unsigned char flag;
unsigned char flag = 300;

int main(void) {
    int i;
    for (i = 0; i < limit; i = i + 1)
        total = total + bump() * table[i % 4];
    show(counter);
    show(limit);
    show(total);
    show(table[2] + table[3]);
    show(flag);
    return 0;
}
