# ---- Forge runtime (x86-64 Linux, no libc). Stack args: last arg at 8(%rsp). ----
# Object layout: 8-byte header at ptr-8 = tag<<56 | aux.  tags: 0 list [len,cap,data]  1 str (aux=len, NUL-terminated)
#   2 object (aux=class id, fields at 0..)  3 dict  4 raw bytes (aux=size)
# Heap: 64 GiB MAP_NORESERVE bump arena at 2^45; image linked at 2^44, so ints in [0,2^44) never alias pointers.
.weak f___getidx, f___setidx, f___add, f___eq, f___truth, f___err, f___uncaught, f___gstart
.text
rt_init:
    # rdi = initial %rsp (argc, argv...)
    mov %rdi, rt_stk0(%rip)
    mov (%rdi), %rax
    mov %rax, rt_argcv(%rip)
    lea 8(%rdi), %rax
    mov %rax, rt_argvp(%rip)
    mov $9, %eax
    mov $0x200000000000, %rdi
    mov $0x1000000000, %rsi
    mov $3, %edx
    mov $0x4022, %r10d
    mov $-1, %r8
    xor %r9d, %r9d
    syscall
    mov %rax, rt_hp(%rip)
    mov %rax, rt_hlo(%rip)
    mov %rax, %rdi
    movabs $0x1000000000, %rsi
    mov $14, %edx
    mov $28, %eax
    syscall
    mov $9, %eax
    xor %edi, %edi
    mov $0x800000, %esi
    mov $3, %edx
    mov $0x4022, %r10d
    mov $-1, %r8
    xor %r9d, %r9d
    syscall
    mov %rax, rt_pt(%rip)
    mov $9, %eax
    xor %edi, %edi
    mov $0x40000000, %esi
    mov $3, %edx
    mov $0x4022, %r10d
    mov $-1, %r8
    xor %r9d, %r9d
    syscall
    mov %rax, rt_ms(%rip)
    lea rt_segv(%rip), %rax
    mov %rax, rt_sigact(%rip)
    mov %rax, rt_sigact+16(%rip)
    movq $0x04000000, rt_sigact+8(%rip)
    mov $13, %eax
    mov $11, %edi
    lea rt_sigact(%rip), %rsi
    xor %edx, %edx
    mov $8, %r10d
    syscall
    ret
rt_segv:
    call rt_flush
    mov $2, %edi
    lea rt_segv_msg(%rip), %rsi
    mov $61, %edx
    call rt_write_r
    mov $1, %edi
    mov $60, %eax
    syscall
rt_new_r:
    # rdi = payload bytes, rsi = header -> rax = object (8-aligned, zeroed); clobbers rdx
    # GC heap: 64 KiB pages; size classes 1..32 (16-byte steps), 33..38 (1K..32K), large runs above.
    push %rcx
    lea 23(%rdi), %rcx
    shr $4, %rcx
    cmp $32, %rcx
    ja 5f
0:  lea rt_fl(%rip), %rdx
    lea (%rdx,%rcx,8), %rdx
    mov (%rdx), %rax
    test %rax, %rax
    jz 6f
    mov 8(%rax), %rcx
    mov %rcx, (%rdx)
    movq $0, 8(%rax)
    mov %rsi, (%rax)
    add $8, %rax
    add %rdi, rt_inuse(%rip)
    pop %rcx
    ret
6:  call rt_refill
    jmp 0b
5:  lea 8(%rdi), %rdx
    mov $1024, %eax
    mov $33, %ecx
7:  cmp %rax, %rdx
    jbe 0b
    shl $1, %rax
    inc %ecx
    cmp $38, %ecx
    jbe 7b
    pop %rcx
    jmp rt_new_large
rt_saveall:
    # not called; documentation of save order used by rt_refill/rt_new_large/rt_collect
rt_refill:
    # rcx = class; refill rt_fl[class] (may collect). preserves all GPRs
    push %rax
    push %rbx
    push %rcx
    push %rdx
    push %rsi
    push %rdi
    push %rbp
    push %r8
    push %r9
    push %r10
    push %r11
    push %r12
    push %r13
    push %r14
    push %r15
    mov %rcx, %rbx
    mov rt_inuse(%rip), %rax
    cmp rt_gcnext(%rip), %rax
    jb 1f
    push %rbx
    call rt_gc_core
    pop %rbx
    lea rt_fl(%rip), %rax
    cmpq $0, (%rax,%rbx,8)
    jne 9f
1:  mov $1, %edi
    call rt_getpages
    mov %rax, %r8
    # stride
    mov %rbx, %r12
    shl $4, %r12
    cmp $32, %rbx
    jbe 2f
    lea -33(%rbx), %rcx
    mov $1024, %r12d
    shl %cl, %r12
2:  mov %r8, %rax
    sub rt_hlo(%rip), %rax
    shr $16, %rax
    mov rt_pt(%rip), %rdx
    mov %r12, (%rdx,%rax,8)
    lea 65536(%r8), %r9
    mov %r8, %r10
    movabs $0xFF00000000000000, %rax
3:  mov %rax, (%r10)
    lea (%r10,%r12), %r11
    lea (%r11,%r12), %rcx
    cmp %r9, %rcx
    ja 4f
    mov %r11, 8(%r10)
    mov %r11, %r10
    jmp 3b
4:  movq $0, 8(%r10)
    lea rt_fl(%rip), %rax
    mov %r8, (%rax,%rbx,8)
9:  pop %r15
    pop %r14
    pop %r13
    pop %r12
    pop %r11
    pop %r10
    pop %r9
    pop %r8
    pop %rbp
    pop %rdi
    pop %rsi
    pop %rdx
    pop %rcx
    pop %rbx
    pop %rax
    ret
rt_getpages:
    # rdi = npages -> rax = page-aligned zeroed run (first-fit from free runs, else bump); clobbers rcx,rdx,rsi,r8
    lea rt_lfree(%rip), %rcx
1:  mov (%rcx), %rax
    test %rax, %rax
    jz 5f
    mov 8(%rax), %rdx
    cmp %rdi, %rdx
    jae 2f
    mov %rax, %rcx
    jmp 1b
2:  je 3f
    mov %rdi, %rsi
    shl $16, %rsi
    add %rax, %rsi
    mov (%rax), %r8
    mov %r8, (%rsi)
    sub %rdi, %rdx
    mov %rdx, 8(%rsi)
    mov %rsi, (%rcx)
    jmp 4f
