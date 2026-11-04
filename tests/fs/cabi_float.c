/* SysV floats (11.2): SSE class in xmm0-7, returns in xmm0, mixed with integer registers, 9th double on the stack */
double c_mix(long a, double x, long b, float y) { return a * 100 + x * 10 + b + y; }
double c_nine(double a, double b, double c, double d, double e, double f, double g, double h, double i) {
  return a + b * 2 + c * 3 + d * 4 + e * 5 + f * 6 + g * 7 + h * 8 + i * 1000;
}
float c_half(float x) { return x / 2; }
extern double forge_scale(double, long, float);
long c_calls(void) { return (long)(forge_scale(1.5, 4, 0.25f) * 1000); }
