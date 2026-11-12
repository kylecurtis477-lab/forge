#!/usr/bin/env python3
"""Boot one QEMU configuration, check serial markers, and (optionally) OCR the framebuffer.

usage: boot.py NAME LOG TIMEOUT 'marker|marker|...' FBTEXT -- qemu args...
FBTEXT ('' to skip): text expected at the start of row 0 of the 8x16 framebuffer console. After
'KERNEL OK' appears on serial the harness takes a QEMU monitor screendump, decodes the first row
cell by cell against examples/kernel/font.fsi, then sends 'q' on COM1 so the kernel exits with 0x10.
Prints PASS/FAIL os/NAME like run_tests.sh, with the boot's wall time.

Timeouts (the old bare "qemu rc=-9"): a boot that runs out of TIMEOUT is classified by its serial output.
  - slow: output was still arriving in the last STALL seconds. The deadline is extended while output keeps
    coming, up to 2 x TIMEOUT, and the PASS/FAIL line says "SLOW" with the extra time used.
  - hang: no output for STALL seconds (the guest stopped making progress; load alone does not do that).
    The harness dumps the vCPU state ('info cpus', 'info registers -a') from the QEMU monitor into
    LOG.hangN (plus 64 words at each vCPU's rsp for a frame walk: RSP, or ESP before long mode, is read
    from 'info registers -a' and dumped with 'cpu N' + 'x/64gx 0xADDR', since HMP has no $rsp), then boots ONCE more for information.
    A hang is ALWAYS a FAIL, even when the retry passes: the line says "HANG", names the dump, the time
    the output stopped, the last line printed and the retry's result ("retry PASS"/"retry FAIL").
FORGE_BOOT_NO_RETRY=1 skips the informational retry."""
import os, re, socket, subprocess, sys, time

STALL = float(os.environ.get('FORGE_BOOT_STALL', '60'))  # seconds of serial silence that count as a hang

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
          ('s', 'sum /efi/forge/docs/big.bin'), ('s', 'captest'), ('s', 'chantest'), ('s', 'run sbtest'), ('s', 'run grtest &'), ('s', 'grants'), ('s', 'revoke grtest', 'grtest: halfway'), ('s', 'wait', 'grtest: cut off'), ('s', 'grants'), ('s', 'audit'), ('s', 'wxtest'), ('s', 'unsealed'), ('s', 'tampered'), ('s', 'forged'), ('s', 'linuxabi'), ('s', 'lxtest'), ('s', 'run lxtest'), ('s', 'run lxthreads'), ('s', 'run lxfs'), ('s', 'run lxexec'), ('s', 'run lxsig'), ('s', 'run lxsig term'), ('s', 'run busybox echo hello from busybox'), ('s', 'run busybox basename /a/b/forge-bb'), ('s', 'run busybox false'), ('s', 'run busybox seq -s , 3 5'), ('s', 'run busybox expr 6 + 7'), ('s', 'run busybox uname -s -m'), ('s', 'run busybox wc -c /efi/forge/docs/big.bin'), ('s', 'run busybox sha256sum /efi/forge/docs/big.bin'), ('s', 'run busybox cat /efi/forge/bin/cat'), ('s', 'run busybox ls /efi/forge/bin'), ('s', 'run busybox ls /efi/forge/docs'), ('s', 'run busybox printf %d-%s 42 ok'), ('s', 'run busybox pwd'), ('s', 'run busybox sleep 1'), ('s', 'run busybox sh -c exit${IFS}3'), ('s', 'run busybox sh -c exit${IFS}4</efi/forge/docs/big.bin'), ('s', 'run busybox tail -n 2 /efi/forge/docs/hello.sh'), ('s', 'run busybox sh /efi/forge/docs/hello.sh'), ('s', 'run busybox grep -c echo /efi/forge/docs/hello.sh'), ('s', 'capdeny'), ('s', 'badptr'), ('s', 'crash k 4'), ('k', 'hello Swap OK'), ('s', 'exit'),
          ('s', 'hello x'), ('s', 'bg 1 &'), ('s', 'hello z &'), ('s', 'kexec next'),
          ('k', 'hello after kexec'), ('s', 'wait'), ('s', 'exit')]
KEYS = {' ': 'spc', '/': 'slash', '-': 'minus', '.': 'dot', '\n': 'ret'}

def type_keys(line):
    s = socket.socket(socket.AF_UNIX); s.connect(mon)
    for ch in line + '\n':
        k = KEYS.get(ch) or (('shift-' + ch.lower()) if ch.isupper() else ch)
        s.sendall(f'sendkey {k}\n'.encode()); time.sleep(0.03)
    time.sleep(0.2); s.close()

def monitor(cmd, wait=1.0):
    try:
        s = socket.socket(socket.AF_UNIX); s.settimeout(wait); s.connect(mon)
        s.sendall(cmd.encode() + b'\n'); out, t = b'', time.time()
        while time.time() - t < wait:
            try:
                c = s.recv(65536)
                if not c: break
                out += c
            except socket.timeout:
                break
        s.close()
        t = re.sub(r'\x1b\[[0-9]*[A-Za-z]', '', out.decode('latin-1'))
        return '\n'.join(l for l in t.splitlines() if not l.startswith('(qemu) i'))
    except Exception as ex:
        return f'[monitor {cmd!r} failed: {ex}]'

