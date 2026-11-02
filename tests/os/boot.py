#!/usr/bin/env python3
"""Boot one QEMU configuration, check serial markers, and (optionally) OCR the framebuffer.

usage: boot.py NAME LOG TIMEOUT 'marker|marker|...' FBTEXT -- qemu args...
FBTEXT ('' to skip): text expected at the start of row 0 of the 8x16 framebuffer console. After
'KERNEL OK' appears on serial the harness takes a QEMU monitor screendump, decodes the first row
cell by cell against examples/kernel/font.fsi, then sends 'q' on COM1 so the kernel exits with 0x10.
Prints PASS/FAIL os/NAME like run_tests.sh."""
import os, re, socket, subprocess, sys, time

name, log, tmo, markers, fbtext = sys.argv[1:6]
qargs = sys.argv[sys.argv.index('--') + 1:]
root = os.path.dirname(os.path.dirname(os.path.dirname(os.path.abspath(__file__))))
mon = f'/tmp/forge-mon-{os.getpid()}.sock'
shot = os.path.join(os.path.dirname(os.path.abspath(log)), f'{name}.ppm')

def font():
    src = open(os.path.join(root, 'examples/kernel/font.fsi')).read()
    body = src[src.index('[', src.index('=')) + 1:src.rindex(']')]
    return [int(x, 0) for x in body.replace('\n', ' ').split(',') if x.strip()]

def read_ppm(path):
    d = open(path, 'rb').read()
    toks, i = [], 0
    while len(toks) < 4:
        while d[i:i+1].isspace(): i += 1
        j = i
        while not d[j:j+1].isspace(): j += 1
        toks.append(d[i:j]); i = j
    w, h = int(toks[1]), int(toks[2])
    return w, h, d[i+1:]

def decode_row0(path, n):
    w, h, px = read_ppm(path)
    f = font()
    out = ''
    for cx in range(n):
        bits = []
        for r in range(16):
            b = 0
            for x in range(8):
                o = (r * w + cx * 8 + x) * 3
                b = (b << 1) | (1 if px[o] + px[o+1] + px[o+2] > 384 else 0)
            bits.append(b)
        ch = '?'
        for g in range(95):
            if f[g*16:(g+1)*16] == bits:
                ch = chr(32 + g); break
        out += ch
    return w, h, out

def main():
    p = subprocess.Popen(['qemu-system-x86_64'] + qargs + ['-monitor', f'unix:{mon},server,nowait'],
                         stdin=subprocess.PIPE, stdout=subprocess.PIPE, stderr=subprocess.STDOUT)
    os.set_blocking(p.stdout.fileno(), False)
    buf, t0, shot_ok, fbmsg = b'', time.time(), False, ''
    while p.poll() is None and time.time() - t0 < float(tmo):
        try:
            chunk = p.stdout.read()
        except Exception:
            chunk = None
        if chunk:
            buf += chunk
        if not shot_ok and b'KERNEL OK' in buf:
            shot_ok = True
            if fbtext:
                try:
                    if os.path.exists(shot): os.remove(shot)
                    s = socket.socket(socket.AF_UNIX); s.connect(mon)
                    s.sendall(f'screendump {shot}\n'.encode()); time.sleep(0.5)
                    for _ in range(50):
                        if os.path.exists(shot) and os.path.getsize(shot) > 0: break
                        time.sleep(0.1)
                    time.sleep(0.3); s.close()
                    w, h, row = decode_row0(shot, len(fbtext))
                    fbmsg = f'{w}x{h} row0="{row}"'
                except Exception as ex:
                    fbmsg = f'screendump failed: {ex}'
            try:
                p.stdin.write(b'q'); p.stdin.flush()
            except Exception:
                pass
        time.sleep(0.05)
    if p.poll() is None:
        p.kill(); p.wait()
    try:
        buf += p.stdout.read() or b''
    except Exception:
        pass
    rc = p.returncode
    text = buf.decode('latin-1')
    open(log, 'w').write(text + f'\n[fb] {fbmsg}\nrc={rc}\n')
    for m in [m for m in markers.split('|') if m]:
        if m not in text:
            print(f'FAIL os/{name} (missing: {m}, qemu rc={rc})'); return
    if fbtext and f'row0="{fbtext}"' not in fbmsg:
        print(f'FAIL os/{name} (framebuffer: {fbmsg})'); return
    if rc != 33:
        print(f'FAIL os/{name} (qemu rc={rc})'); return
    print(f'PASS os/{name}' + (f' [fb {fbmsg}]' if fbtext else ''))

main()