3:  mov (%rax), %rsi
    mov %rsi, (%rcx)
4:  movq $0, (%rax)
    movq $0, 8(%rax)
    ret
5:  mov rt_hp(%rip), %rax
    mov %rdi, %rdx
    shl $16, %rdx
    add %rax, %rdx
    movabs $0x1000000000, %rsi
    add rt_hlo(%rip), %rsi
    cmp %rsi, %rdx
    ja rt_oom
    mov %rdx, rt_hp(%rip)
    ret
rt_oom:
    call rt_flush
    mov $2, %edi
    lea rt_oom_msg(%rip), %rsi
    mov $20, %edx
    call rt_write_r
    mov $1, %edi
    mov $60, %eax
    syscall
rt_new_large:
    # rdi = payload, rsi = header -> rax; clobbers rdx
    push %rbx
    push %rcx
    push %rsi
    push %rdi
    push %rbp
    push %r8
    push %r9
    push %r10
    push %r11
    push %r12
    push %r13
    push %r14
    push %r15
    mov rt_inuse(%rip), %rax
    cmp rt_gcnext(%rip), %rax
    jb 1f
    call rt_gc_core
1:  mov 72(%rsp), %rdi
    lea 65543(%rdi), %rdi
    shr $16, %rdi
    mov %rdi, %rbx
    call rt_getpages
    mov %rax, %r8
    sub rt_hlo(%rip), %rax
    shr $16, %rax
    mov rt_pt(%rip), %rdx
    mov %rbx, %rcx
    shl $16, %rcx
    add %rcx, rt_inuse(%rip)
    mov %rcx, (%rdx,%rax,8)
    mov $1, %ecx
2:  cmp %rbx, %rcx
    jae 3f
    lea (%rax,%rcx), %rsi
    mov %rcx, %r9
    neg %r9
    mov %r9, (%rdx,%rsi,8)
    inc %rcx
    jmp 2b
3:  mov 80(%rsp), %rsi
    mov %rsi, (%r8)
    lea 8(%r8), %rax
    pop %r15
    pop %r14
    pop %r13
    pop %r12
    pop %r11
    pop %r10
    pop %r9
    pop %r8
    pop %rbp
    pop %rdi
    pop %rsi
    pop %rcx
    pop %rbx
    ret
rt_collect:
    # full collection, preserves all GPRs except rax
    push %rbx
    push %rcx
    push %rdx
    push %rsi
    push %rdi
    push %rbp
    push %r8
    push %r9
    push %r10
    push %r11
    push %r12
    push %r13
    push %r14
    push %r15
    call rt_gc_core
    pop %r15
    pop %r14
    pop %r13
    pop %r12
    pop %r11
    pop %r10
    pop %r9
    pop %r8
    pop %rbp
    pop %rdi
    pop %rsi
    pop %rdx
    pop %rcx
    pop %rbx
    ret
rt_gc_mark:
    # rdi = candidate word; pushes newly marked slot onto mark stack (%r15); clobbers rax,rcx,rdx,rsi,r8
    mov rt_hlo(%rip), %rax
    cmp %rax, %rdi
    jb 9f
    cmp rt_hp(%rip), %rdi
    jae 9f
    mov %rdi, %rcx
    sub %rax, %rcx
    shr $16, %rcx
    mov rt_pt(%rip), %rdx
    mov (%rdx,%rcx,8), %rsi
    test %rsi, %rsi
    jz 9f
    jns 1f
    add %rsi, %rcx
    mov (%rdx,%rcx,8), %rsi
1:  mov %rcx, %r8
    shl $16, %r8
    add %rax, %r8
    cmp $65536, %rsi
    jae 2f
    mov %rdi, %rax
    sub %r8, %rax
    xor %edx, %edx
    div %rsi
    imul %rsi, %rax
    lea (%rax,%rsi), %rdx
    cmp $65536, %rdx
    ja 9f
    add %rax, %r8
2:  mov (%r8), %rax
    mov %rax, %rcx
    shr $56, %rcx
    cmp $0xFF, %ecx
    je 9f
    bts $55, %rax
    jc 9f
    mov %rax, (%r8)
    mov %r8, (%r15)
    add $8, %r15
9:  ret
rt_gc_core:
    # mark from stack [rsp, rt_stk0), data [rt_dlo, rt_dhi), rt_exc_val; then sweep
    incq rt_ngc(%rip)
    mov rt_ms(%rip), %r15
    mov %rsp, %r14
    mov rt_gcur(%rip), %rdi
    test %rdi, %rdi
    jz 1f
    # inside a generator: its stack is a heap object reached via rt_gcur; scan the main stack from where it was left
    call rt_gc_mark
    mov rt_main_sp(%rip), %r14
1:  cmp rt_stk0(%rip), %r14
    jae 2f
    mov (%r14), %rdi
    call rt_gc_mark
    add $8, %r14
    jmp 1b
2:  lea rt_dlo(%rip), %r14
3:  lea rt_dhi(%rip), %rax
    cmp %rax, %r14
    jae 4f
    mov (%r14), %rdi
    call rt_gc_mark
    add $8, %r14
    jmp 3b
4:  mov rt_exc_val(%rip), %rdi
    call rt_gc_mark
5:  cmp rt_ms(%rip), %r15
    je 10f
    sub $8, %r15
    mov (%r15), %r14
    movzbl 7(%r14), %eax
    cmp $1, %eax
    je 5b
    cmp $5, %eax
    je 5b
    mov %r14, %rax
    sub rt_hlo(%rip), %rax
    shr $16, %rax
    mov rt_pt(%rip), %rdx
    mov (%rdx,%rax,8), %r13
    add %r14, %r13
    lea 8(%r14), %r12
6:  cmp %r13, %r12
    jae 5b
    mov (%r12), %rdi
    call rt_gc_mark
    add $8, %r12
    jmp 6b
10: # sweep: r12 = page base, r13 = page index, r11 = live bytes
    lea rt_fl(%rip), %rdi
    mov $40, %ecx
    xor %eax, %eax
    rep stosq
    xor %r11d, %r11d
    mov rt_hlo(%rip), %r12
    xor %r13d, %r13d