def stack_dump(regs):
    """64 words at each vCPU's rsp, parsed from 'info registers -a' (QEMU's HMP has no $rsp register
    name: 'x/64gx $rsp' only printed "unknown register"). 'cpu N' selects the vCPU (its CR3) first."""
    out = []
    for cpu, rsp in re.findall(r'CPU#(\d+)\n(?:.*\n){0,2}?.*?(?:RSP|ESP)=([0-9a-f]{8,16})', regs):
        out.append(f'---- CPU#{cpu} stack at rsp=0x{rsp} ----\n' 
                   + '\n'.join(l for l in monitor(f'cpu {cpu}\nx/64gx 0x{rsp}', 1.0).splitlines()
                                if re.match(r'[0-9a-f]+: ', l)))
    return '\n'.join(out) if out else '[no RSP found in info registers -a]'

def last_line(text):
    ls = [l for l in text.splitlines() if l.strip()]
    return ls[-1][:120] if ls else ''

def boot(attempt):
    p = subprocess.Popen(['qemu-system-x86_64'] + qargs + ['-monitor', f'unix:{mon},server,nowait'],
                         stdin=subprocess.PIPE, stdout=subprocess.PIPE, stderr=subprocess.STDOUT)
    os.set_blocking(p.stdout.fileno(), False)
    buf, t0, shot_ok, fbmsg, sent = b'', time.time(), False, '', 0
    tlast, deadline, kind, dump = t0, t0 + float(tmo), '', ''
    while p.poll() is None:
        try:
            chunk = p.stdout.read()
        except Exception:
            chunk = None
        if chunk:
            buf += chunk; tlast = time.time()
        now = time.time()
        if now - tlast >= STALL:
            # Drain once more before HMP so a full stdio pipe cannot block QEMU, and so last_line is current.
            try:
                chunk = p.stdout.read()
            except Exception:
                chunk = None
            if chunk:
                buf += chunk; tlast = time.time(); now = time.time()
            if now - tlast >= STALL:
                kind = 'hang'
                dump = f'{log}.hang{attempt}'
                open(dump, 'w').write(f'hang: no serial output for {now - tlast:.0f}s (stopped at +{tlast - t0:.0f}s), '
                    f'load {open("/proc/loadavg").read().split()[0]}\nlast line: {last_line(buf.decode("latin-1"))}\n\n'
                    + monitor('info cpus') + '\n' + (regs := monitor('info registers -a', 2.0))
                    + '\n' + stack_dump(regs))
                break
        if now >= deadline:
            if now < t0 + 2 * float(tmo):
                deadline = min(now + STALL, t0 + 2 * float(tmo)); kind = 'slow'  # still progressing
            else:
                break
        # a third element holds the line back until that text has appeared (a background program's progress)
        if buf.count(b'sh$ ') > sent and sent < len(SCRIPT) and (len(SCRIPT[sent]) < 3 or SCRIPT[sent][2].encode() in buf):
            how, line = SCRIPT[sent][:2]; sent += 1
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
                    want = fbtext.split('^')  # 'A^B': row i-1 contains A and row i contains B
                    hit = [i for i, t in enumerate(rows) if want[-1] in t and (len(want) == 1 or (i > 0 and want[0] in rows[i - 1]))]
                    fbmsg = (f'{w}x{h} row{hit[-1]-1}="{rows[hit[-1]-1]}" ' if len(want) > 1 else '') + f'{w}x{h} row{hit[-1]}="{rows[hit[-1]]}"' if hit else f'{w}x{h} text not found, last row="{rows[-1]}"'
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
    el = time.time() - t0
    text = buf.decode('latin-1')
    why = ''
    if kind == 'hang':
        why = f'HANG: killed after {el:.0f}s, no serial output after +{tlast - t0:.0f}s, last line "{last_line(text)}", vCPU dump {dump}'
    elif rc == -9:
        why = f'SLOW: killed at the 2x limit ({el:.0f}s of {tmo}s), output still arriving'
    open(log, 'w').write(text + f'\n[fb] {fbmsg}\nrc={rc}\n[harness] {el:.1f}s {why}\n')
    if kind == 'hang':  # the retry rewrites LOG, so the hung boot's serial output goes into the dump too
        open(dump, 'a').write('\n---- serial output of the hung boot ----\n' + text)
    res = ''
    for m in [m for m in markers.split('|') if m]:
        if m not in text:
            res = f'FAIL os/{name} (missing: {m}, qemu rc={rc}; {el:.0f}s{"; " + why if why else ""})'; break
    if not res and fbtext and f'="{fbtext.split("^")[-1]}"' not in fbmsg:
        res = f'FAIL os/{name} (framebuffer: {fbmsg})'
    if not res and rc != 33:
        res = f'FAIL os/{name} (qemu rc={rc}; {el:.0f}s{"; " + why if why else ""})'
    if not res:
        slow = f' SLOW {el:.0f}s > {tmo}s timeout' if el > float(tmo) else f' {el:.0f}s'
        res = f'PASS os/{name}' + (f' [fb {fbmsg}]' if fbtext else '') + f' [{slow.strip()}]'
    return res, kind, why

def main():
    res, kind, why = boot(1)
    if kind == 'hang' and os.environ.get('FORGE_BOOT_NO_RETRY') != '1':
        res2, kind2, why2 = boot(2)
        r2 = 'PASS' if res2.startswith('PASS') else 'FAIL'
        print(f'FLAKY os/{name}: first boot {why}; informational retry {r2}')
        assert res.startswith('FAIL')  # the hang result itself, whatever the retry did
        res += f' [retry {r2}; a HANG always fails, dump kept in {log}.hang1]'
    print(res)

main()
