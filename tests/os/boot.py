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

def decode_row(path, n, row=0):
    w, h, px = read_ppm(path)
    if n is None: n = w // 8
    f = font()
    out = ''
    for cx in range(n):
        bits = []
        for r in range(16):
            b = 0
            for x in range(8):
                o = ((row * 16 + r) * w + cx * 8 + x) * 3
                b = (b << 1) | (1 if px[o] + px[o+1] + px[o+2] > 384 else 0)
            bits.append(b)
        ch = '?'
        for g in range(95):
            if f[g*16:(g+1)*16] == bits:
                ch = chr(32 + g); break
        out += ch
    return w, h, out

# Interactive shell script: after the n-th "sh$ " prompt, type line n. 'k' lines are typed on the
# PS/2 keyboard through the QEMU monitor (sendkey), 's' lines arrive on COM1 (serial RX interrupt).
SCRIPT = [('k', 'ls'), ('s', 'cat /efi/forge/docs/Long File Name Notes.txt'), ('k', 'forktest'),
          ('s', 'hello a b'), ('k', 'nosuch'), ('k', 'ls /EFI/Forge'), ('s', 'crash 40'),
          ('s', 'sum /efi/forge/docs/big.bin'), ('s', 'captest'), ('s', 'chantest'), ('s', 'run sbtest'), ('s', 'wxtest'), ('s', 'exit'),
          ('s', 'hello x'), ('s', 'bg 1 &'), ('s', 'hello z &'), ('s', 'kexec next'),
          ('k', 'hello after kexec'), ('s', 'wait'), ('s', 'exit')]
KEYS = {' ': 'spc', '/': 'slash', '-': 'minus', '.': 'dot', '\n': 'ret'}

def type_keys(line):
    s = socket.socket(socket.AF_UNIX); s.connect(mon)
    for ch in line + '\n':
        k = KEYS.get(ch) or (('shift-' + ch.lower()) if ch.isupper() else ch)
        s.sendall(f'sendkey {k}\n'.encode()); time.sleep(0.03)
    time.sleep(0.2); s.close()

def main():
    p = subprocess.Popen(['qemu-system-x86_64'] + qargs + ['-monitor', f'unix:{mon},server,nowait'],
                         stdin=subprocess.PIPE, stdout=subprocess.PIPE, stderr=subprocess.STDOUT)
    os.set_blocking(p.stdout.fileno(), False)
    buf, t0, shot_ok, fbmsg, sent = b'', time.time(), False, '', 0
    while p.poll() is None and time.time() - t0 < float(tmo):
        try:
            chunk = p.stdout.read()
        except Exception:
            chunk = None
        if chunk:
            buf += chunk
        if buf.count(b'sh$ ') > sent and sent < len(SCRIPT):
            how, line = SCRIPT[sent]; sent += 1
            try:
                if how == 'k': type_keys(line)
                else: p.stdin.write(line.encode() + b'\r'); p.stdin.flush()
            except Exception as ex:
                buf += f'[harness: send failed: {ex}]'.encode()
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
                    w, h, _ = read_ppm(shot)
                    rows = [decode_row(shot, None, r)[2].rstrip() for r in range(h // 16)]
                    hit = [i for i, t in enumerate(rows) if fbtext in t]
                    fbmsg = f'{w}x{h} row{hit[-1]}="{rows[hit[-1]]}"' if hit else f'{w}x{h} text not found, last row="{rows[-1]}"'
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
    if fbtext and f'="{fbtext}"' not in fbmsg:
        print(f'FAIL os/{name} (framebuffer: {fbmsg})'); return
    if rc != 33:
        print(f'FAIL os/{name} (qemu rc={rc})'); return
    print(f'PASS os/{name}' + (f' [fb {fbmsg}]' if fbtext else ''))

main()