11: cmp rt_hp(%rip), %r12
    jae 30f
    mov rt_pt(%rip), %rdx
    mov (%rdx,%r13,8), %r10
    test %r10, %r10
    jg 12f
    add $65536, %r12
    inc %r13
    jmp 11b
12: cmp $65536, %r10
    jb 20f
    # large run of r10 bytes
    mov %r10, %r9
    shr $16, %r9
    mov (%r12), %rax
    btr $55, %rax
    jnc 13f
    mov %rax, (%r12)
    add %r10, %r11
    jmp 14f
13: mov %r12, %rdi
    mov %r10, %rsi
    mov %r9, %rcx
    call rt_freerun
14: add %r10, %r12
    add %r9, %r13
    jmp 11b
20: # small page, stride r10: count live
    lea 65536(%r12), %r9
    xor %r8d, %r8d
    mov %r12, %rsi
21: lea (%rsi,%r10), %rax
    cmp %r9, %rax
    ja 22f
    mov (%rsi), %rax
    bt $55, %rax
    jnc 211f
    mov %rax, %rcx
    shr $56, %rcx
    cmp $0xFF, %ecx
    je 211f
    inc %r8
211: add %r10, %rsi
    jmp 21b
22: test %r8, %r8
    jnz 23f
    mov %r12, %rdi
    mov $65536, %esi
    mov $1, %ecx
    call rt_freerun
    jmp 29f
23: # class index of stride
    mov %r10, %rbx
    shr $4, %rbx
    cmp $512, %r10
    jbe 24f
    bsr %r10, %rbx
    add $23, %rbx
24: lea rt_fl(%rip), %rax
    lea (%rax,%rbx,8), %rbx
    mov %r12, %rsi
25: lea (%rsi,%r10), %rax
    cmp %r9, %rax
    ja 29f
    mov (%rsi), %rax
    mov %rax, %rcx
    shr $56, %rcx
    cmp $0xFF, %ecx
    je 27f
    btr $55, %rax
    jnc 26f
    mov %rax, (%rsi)
    add %r10, %r11
    jmp 28f
26: mov %rsi, %rdi
    mov %r10, %rcx
    shr $3, %rcx
    xor %eax, %eax
    rep stosq
    movabs $0xFF00000000000000, %rax
    mov %rax, (%rsi)
27: mov (%rbx), %rax
    mov %rax, 8(%rsi)
    mov %rsi, (%rbx)
28: add %r10, %rsi
    jmp 25b
29: add $65536, %r12
    inc %r13
    jmp 11b
30: mov %r11, rt_inuse(%rip)
    add %r11, %r11
    cmp $0x2000000, %r11
    jae 31f
    mov $0x2000000, %r11d
31: mov %r11, rt_gcnext(%rip)
    ret
rt_freerun:
    # rdi = run base, rsi = bytes, rcx = npages, r13 = first page index: clear page table, madvise, push on free list
    push %rcx
    push %r11
    mov rt_pt(%rip), %rdx
    lea (%rdx,%r13,8), %rdx
1:  movq $0, (%rdx)
    add $8, %rdx
    dec %rcx
    jnz 1b
    push %rdi
    mov $4, %edx
    mov $28, %eax
    syscall
    pop %rdi
    pop %r11
    pop %rcx
    mov rt_lfree(%rip), %rax
    mov %rax, (%rdi)
    mov %rcx, 8(%rdi)
    mov %rdi, rt_lfree(%rip)
    ret
rt_mkstr_r:
    # rdi = length -> rax = string with header and NUL; clobbers rdx, rsi
    push %rdi
    mov %rdi, %rsi
    bts $56, %rsi
    inc %rdi
    call rt_new_r
    pop %rdi
    movb $0, (%rax,%rdi)
    ret
rt_new:
    # (nbytes, tag, aux)
    mov 24(%rsp), %rdi
    mov 16(%rsp), %rsi
    shl $56, %rsi
    or 8(%rsp), %rsi
    jmp rt_new_r
rt_alloc:
    mov 8(%rsp), %rdi
    mov %rdi, %rsi
    bts $58, %rsi
    jmp rt_new_r
rt_list:
    # (n) -> zero-filled list of length n
    mov $24, %edi
    xor %esi, %esi
    call rt_new_r
    mov %rax, %r8
    mov 8(%rsp), %rcx
    mov %rcx, (%r8)
    cmp $4, %rcx
    jge 1f
    mov $4, %ecx
1:  mov %rcx, 8(%r8)
    lea (,%rcx,8), %rdi
    mov %rdi, %rsi
    bts $58, %rsi
    push %rcx
    call rt_new_r
    pop %rcx
    mov %rax, 16(%r8)
    # heap memory is always handed out zeroed (fresh pages, madvised runs, swept slots)
    mov %r8, %rax
    ret
rt_tag_r:
    # rdi = value -> rax = tag (6 for bools), or -1 if not a heap/data object
    mov %rdi, %rax
    or $1, %rax
    btr $62, %rax
    jnc 0f
    cmp $1, %rax
    jne 7f
    mov $6, %eax
    ret
7:  cmp $3, %rax
    jne 0f
    mov $7, %eax
    ret
0:  test $7, %dil
    jnz 9f
    cmp rt_hlo(%rip), %rdi
    jbe 1f
    cmp rt_hp(%rip), %rdi
    jae 9f
8:  movzbq -1(%rdi), %rax
    ret
1:  lea rt_dlo(%rip), %rax
    cmp %rax, %rdi
    jbe 9f
    lea rt_dhi(%rip), %rax
    cmp %rax, %rdi
    jb 8b
9:  mov $-1, %rax
    ret
rt_tag:
    mov 8(%rsp), %rdi
    jmp rt_tag_r
rt_aux:
    mov 8(%rsp), %rax
    mov -8(%rax), %rax
    shl $8, %rax
    shr $8, %rax
    ret
rt_len:
    mov 8(%rsp), %rdi
    movzbl -1(%rdi), %eax
    cmp $1, %eax
    je 1f
    cmp $2, %eax
    je 1f
    cmp $4, %eax
    je 1f
    mov (%rdi), %rax
    ret
1:  mov -8(%rdi), %rax
    shl $8, %rax
    shr $8, %rax
    ret
