# memimg.py A.elf B.elf: the two executables load identical bytes at identical addresses (PT_LOAD by
# PT_LOAD: vaddr, size, flags, contents with .bss zero-fill) and have the same entry point
import sys,struct
def segs(p):
    d=open(p,'rb').read()
    phoff,=struct.unpack_from('<Q',d,0x20); n,=struct.unpack_from('<H',d,0x38)
    ent,=struct.unpack_from('<Q',d,0x18)
    m={}
    for i in range(n):
        t,f,off,va,pa,fs,ms,al=struct.unpack_from('<IIQQQQQQ',d,phoff+56*i)
        if t!=1 or off==0: continue
        b=d[off:off+fs]+b'\0'*(ms-fs)
        m[va]=(b,pa,f)
    return ent,m
e1,a=segs(sys.argv[1]); e2,b=segs(sys.argv[2])
ok = e1==e2
def flat(m):
    r={}
    for va,(bb,pa,f) in m.items():
        r[va]=(bb,pa-va,f)
    return r
A=flat(a);B=flat(b)
# compare bytes by address
def img(m):
    out=[]
    for va in sorted(m): out.append((va,m[va][0],m[va][1],m[va][2]))
    return out
ia=img(A); ib=img(B)
if [(x[0],len(x[1]),x[2],x[3]) for x in ia]!=[(x[0],len(x[1]),x[2],x[3]) for x in ib]:
    print("SEGS differ", [(hex(x[0]),hex(len(x[1])),x[3]) for x in ia], [(hex(x[0]),hex(len(x[1])),x[3]) for x in ib]); ok=False
else:
    for x,y in zip(ia,ib):
        if x[1]!=y[1]:
            i=next(i for i in range(len(x[1])) if x[1][i]!=y[1][i]); print("bytes differ at",hex(x[0]+i)); ok=False
print("SAME" if ok else "DIFF", hex(e1),hex(e2))
