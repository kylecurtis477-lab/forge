#!/usr/bin/env python3
"""Build a GPT disk image: protective MBR + primary/backup GPT with
  1: EFI System Partition (FAT image given on the command line)
  2: Microsoft basic data (empty placeholder, simulates an existing Windows install)
  3: Linux filesystem   (empty placeholder, simulates an existing Linux install)
Used to test that the Forge EFI app / kernel boot from a shared ESP without owning the disk.
usage: mkgpt.py out.img esp.fat [size_mib]
FORGE_GUID_SEED=N (testing only): GUID k (k = 0..2 partitions, 3 disk) is bytes_le = pack('<QQ', N, k)
instead of uuid4, so the image is reproducible (tests/mkgpt compares against examples/efi/mkgpt.forge)."""
import os, sys, struct, zlib, uuid
def newguid(k):
    seed = os.environ.get('FORGE_GUID_SEED')
    if seed is None: return uuid.uuid4()
    return uuid.UUID(bytes_le=struct.pack('<QQ', int(seed), k))
SEC = 512
out, esp = sys.argv[1], sys.argv[2]
size = int(sys.argv[3]) if len(sys.argv) > 3 else 128
nsec = size * 1024 * 1024 // SEC
espdata = open(esp, 'rb').read()
esp_secs = (len(espdata) + SEC - 1) // SEC
ESP = uuid.UUID('C12A7328-F81F-11D2-BA4B-00A0C93EC93B')
MSDATA = uuid.UUID('EBD0A0A2-B9E5-4433-87C0-68B6B72699C7')
LINUX = uuid.UUID('0FC63DAF-8483-4772-8E79-3D69D8477DE4')
first = 2048
parts = []
lba = first
for (t, name, n) in [(ESP, 'EFI system partition', esp_secs), (MSDATA, 'Basic data partition', 16 * 2048), (LINUX, 'linux-root', 16 * 2048)]:
    parts.append((t, newguid(len(parts)), lba, lba + n - 1, name))
    lba += (n + 2047) // 2048 * 2048
last_usable = nsec - 34
assert lba <= last_usable
ents = b''
for (t, u, s, e, name) in parts:
    ents += t.bytes_le + u.bytes_le + struct.pack('<QQQ', s, e, 0) + name.encode('utf-16-le').ljust(72, b'\0')
ents = ents.ljust(128 * 128, b'\0')
ecrc = zlib.crc32(ents)
disk_guid = newguid(3)
def hdr(my, alt, entlba):
    h = struct.pack('<8sIIIIQQQQ16sQIII', b'EFI PART', 0x10000, 92, 0, 0, my, alt, 34, last_usable, disk_guid.bytes_le, entlba, 128, 128, ecrc)
    h = h[:16] + struct.pack('<I', zlib.crc32(h)) + h[20:]
    return h.ljust(SEC, b'\0')
img = bytearray(nsec * SEC)
mbr = bytearray(SEC)
mbr[446:462] = struct.pack('<BBBBBBBBII', 0, 0, 2, 0, 0xEE, 0xFF, 0xFF, 0xFF, 1, min(nsec - 1, 0xFFFFFFFF))
mbr[510:512] = b'\x55\xaa'
img[0:SEC] = mbr
img[SEC:2 * SEC] = hdr(1, nsec - 1, 2)
img[2 * SEC:34 * SEC] = ents
img[(nsec - 33) * SEC:(nsec - 1) * SEC] = ents
img[(nsec - 1) * SEC:] = hdr(nsec - 1, 1, nsec - 33)
s = parts[0][2] * SEC
img[s:s + len(espdata)] = espdata
open(out, 'wb').write(img)