rt_copy:
    # (dst, src, n)
    mov 24(%rsp), %rdi
    mov 16(%rsp), %rsi
    mov 8(%rsp), %rcx
    rep movsb
    xor %eax, %eax
    ret
rt_mark:
    # bytes in use (approximate payload bytes since last collection)
    mov rt_inuse(%rip), %rax
    ret
rt_release:
    # force a full collection (argument ignored)
    call rt_collect
    xor %eax, %eax
    ret
rt_char_at:
    mov 16(%rsp), %rax
    add 8(%rsp), %rax
    movzbq (%rax), %rax
    ret
rt_set_byte:
    mov 24(%rsp), %rax
    add 16(%rsp), %rax
    mov 8(%rsp), %rcx
    movb %cl, (%rax)
    xor %eax, %eax
    ret
rt_exit:
    call rt_flush
    mov 8(%rsp), %rdi
    mov $60, %eax
    syscall
rt_exit0:
    call rt_flush
    xor %edi, %edi
    mov $60, %eax
    syscall
rt_argc:
    mov rt_argcv(%rip), %rax
    ret
rt_argv:
    mov 8(%rsp), %rcx
    cmp rt_argcv(%rip), %rcx
    jae 1f
    mov rt_argvp(%rip), %rax
    mov (%rax,%rcx,8), %r8
    mov %r8, %rdi
    xor %ecx, %ecx
2:  cmpb $0, (%rdi,%rcx)
    je 3f
    inc %rcx
    jmp 2b
3:  mov %rcx, %rdi
    push %rcx
    call rt_mkstr_r
    pop %rcx
    mov %rax, %rdi
    mov %r8, %rsi
    rep movsb
    ret
1:  xor %edi, %edi
    jmp rt_mkstr_r
rt_flushv:
    call rt_flush
    xor %eax, %eax
    ret
rt_env:
    # (i) -> envp[i] as a string, or 0 past the end
    mov 8(%rsp), %rcx
    mov rt_argcv(%rip), %rax
    inc %rax
    shl $3, %rax
    add rt_argvp(%rip), %rax
    mov (%rax,%rcx,8), %r8
    test %r8, %r8
    jz 1f
    mov %r8, %rdi
    xor %ecx, %ecx
2:  cmpb $0, (%rdi,%rcx)
    je 3f
    inc %rcx
    jmp 2b
3:  mov %rcx, %rdi
    push %rcx
    call rt_mkstr_r
    pop %rcx
    mov %rax, %rdi
    mov %r8, %rsi
    rep movsb
    ret
1:  xor %eax, %eax
    ret
rt_write_r:
    # rdi=fd rsi=buf rdx=len, loops until done
1:  test %rdx, %rdx
    jle 2f
    mov $1, %eax
    push %rdi
    push %rsi
    push %rdx
    syscall
    pop %rdx
    pop %rsi
    pop %rdi
    test %rax, %rax
    jle 2f
    add %rax, %rsi
    sub %rax, %rdx
    jmp 1b
2:  ret
rt_flush:
    mov $1, %edi
    lea rt_obuf(%rip), %rsi
    mov rt_on(%rip), %rdx
    movq $0, rt_on(%rip)
    jmp rt_write_r
rt_out_r:
    # buffered stdout: rsi=buf rdx=len
    mov rt_on(%rip), %rax
    add %rdx, %rax
    cmp $65536, %rax
    jbe 1f
    push %rsi
    push %rdx
    call rt_flush
    pop %rdx
    pop %rsi
    cmp $65536, %rdx
    jb 1f
    mov $1, %edi
    jmp rt_write_r
1:  lea rt_obuf(%rip), %rdi
    add rt_on(%rip), %rdi
    mov %rdx, %rcx
    add %rdx, rt_on(%rip)
    rep movsb
    ret
rt_slen_r:
    # rsi = string -> rdx = length
    mov -8(%rsi), %rdx
    shl $8, %rdx
    shr $8, %rdx
    ret
rt_writes:
    mov 8(%rsp), %rsi
    call rt_slen_r
    call rt_out_r
    xor %eax, %eax
    ret
rt_nl:
    lea rt_nlc(%rip), %rsi
    mov $1, %edx
    call rt_out_r
    xor %eax, %eax
    ret
rt_sp:
    lea rt_spc(%rip), %rsi
    mov $1, %edx
    call rt_out_r
    xor %eax, %eax
    ret
rt_puts:
    mov 8(%rsp), %rsi
    call rt_slen_r
    call rt_out_r
    jmp rt_nl
rt_eputs:
    call rt_flush
    mov $2, %edi
    mov 8(%rsp), %rsi
    call rt_slen_r
    call rt_write_r
    mov $2, %edi
    lea rt_nlc(%rip), %rsi
    mov $1, %edx
    call rt_write_r
    xor %eax, %eax
    ret
rt_digits_r:
    # rax = value; writes digits ending at rt_dbuf+32 -> rsi=start, rdx=len
    lea rt_dbuf+32(%rip), %rsi
    mov %rsi, %r9
    xor %r10d, %r10d
    test %rax, %rax
    jns 1f
    neg %rax
    mov $1, %r10d
1:  mov $10, %ecx
2:  xor %edx, %edx
    div %rcx
    add $48, %dl
    dec %rsi
    movb %dl, (%rsi)
    test %rax, %rax
    jnz 2b
    test %r10d, %r10d
    jz 3f
    dec %rsi
    movb $45, (%rsi)
3:  mov %r9, %rdx
    sub %rsi, %rdx
    ret
rt_wint:
    mov 8(%rsp), %rax
    call rt_digits_r
    call rt_out_r
    xor %eax, %eax
    ret
rt_print_int:
    mov 8(%rsp), %rax
    call rt_digits_r
    call rt_out_r
    jmp rt_nl
rt_itos:
    mov 8(%rsp), %rax
    call rt_digits_r
    mov %rsi, %r8
    mov %rdx, %rdi
    push %rdx
    call rt_mkstr_r
    pop %rcx
    mov %rax, %rdi
    mov %r8, %rsi
    rep movsb
    ret
rt_concat:
    mov 16(%rsp), %r10
    mov 8(%rsp), %r11
    mov -8(%r10), %r8
    shl $8, %r8
    shr $8, %r8
    mov -8(%r11), %r9
    shl $8, %r9
    shr $8, %r9
    lea (%r8,%r9), %rdi
    call rt_mkstr_r
    mov %rax, %rdi
    mov %r10, %rsi
    mov %r8, %rcx
    rep movsb
    mov %r11, %rsi
    mov %r9, %rcx
    rep movsb
    ret
