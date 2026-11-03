/* SysV stack arguments (more than 6 integer words), both directions */
long c_add8(long a,long b,long c,long d,long e,long f,long g,long h){return a+2*b+3*c+4*d+5*e+6*f+7*g+8*h;}
/* odd count (7): the caller must pad so rsp is 16-aligned at the call; rbp%16 == 0 inside */
long c_align7(long a,long b,long c,long d,long e,long f,long g){
  long al = (long)__builtin_frame_address(0) % 16;
  return al * 1000000 + a+b+c+d+e+f+g;}
/* narrow stack args: the caller leaves upper bits unspecified, so write garbage high bits ourselves via asm-free cast */
extern long forge_add8(long,long,long,long,long,long,long,long);
extern long forge_narrow(long,long,long,long,long,long,unsigned char,short,unsigned int,long);
long c_calls8(void){return forge_add8(1,2,3,4,5,6,7,8);}
long c_calls_narrow(void){return forge_narrow(1,2,3,4,5,6,200,-3,4000000000u,10);}
