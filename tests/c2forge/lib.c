/* tests/c2forge/lib.c: c2forge step 7 -- a first small real C library, translated unmodified in style:
   CRC-32 (zlib's polynomial 0xedb88320, table-driven like zlib's crc32), Adler-32 (zlib's adler32 with
   its NMAX deferred modulo), and the XTEA block cipher (Needham & Wheeler's reference encipher/decipher,
   32 rounds). main hashes and encrypts generated buffers and prints the results; the translated program
   must print exactly what the gcc build prints. */
#include <stdint.h>
#include <stddef.h>
void put_i64(long v);
void put_u64(unsigned long v);
void nl(void);

#define CRC_POLY 0xedb88320u
#define ADLER_BASE 65521u
#define ADLER_NMAX 5552
#define XTEA_DELTA 0x9e3779b9u
#define XTEA_ROUNDS 32
#define BUF_LEN 1000

/* ---- CRC-32 ---- */
static void crc32_table(uint32_t *table) {
    uint32_t n, c;
    int k;
    for (n = 0; n < 256; n++) {
        c = n;
        for (k = 0; k < 8; k++)
            c = (c & 1) ? CRC_POLY ^ (c >> 1) : c >> 1;
        table[n] = c;
    }
}

static uint32_t crc32_update(const uint32_t *table, uint32_t crc, const uint8_t *buf, size_t len) {
    size_t i;
    crc = crc ^ 0xffffffffu;
    for (i = 0; i < len; i++)
        crc = table[(crc ^ buf[i]) & 0xff] ^ (crc >> 8);
    return crc ^ 0xffffffffu;
}

/* ---- Adler-32 ---- */
static uint32_t adler32(uint32_t adler, const uint8_t *buf, size_t len) {
    uint32_t a = adler & 0xffff, b = (adler >> 16) & 0xffff;
    size_t n;
    while (len > 0) {
        n = len < ADLER_NMAX ? len : ADLER_NMAX;
        len -= n;
        while (n > 0) {
            a += *buf;
            b += a;
            buf++;
            n--;
        }
        a %= ADLER_BASE;
        b %= ADLER_BASE;
    }
    return (b << 16) | a;
}

/* ---- XTEA ---- */
static void xtea_encipher(unsigned int num_rounds, uint32_t v[2], const uint32_t key[4]) {
    unsigned int i;
    uint32_t v0 = v[0], v1 = v[1], sum = 0, delta = XTEA_DELTA;
    for (i = 0; i < num_rounds; i++) {
        v0 += (((v1 << 4) ^ (v1 >> 5)) + v1) ^ (sum + key[sum & 3]);
        sum += delta;
        v1 += (((v0 << 4) ^ (v0 >> 5)) + v0) ^ (sum + key[(sum >> 11) & 3]);
    }
    v[0] = v0; v[1] = v1;
}

static void xtea_decipher(unsigned int num_rounds, uint32_t v[2], const uint32_t key[4]) {
    unsigned int i;
    uint32_t v0 = v[0], v1 = v[1], delta = XTEA_DELTA, sum = delta * num_rounds;
    for (i = 0; i < num_rounds; i++) {
        v1 -= (((v0 << 4) ^ (v0 >> 5)) + v0) ^ (sum + key[(sum >> 11) & 3]);
        sum -= delta;
        v0 -= (((v1 << 4) ^ (v1 >> 5)) + v1) ^ (sum + key[sum & 3]);
    }
    v[0] = v0; v[1] = v1;
}

int main(void) {
    uint32_t table[256];
    uint8_t buf[BUF_LEN];
    uint8_t big[20000];
    uint32_t key[4];
    uint32_t v[2];
    uint32_t seed = 12345, crc;
    size_t i;
    int blk;
    crc32_table(table);
    put_u64(table[1]); nl();
    put_u64(table[255]); nl();
    /* "123456789" -> the standard check values crc32 cbf43926, adler32 091e01de */
    for (i = 0; i < 9; i++) buf[i] = (uint8_t)('1' + i);
    put_u64(crc32_update(table, 0, buf, 9)); nl();
    put_u64(adler32(1, buf, 9)); nl();
    for (i = 0; i < BUF_LEN; i++) {
        seed = seed * 1103515245u + 12345u;
        buf[i] = (uint8_t)(seed >> 16);
    }
    put_u64(crc32_update(table, 0, buf, BUF_LEN)); nl();
    crc = crc32_update(table, 0, buf, 400);
    put_u64(crc32_update(table, crc, buf + 400, BUF_LEN - 400)); nl();
    put_u64(adler32(1, buf, BUF_LEN)); nl();
    for (i = 0; i < 20000; i++) big[i] = 0xff;
    put_u64(adler32(1, big, 20000)); nl();
    key[0] = 0x01234567u; key[1] = 0x89abcdefu; key[2] = 0xfedcba98u; key[3] = 0x76543210u;
    for (blk = 0; blk < 4; blk++) {
        v[0] = (uint32_t)blk * 0x11111111u;
        v[1] = ~v[0];
        xtea_encipher(XTEA_ROUNDS, v, key);
        put_u64(v[0]); nl(); put_u64(v[1]); nl();
        xtea_decipher(XTEA_ROUNDS, v, key);
        put_u64(v[0]); nl(); put_u64(v[1]); nl();
    }
    return 0;
}