rt_substr:
    # (s, a, b) -> s[a:b] (caller guarantees 0 <= a, b <= len)
    mov 16(%rsp), %r10
    mov 8(%rsp), %r11
    sub %r10, %r11
    jge 1f
    xor %r11d, %r11d
1:  mov %r11, %rdi
    call rt_mkstr_r
    mov %rax, %rdi
    mov 24(%rsp), %rsi
    add %r10, %rsi
    mov %r11, %rcx
    rep movsb
    ret
rt_chr:
    mov $1, %edi
    call rt_mkstr_r
    mov 8(%rsp), %rcx
    movb %cl, (%rax)
    ret
rt_str_eq:
    mov 16(%rsp), %rsi
    mov 8(%rsp), %rdi
    mov -8(%rsi), %rcx
    cmp -8(%rdi), %rcx
    jne 2f
    shl $8, %rcx
    shr $8, %rcx
    repe cmpsb
    jne 2f
    mov $1, %eax
    ret
2:  xor %eax, %eax
    ret
rt_join:
    # (list, n): concatenation of the first n strings
    mov 16(%rsp), %r8
    mov 16(%r8), %r8
    mov 8(%rsp), %r9
    xor %r10d, %r10d
    xor %r11d, %r11d
1:  cmp %r9, %r11
    jge 2f
    mov (%r8,%r11,8), %rsi
    call rt_slen_r
    add %rdx, %r10
    inc %r11
    jmp 1b
2:  mov %r10, %rdi
    call rt_mkstr_r
    mov %rax, %rdi
    xor %r11d, %r11d
3:  cmp %r9, %r11
    jge 5f
    mov (%r8,%r11,8), %rsi
    call rt_slen_r
    mov %rdx, %rcx
    rep movsb
    inc %r11
    jmp 3b
5:  ret
rt_write_file:
    # (path, s) -> 1 ok / 0 fail
    mov 16(%rsp), %rdi
    mov $577, %esi
    mov $420, %edx
    mov $2, %eax
    syscall
    test %rax, %rax
    js 1f
    push %rax
    mov %rax, %rdi
    mov 16(%rsp), %rsi
    call rt_slen_r
    call rt_write_r
    pop %rdi
    mov $3, %eax
    syscall
    mov $1, %eax
    ret
1:  xor %eax, %eax
    ret
rt_read_file:
    # (path) -> string, or 0 on failure
    mov 8(%rsp), %rdi
    xor %esi, %esi
    mov $2, %eax
    syscall
    test %rax, %rax
    js 9f
    mov %rax, %r8
    mov %r8, %rdi
    xor %esi, %esi
    mov $2, %edx
    mov $8, %eax
    syscall
    mov %rax, %r9
    mov %r8, %rdi
    xor %esi, %esi
    xor %edx, %edx
    mov $8, %eax
    syscall
    mov %r9, %rdi
    call rt_mkstr_r
    mov %rax, %r10
    xor %r11d, %r11d
1:  cmp %r9, %r11
    jge 2f
    mov %r8, %rdi
    lea (%r10,%r11), %rsi
    mov %r9, %rdx
    sub %r11, %rdx
    xor %eax, %eax
    push %r11
    syscall
    pop %r11
    test %rax, %rax
    jle 2f
    add %rax, %r11
    jmp 1b
2:  movb $0, (%r10,%r11)
    mov %r11, %rax
    bts $56, %rax
    mov %rax, -8(%r10)
    mov %r8, %rdi
    mov $3, %eax
    syscall
    mov %r10, %rax
    ret
9:  xor %eax, %eax
    ret
# ---- slow paths that preserve r8-r11 (expression temporaries) ----
rtp_getidx:
    # rax=obj rcx=index rdx=line
    push %r8
    push %r9
    push %r10
    push %r11
    push %rax
    push %rcx
    push %rdx
    call f___getidx
    add $24, %rsp
    jmp rtp_ret
rtp_setidx:
    # rdx=obj rcx=index rax=value rsi=line
    push %r8
    push %r9
    push %r10
    push %r11
    push %rdx
    push %rcx
    push %rax
    push %rsi
    call f___setidx
    add $32, %rsp
    jmp rtp_ret
rtp_add:
    push %r8
    push %r9
    push %r10
    push %r11
    push %rax
    push %rcx
    push %rdx
    call f___add
    add $24, %rsp
    jmp rtp_ret
rtp_eq:
    push %r8
    push %r9
    push %r10
    push %r11
    push %rax
    push %rcx
    call f___eq
    add $16, %rsp
rtp_ret:
    pop %r11
    pop %r10
    pop %r9
    pop %r8
    ret
rtp_truth:
    # rax=value (non-small-int) -> rdx = 0/1, rax preserved
    push %r8
    push %r9
    push %r10
    push %r11
    push %rax
    push %rax
    call f___truth
    add $8, %rsp
    mov %rax, %rdx
    pop %rax
    jmp rtp_ret
rt_callchk:
    # r10 = callee value, r11 = argument count, edx = line
    mov %r10, %rdi
    call rt_tag_r
    cmp $8, %rax
    jne 1f
    cmpq $0, (%r10)
    je 1f
    cmp 8(%r10), %r11
    jne 2f
    ret
1:  push %rdx
    lea rt_nc_msg(%rip), %rax
    push %rax
    call f___err
2:  push %rdx
    lea rt_na_msg(%rip), %rax
    push %rax
    call f___err
rt_nometh:
    push $0
    lea rt_nm_msg(%rip), %rax
    push %rax
    call f___err
rt_divzero:
    # rdx = line
    push %rdx
    lea rt_dz_msg(%rip), %rax
    push %rax
    call f___err
# ---- numbers: bools are 1<<62|b, floats are boxed doubles (tag 5) ----
rt_tod_r:
    # rdi = int/bool/float -> xmm0; clobbers rax
    call rt_tag_r
    cmp $5, %eax
    je 1f
    cmp $6, %eax
    je 2f
    cvtsi2sdq %rdi, %xmm0
    ret
1:  movsd (%rdi), %xmm0
    ret
