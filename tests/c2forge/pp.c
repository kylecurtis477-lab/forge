/* tests/c2forge/pp.c: c2forge step 6 -- preprocessor subset. Object-like #define (nested, redefined
   after #undef, multi-line), #include of standard headers for their typedef names and limit macros.
   Must print the same as the gcc build. */
#include <stdint.h>
#include <stddef.h>
#include <limits.h>
void put_i64(long v);
void put_u64(unsigned long v);
void nl(void);

#define N 8
#define TWO_N (N * 2)
#define MASK 0xffu
#define SCALE \
    (TWO_N + 1)
#define SELF SELF
#define STEP 3
#define TABSZ 16
#define LAST (TABLE_LEN - 1)
#define TABLE_LEN N


static uint8_t low(uint32_t v) { return (uint8_t)(v & MASK); }

int main(void) {
    size_t i;
    uint32_t tab[TABSZ];
    int SELF = 5;
    int *p = NULL;
    for (i = 0; i < TWO_N; i++) tab[i] = (uint32_t)(i * SCALE);
    put_u64(tab[TWO_N - 1]); nl();
    put_i64(SELF + STEP); nl();
#undef STEP
#define STEP 10
    put_i64(SELF + STEP); nl();
    put_i64(LAST); nl();
    put_u64(low(0x12345u)); nl();
    put_i64(INT_MAX); nl();
    put_i64(INT_MIN); nl();
    put_u64(UINT_MAX); nl();
    put_i64(LONG_MIN); nl();
    put_u64(ULONG_MAX); nl();
    put_i64(CHAR_BIT * (long)sizeof(int64_t)); nl();
    put_u64(UINT32_MAX); nl();
    put_i64(INT8_MIN); nl();
    put_u64(SIZE_MAX); nl();
    put_i64(p == NULL); nl();
    put_i64((int16_t)(INT16_MAX + 1)); nl();
    return 0;
}
