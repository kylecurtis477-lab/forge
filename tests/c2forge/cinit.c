/* c2forge: file-scope integer initializers that are constant expressions, wrapped to the global's type */
void put_i64(long v);
void nl(void);

static void show(long v) {
    put_i64(v);
    nl();
}

int k1 = 2 + 3 * 4;
unsigned char k2 = (1 << 8) + 44;
short k3 = -(40000);
long k4 = 1L << 40;
int k5 = ~0 ^ 0xF0;
unsigned int k6 = 0xFFFF & ~0x0F0F;
int k7 = sizeof(long) * 3 % 5;
int k8 = -7 / 2;
int k9 = -7 % 3;
int k10 = (-16) >> 2;
long k11 = 'A' | 0x20;
unsigned int k12 = -1;
signed char k13 = 0x7F + 2;
int k14 = (1 + 2) * (3 + 4) - (10 / 3) + (10 % 4) - ~1;
int arr[2 << 2];
double z = 0;
int k15 = +5;

int main(void) {
    show(k1);
    show(k2);
    show(k3);
    show(k4);
    show(k5);
    show(k6);
    show(k7);
    show(k8);
    show(k9);
    show(k10);
    show(k11);
    show(k12);
    show(k13);
    show(k14);
    show((long)(sizeof(arr) / sizeof(arr[0])));
    show((long)z);
    show(k15);
    return 0;
}