2:  mov %rdi, %rax
    and $1, %eax
    cvtsi2sdq %rax, %xmm0
    ret
rt_fbox_r:
    # xmm0 -> rax (new float object); clobbers rdx, rdi, rsi
    mov $8, %edi
    mov $5, %esi
    shl $56, %rsi
    call rt_new_r
    movsd %xmm0, (%rax)
    ret
rt_numk:
    # rdi -> eax: 0 int, 1 bool, 2 float, 3 other; clobbers nothing else
    call rt_tag_r
    cmp $-1, %rax
    je 1f
    cmp $4, %eax
    je 1f
    cmp $6, %eax
    je 2f
    cmp $5, %eax
    je 3f
    mov $3, %eax
    ret
1:  xor %eax, %eax
    ret
2:  mov $1, %eax
    ret
3:  mov $2, %eax
    ret
rt_floor_r:
    # xmm0 -> xmm0 = floor(xmm0); clobbers rax, rdx, xmm1, xmm2
    movq %xmm0, %rax
    btr $63, %rax
    mov $0x4330000000000000, %rdx
    cmp %rdx, %rax
    jae 1f
    cvttsd2si %xmm0, %rax
    cvtsi2sdq %rax, %xmm1
    ucomisd %xmm0, %xmm1
    jbe 2f
    dec %rax
    cvtsi2sdq %rax, %xmm1
2:  movapd %xmm1, %xmm0
1:  ret
rt_fmod_r:
    # xmm0 = a, xmm1 = b -> xmm0 = a mod b with the sign of b (Python); clobbers rax, rdx, xmm3
    sub $16, %rsp
    movsd %xmm1, (%rsp)
    fldl (%rsp)
    movsd %xmm0, 8(%rsp)
    fldl 8(%rsp)
1:  fprem
    fnstsw %ax
    test $0x400, %ax
    jnz 1b
    fstpl 8(%rsp)
    fstp %st(0)
    movsd 8(%rsp), %xmm0
    add $16, %rsp
    xorpd %xmm3, %xmm3
    ucomisd %xmm3, %xmm0
    jp 3f
    je 2f
    movq %xmm0, %rax
    movq %xmm1, %rdx
    xor %rdx, %rax
    jns 3f
    addsd %xmm1, %xmm0
    ret
2:  movq %xmm1, %rax
    shr $63, %rax
    shl $63, %rax
    movq %rax, %xmm0
3:  ret
rtp_binop:
    # rax=a rcx=b esi=op (0 + 1 - 2 * 3 / 4 // 5 %) edx=line -> rax; preserves r8-r11
    push %r8
    push %r9
    push %r10
    push %r11
    push %rax
    push %rcx
    push %rsi
    push %rdx
    mov 24(%rsp), %rdi
    call rt_numk
    mov %eax, %r8d
    mov 16(%rsp), %rdi
    call rt_numk
    mov %eax, %r9d
    cmp $3, %r8d
    je 7f
    cmp $3, %r9d
    je 7f
    cmp $2, %r8d
    je 5f
    cmp $2, %r9d
    je 5f
    cmpq $3, 8(%rsp)
    je 5f
    mov 24(%rsp), %rax
    cmp $1, %r8d
    jne 1f
    and $1, %eax
1:  mov 16(%rsp), %rcx
    cmp $1, %r9d
    jne 1f
    and $1, %ecx
1:  mov 8(%rsp), %rsi
    cmp $0, %esi
    jne 1f
    add %rcx, %rax
    jo 2f
    jmp 6f
1:  cmp $1, %esi
    jne 1f
    sub %rcx, %rax
    jo 2f
    jmp 6f
1:  cmp $2, %esi
    jne 1f
    imul %rcx, %rax
    jo 2f
    jmp 6f
6:  mov %rax, %rdx
    shr $2, %rdx
    mov $0x1000000000000000, %r10
    cmp %r10, %rdx
    jne 9f
2:  pushq 24(%rsp)
    pushq 24(%rsp)
    pushq 24(%rsp)
    pushq 24(%rsp)
    call f___bigop
    add $32, %rsp
    jmp 9f
1:  test %rcx, %rcx
    jz 8f
    cqo
    idiv %rcx
    test %rdx, %rdx
    jz 4f
    mov %rdx, %r10
    xor %rcx, %r10
    jns 4f
    dec %rax
    add %rcx, %rdx
4:  cmp $5, %esi
    jne 9f
    mov %rdx, %rax
    jmp 9f
5:  mov 24(%rsp), %rdi
    call rt_tod_r
    movapd %xmm0, %xmm2
    mov 16(%rsp), %rdi
    call rt_tod_r
    movapd %xmm0, %xmm1
    movapd %xmm2, %xmm0
    mov 8(%rsp), %rsi
    cmp $0, %esi
    jne 1f
    addsd %xmm1, %xmm0
    jmp 6f
1:  cmp $1, %esi
    jne 1f
    subsd %xmm1, %xmm0
    jmp 6f
1:  cmp $2, %esi
    jne 1f
    mulsd %xmm1, %xmm0
    jmp 6f
1:  xorpd %xmm3, %xmm3
    ucomisd %xmm3, %xmm1
    jp 1f
    je 3f
1:  cmp $3, %esi
    jne 1f
    divsd %xmm1, %xmm0
    jmp 6f
1:  cmp $5, %esi
    jne 1f
    call rt_fmod_r
    jmp 6f
1:  mov 24(%rsp), %rax
    push %rax
    mov 24(%rsp), %rax
    push %rax
    call f___ffloordiv
    add $16, %rsp
    jmp 9f
3:  mov (%rsp), %rdx
    jmp rt_fdivzero
6:  call rt_fbox_r
    jmp 9f
7:  cmpq $0, 8(%rsp)
    jne 1f
    pushq 24(%rsp)
    pushq 24(%rsp)
    pushq 16(%rsp)
    call f___add
    add $24, %rsp
    jmp 9f
1:  call f___binop
    jmp 9f
8:  mov (%rsp), %rdx
    jmp rt_divzero
9:  add $32, %rsp
    pop %r11
    pop %r10
    pop %r9
    pop %r8
    ret
