# ---- Forge runtime (x86-64 Linux, no libc). Stack args: last arg at 8(%rsp). ----
# Object layout: 8-byte header at ptr-8 = tag<<56 | aux.  tags: 0 list [len,cap,data]  1 str (aux=len, NUL-terminated)
#   2 object (aux=class id, fields at 0..)  3 dict  4 raw bytes (aux=size)
# Heap: 64 GiB MAP_NORESERVE bump arena at 2^45; image linked at 2^44, so ints in [0,2^44) never alias pointers.
.weak f___getidx, f___setidx, f___add, f___eq, f___truth, f___err
.text
rt_init:
    # rdi = initial %rsp (argc, argv...)
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
    # rdi = payload bytes, rsi = header -> rax = object (8-aligned); clobbers rdx
    mov rt_hp(%rip), %rax
    mov %rsi, (%rax)
    add $8, %rax
    lea 7(%rax,%rdi), %rdx
    and $-8, %rdx
    mov %rdx, rt_hp(%rip)
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
    mov %rax, %rdi
    xor %eax, %eax
    rep stosq
    mov %r8, %rax
    ret
rt_tag_r:
    # rdi = value -> rax = tag, or -1 if not a heap/data object
    test $7, %dil
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
    mov rt_hp(%rip), %rax
    ret
rt_release:
    mov 8(%rsp), %rax
    mov %rax, rt_hp(%rip)
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
.data
.p2align 3
rt_sigact: .quad 0, 0, 0, 0
rt_hp: .quad 0
rt_hlo: .quad 0
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
.quad 0x100000000000019
rt_nm_msg: .asciz "object has no such method"
.bss
.p2align 4
rt_obuf: .space 65536
rt_dbuf: .space 32
