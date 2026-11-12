#!/bin/sh
# tests/os/hostcap.sh PID: host-side view of a QEMU process, taken by tests/os/boot.forge when a boot stalls and
# BEFORE any HMP command (the monitor needs QEMU's global lock, and earlier dumps showed the guest resuming right
# after it). Per thread: name, state, CPU ticks used over a 1 s window (two /proc samples), wchan, the syscall it
# is in (/proc/TID/syscall: "running" or the syscall number, 202 = futex), the box CPU it last ran on, and its
# kernel stack when sudo -n is allowed; gdb backtraces of every thread when gdb is installed. Also the box view
# over the same 2 s window: steal ticks gained per box CPU (the box is itself a VM; steal = its hypervisor did
# not run that vCPU), memory/cpu/io pressure, load, other QEMU processes and the top CPU users.
# Prints only; never signals QEMU.
P=$1
[ -d /proc/$P ] || { echo "hostcap: no process $P"; exit 0; }
cpu() { sed 's/.*) //' /proc/$P/task/$1/stat 2>/dev/null | awk '{print $12 + $13}'; }
steal() { grep '^cpu[0-9]' /proc/stat | awk '{printf "%s ", $9}'; }
echo "---- host: QEMU pid $P, $(ls /proc/$P/task | wc -l) threads, load $(cut -d' ' -f1-3 /proc/loadavg) ----"
for t in /proc/$P/task/*; do echo "${t##*/} $(cpu ${t##*/})"; done > /tmp/hostcap.$$
s0=$(steal)
sleep 2
s1=$(steal)
while read -r tid c0; do
  c1=$(cpu $tid); [ -n "$c1" ] || continue
  st=$(sed 's/.*) //' /proc/$P/task/$tid/stat | cut -d' ' -f1)
  sc=$(cut -d' ' -f1 /proc/$P/task/$tid/syscall 2>/dev/null)
  on=$(sed 's/.*) //' /proc/$P/task/$tid/stat | cut -d' ' -f37)
  printf '%-8s %-16s state %s cpu %3s ticks/2s  on box cpu %-2s wchan %-24s syscall %s\n' "$tid" "$(cat /proc/$P/task/$tid/comm)" "$st" "$((c1 - c0))" "$on" "$(cat /proc/$P/task/$tid/wchan 2>/dev/null)" "$sc"
done < /tmp/hostcap.$$
rm -f /tmp/hostcap.$$
set -- $s0; a="$*"; set -- $s1; i=0; d=""; for x in $a; do i=$((i + 1)); eval y=\${$i}; d="$d $((y - x))"; done
echo "box steal ticks gained per cpu over the 2 s:$d"
echo "pressure: mem $(grep full /proc/pressure/memory | cut -d' ' -f2) cpu $(grep some /proc/pressure/cpu | cut -d' ' -f2) io $(grep full /proc/pressure/io | cut -d' ' -f2); load $(cat /proc/loadavg)"
echo "QEMU processes: $(pgrep -cx qemu-system-x86) ($(pgrep -d, -x qemu-system-x86))"
echo "top CPU users:"; ps -eo pid,pcpu,etime,comm --sort=-pcpu | head -6
if sudo -n true 2>/dev/null; then
  for t in /proc/$P/task/*; do echo "-- kernel stack of ${t##*/} ($(cat $t/comm)):"; sudo -n cat $t/stack 2>/dev/null | head -12; done
fi
if command -v gdb >/dev/null 2>&1; then
  timeout 20 gdb -p $P -batch -ex "thread apply all bt 12" 2>/dev/null | grep -E '^Thread|^#' | head -200
else
  echo "(gdb not installed: no user-space backtraces)"
fi