rtp_cmp:
    # rax=a rcx=b esi=op (10 == 11 != 12 < 13 > 14 <= 15 >=) -> rax 0/1 or a bool; preserves r8-r11
    push %r8
    push %r9
    push %r10
    push %r11
    push %rax
    push %rcx
    push %rsi
    mov 16(%rsp), %rdi
    call rt_numk
    mov %eax, %r8d
    mov 8(%rsp), %rdi
    call rt_numk
    mov %eax, %r9d
    cmp $3, %r8d
    je 7f
    cmp $3, %r9d
    je 7f
    mov (%rsp), %rsi
    cmp $2, %r8d
    je 5f
    cmp $2, %r9d
    je 5f
    mov 16(%rsp), %rdx
    cmp $1, %r8d
    jne 1f
    and $1, %edx
1:  mov 8(%rsp), %rcx
    cmp $1, %r9d
    jne 1f
    and $1, %ecx
1:  xor %eax, %eax
    cmp $10, %esi
    jne 1f
    cmp %rcx, %rdx
    sete %al
    jmp 9f
1:  cmp $11, %esi
    jne 1f
    cmp %rcx, %rdx
    setne %al
    jmp 9f
1:  cmp $12, %esi
    jne 1f
    cmp %rcx, %rdx
    setl %al
    jmp 9f
1:  cmp $13, %esi
    jne 1f
    cmp %rcx, %rdx
    setg %al
    jmp 9f
1:  cmp $14, %esi
    jne 1f
    cmp %rcx, %rdx
    setle %al
    jmp 9f
1:  cmp %rcx, %rdx
    setge %al
    jmp 9f
5:  mov 16(%rsp), %rdi
    call rt_tod_r
    movapd %xmm0, %xmm2
    mov 8(%rsp), %rdi
    call rt_tod_r
    movapd %xmm0, %xmm1
    movapd %xmm2, %xmm0
    mov (%rsp), %rsi
    xor %eax, %eax
    cmp $10, %esi
    jne 1f
    ucomisd %xmm1, %xmm0
    setnp %al
    sete %cl
    and %cl, %al
    jmp 9f
1:  cmp $11, %esi
    jne 1f
    ucomisd %xmm1, %xmm0
    setp %al
    setne %cl
    or %cl, %al
    jmp 9f
1:  cmp $12, %esi
    jne 1f
    ucomisd %xmm0, %xmm1
    seta %al
    jmp 9f
1:  cmp $13, %esi
    jne 1f
    ucomisd %xmm1, %xmm0
    seta %al
    jmp 9f
1:  cmp $14, %esi
    jne 1f
    ucomisd %xmm0, %xmm1
    setae %al
    jmp 9f
1:  ucomisd %xmm1, %xmm0
    setae %al
    jmp 9f
7:  call f___cmpop
9:  add $24, %rsp
    pop %r11
    pop %r10
    pop %r9
    pop %r8
    ret
rt_fdivzero:
    # rdx = line
    push %rdx
    lea rt_fdz_msg(%rip), %rax
    push %rax
    call f___err
# float builtins callable from Forge
rt_itof:
    mov 8(%rsp), %rdi
    call rt_tod_r
    jmp rt_fbox_r
rt_ftoi:
    # (float, mode) mode 0 truncate, 1 floor, 2 round-half-even -> int
    mov 16(%rsp), %rdi
    call rt_tod_r
    mov 8(%rsp), %rax
    cmp $1, %eax
    jne 1f
    call rt_floor_r
    mov 8(%rsp), %rax
1:  cmp $2, %eax
    je 2f
    cvttsd2si %xmm0, %rax
    ret
2:  cvtsd2si %xmm0, %rax
    ret
rt_fbits:
    mov 8(%rsp), %rdi
    call rt_tod_r
    movq %xmm0, %rax
    ret
rt_ffrombits:
    mov 8(%rsp), %rax
    movq %rax, %xmm0
    jmp rt_fbox_r
rt_math:
    # (x, op) -> float. 0 sqrt 1 floor 2 sin 3 cos 4 tan 5 atan 6 exp 7 log 8 log2 9 log10 10 fabs
    mov 16(%rsp), %rdi
    call rt_tod_r
    mov 8(%rsp), %rcx
    cmp $0, %ecx
    jne 1f
    sqrtsd %xmm0, %xmm0
    jmp rt_fbox_r
1:  cmp $1, %ecx
    jne 1f
    call rt_floor_r
    jmp rt_fbox_r
1:  cmp $10, %ecx
    jne 1f
    movq %xmm0, %rax
    btr $63, %rax
    movq %rax, %xmm0
    jmp rt_fbox_r
1:  sub $24, %rsp
    movsd %xmm0, (%rsp)
    cmp $7, %ecx
    jl 1f
    cmp $7, %ecx
    jne 2f
    fldln2
    jmp 3f
2:  cmp $8, %ecx
    jne 2f
    fld1
    jmp 3f
2:  fldlg2
3:  fldl (%rsp)
    fyl2x
    jmp 8f
1:  fldl (%rsp)
    cmp $2, %ecx
    jne 1f
    fsin
    jmp 8f
1:  cmp $3, %ecx
    jne 1f
    fcos
    jmp 8f
1:  cmp $4, %ecx
    jne 1f
    fptan
    fstp %st(0)
    jmp 8f
1:  cmp $5, %ecx
    jne 1f
    fld1
    fpatan
    jmp 8f
1:  fldl2e
    fmulp
    call rt_exp2_x87
8:  fstpl (%rsp)
    movsd (%rsp), %xmm0
    add $24, %rsp
    jmp rt_fbox_r
rt_exp2_x87:
    # st0 = t -> st0 = 2^t; uses 16 bytes of stack scratch
    sub $24, %rsp
    fstl (%rsp)
    frndint
    fstl 8(%rsp)
    fsubrl (%rsp)
    f2xm1
    fld1
    faddp
    fldl 8(%rsp)
    fxch
    fscale
    fstp %st(1)
    add $24, %rsp
    ret
rt_math2:
    # (y, x, op) op 0: atan2(y, x)  1: x ** y for x > 0
    mov 24(%rsp), %rdi
    call rt_tod_r
    movapd %xmm0, %xmm2
    mov 16(%rsp), %rdi
    call rt_tod_r
    sub $24, %rsp
    movsd %xmm2, (%rsp)
    movsd %xmm0, 8(%rsp)
    mov 32(%rsp), %rcx
    cmp $0, %ecx
    jne 1f
    fldl (%rsp)
    fldl 8(%rsp)
    fpatan
    jmp 8f
1:  fldl (%rsp)
    fldl 8(%rsp)
    fyl2x
    call rt_exp2_x87
8:  fstpl (%rsp)
    movsd (%rsp), %xmm0
    add $24, %rsp
    jmp rt_fbox_r
# ---- exceptions: handler frames are [prev, rbp, rbx, r12, r13, r14, r15, landing] on the stack ----
# ---- generators: stackful coroutines. h = raw object:
#   0 fn  8 stack  16 gen sp  24 state(0 new 1 suspended 2 running 3 done 4 failed)  32 -  40 caller sp
#   48 caller exc_top  56 previous rt_gcur  64 gen exc_top
rt_gresume:
    # (h, sendval) -> yielded value
    mov 16(%rsp), %rdi
    mov 8(%rsp), %rax
    push %rbp
    push %rbx
    push %r12
    push %r13
    push %r14
    push %r15
    mov %rsp, 40(%rdi)
    mov rt_exc_top(%rip), %rcx
    mov %rcx, 48(%rdi)
    mov rt_gcur(%rip), %rcx
    mov %rcx, 56(%rdi)
    test %rcx, %rcx
    jnz 1f
    mov %rsp, rt_main_sp(%rip)
1:  mov %rdi, rt_gcur(%rip)
    cmpq $0, 24(%rdi)
    jne 2f
    movq $2, 24(%rdi)
    mov 8(%rdi), %rsp
    add $65520, %rsp
    and $-16, %rsp
    push %rdi
    push %rdi
    movabs $f___gstart, %rax
    call *%rax
    ud2
2:  movq $2, 24(%rdi)
    mov 64(%rdi), %rcx
    mov %rcx, rt_exc_top(%rip)
    mov 16(%rdi), %rsp
    pop %r15
    pop %r14
    pop %r13
    pop %r12
    pop %rbx
    pop %rbp
    ret
rt_gyield:
    # (value) -> value sent by the next resume
    mov 8(%rsp), %rax
    mov rt_gcur(%rip), %rdi
    test %rdi, %rdi
    jz 9f
    push %rbp
    push %rbx
    push %r12
    push %r13
    push %r14
    push %r15
    mov %rsp, 16(%rdi)
    movq $1, 24(%rdi)
rt_gback:
    mov rt_exc_top(%rip), %rcx
    mov %rcx, 64(%rdi)
    mov 48(%rdi), %rcx
    mov %rcx, rt_exc_top(%rip)
    mov 56(%rdi), %rcx
    mov %rcx, rt_gcur(%rip)
    mov 40(%rdi), %rsp
    pop %r15
    pop %r14
    pop %r13
    pop %r12
    pop %rbx
    pop %rbp
    ret
9:  ret
rt_gfin:
    # (value, state): generator finished; never returns
    mov 16(%rsp), %rax
    mov 8(%rsp), %rcx
    mov rt_gcur(%rip), %rdi
    mov %rcx, 24(%rdi)
    jmp rt_gback
rt_syscall:
    # (n, a, b, c) -> raw result
    mov 32(%rsp), %rax
    mov 24(%rsp), %rdi
    mov 16(%rsp), %rsi
    mov 8(%rsp), %rdx
    syscall
    ret
rt_clock:
    # (clockid) -> nanoseconds
    mov 8(%rsp), %rdi
    sub $16, %rsp
    mov %rsp, %rsi
    mov $228, %eax
    syscall
    mov (%rsp), %rax
    imul $1000000000, %rax, %rax
    add 8(%rsp), %rax
    add $16, %rsp
    ret
rt_raise:
    mov 16(%rsp), %rax
    mov 8(%rsp), %rcx
    mov %rax, rt_exc_val(%rip)
    mov %rcx, rt_exc_line(%rip)
rt_throw:
    mov rt_exc_top(%rip), %rdx
    test %rdx, %rdx
    jz 1f
    mov %rdx, %rsp
    pop rt_exc_top(%rip)
    pop %rbp
    pop %rbx
    pop %r12
    pop %r13
    pop %r14
    pop %r15
    ret
1:  push rt_exc_val(%rip)
    push rt_exc_line(%rip)
    call f___uncaught
    push $1
    call rt_exit
rt_rethrow:
    mov 8(%rsp), %rax
    mov %rax, rt_exc_val(%rip)
    jmp rt_throw
rt_clsname:
    mov 8(%rsp), %rax
    movzwl -8(%rax), %ecx
    lea rt_cnames(%rip), %rdx
    mov (%rdx,%rcx,8), %rax
    ret
rt_excline:
    mov rt_exc_line(%rip), %rax
    ret
rt_excval:
    mov rt_exc_val(%rip), %rax
    ret
.data
.p2align 3
rt_exc_top: .quad 0
rt_exc_val: .quad 0
rt_exc_line: .quad 0
rt_sigact: .quad 0, 0, 0, 0
rt_hp: .quad 0
rt_hlo: .quad 0
rt_inuse: .quad 0
rt_gcnext: .quad 0x2000000
rt_ngc: .quad 0
rt_stk0: .quad 0
rt_gcur: .quad 0
rt_main_sp: .quad 0
rt_pt: .quad 0
rt_ms: .quad 0
rt_lfree: .quad 0
rt_fl: .fill 40, 8, 0
rt_oom_msg: .ascii "error: out of memory"
rt_on: .quad 0
rt_argcv: .quad 0
rt_argvp: .quad 0
rt_nlc: .byte 10
rt_spc: .byte 32
rt_segv_msg: .ascii "error: segmentation fault (invalid memory access, e.g. None)\n"
.p2align 3
.quad 0x100000000000010
rt_dz_msg: .asciz "division by zero"
.p2align 3
.quad 0x100000000000016
rt_fdz_msg: .asciz "float division by zero"
.p2align 3
.quad 0x100000000000019
rt_nm_msg: .asciz "object has no such method"
.p2align 3
.quad 0x100000000000016
rt_nc_msg: .asciz "object is not callable"
.p2align 3
.quad 0x100000000000024
rt_na_msg: .asciz "wrong number of positional arguments"
.bss
.p2align 4
rt_obuf: .space 65536
rt_dbuf: .space 32
