# ---- Forge runtime (args on stack, last arg at 8(%rsp)) ----
.text
rt_strlen_r:
    mov %rdi, %rax
1:  cmpb $0, (%rax)
    je 2f
    inc %rax
    jmp 1b
2:  sub %rdi, %rax
    ret
rt_alloc_r:
    mov rt_hp(%rip), %rax
    add $15, %rdi
    and $-16, %rdi
    add %rax, %rdi
    mov %rdi, rt_hp(%rip)
    ret
rt_alloc:
    mov 8(%rsp), %rdi
    jmp rt_alloc_r
rt_list:
    mov 8(%rsp), %rdi
    shl $3, %rdi
    jmp rt_alloc_r
rt_len:
    mov 8(%rsp), %rdi
    jmp rt_strlen_r
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
    mov $0, %rax
    ret
rt_exit:
    mov 8(%rsp), %rdi
    mov $60, %rax
    syscall
rt_argc:
    mov rt_argcv(%rip), %rax
    ret
rt_argv:
    mov 8(%rsp), %rcx
    cmp rt_argcv(%rip), %rcx
    jge 1f
    mov rt_argvp(%rip), %rax
    mov (%rax,%rcx,8), %rax
    ret
1:  lea rt_empty(%rip), %rax
    ret
rt_write_r:
    # rdi=fd rsi=buf rdx=len, loops until done
1:  test %rdx, %rdx
    jle 2f
    mov $1, %rax
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
rt_puts_fd:
    # rdi=fd rsi=str
    push %rdi
    push %rsi
    mov %rsi, %rdi
    call rt_strlen_r
    mov %rax, %rdx
    pop %rsi
    pop %rdi
    jmp rt_write_r
rt_puts:
    mov $1, %rdi
    mov 8(%rsp), %rsi
    call rt_puts_fd
    mov $1, %rdi
    lea rt_nl(%rip), %rsi
    mov $1, %rdx
    call rt_write_r
    xor %eax, %eax
    ret
rt_eputs:
    mov $2, %rdi
    mov 8(%rsp), %rsi
    call rt_puts_fd
    mov $2, %rdi
    lea rt_nl(%rip), %rsi
    mov $1, %rdx
    call rt_write_r
    xor %eax, %eax
    ret
rt_itos:
    mov 8(%rsp), %rax
    mov $32, %rdi
    push %rax
    call rt_alloc_r
    mov %rax, %r8
    pop %rax
    lea 31(%r8), %rsi
    movb $0, (%rsi)
    xor %r9, %r9
    test %rax, %rax
    jns 1f
    neg %rax
    mov $1, %r9
1:  mov $10, %rcx
2:  xor %rdx, %rdx
    div %rcx
    add $48, %dl
    dec %rsi
    movb %dl, (%rsi)
    test %rax, %rax
    jnz 2b
    test %r9, %r9
    jz 3f
    dec %rsi
    movb $45, (%rsi)
3:  mov %rsi, %rax
    ret
rt_print_int:
    pushq 8(%rsp)
    call rt_itos
    add $8, %rsp
    push %rax
    call rt_puts
    add $8, %rsp
    ret
rt_concat:
    mov 16(%rsp), %rdi
    call rt_strlen_r
    mov %rax, %r10
    mov 8(%rsp), %rdi
    call rt_strlen_r
    mov %rax, %r11
    lea 1(%r10,%r11), %rdi
    call rt_alloc_r
    mov %rax, %rdx
    mov %rax, %rdi
    mov 16(%rsp), %rsi
    mov %r10, %rcx
    rep movsb
    mov 8(%rsp), %rsi
    mov %r11, %rcx
    rep movsb
    movb $0, (%rdi)
    mov %rdx, %rax
    ret
rt_substr:
    # (s, a, b)
    mov 16(%rsp), %r10
    mov 8(%rsp), %r11
    sub %r10, %r11
    lea 1(%r11), %rdi
    call rt_alloc_r
    mov %rax, %rdx
    mov %rax, %rdi
    mov 24(%rsp), %rsi
    add %r10, %rsi
    mov %r11, %rcx
    rep movsb
    movb $0, (%rdi)
    mov %rdx, %rax
    ret
rt_chr:
    mov $2, %rdi
    call rt_alloc_r
    mov 8(%rsp), %rcx
    movb %cl, (%rax)
    movb $0, 1(%rax)
    ret
rt_str_eq:
    mov 16(%rsp), %rsi
    mov 8(%rsp), %rdi
1:  movb (%rsi), %al
    cmpb (%rdi), %al
    jne 2f
    test %al, %al
    jz 3f
    inc %rsi
    inc %rdi
    jmp 1b
2:  xor %eax, %eax
    ret
3:  mov $1, %eax
    ret
rt_join:
    # (list, n): total length then copy
    mov 16(%rsp), %r8
    mov 8(%rsp), %r9
    xor %r10, %r10
    xor %r11, %r11
1:  cmp %r9, %r11
    jge 2f
    mov (%r8,%r11,8), %rdi
    call rt_strlen_r
    add %rax, %r10
    inc %r11
    jmp 1b
2:  lea 1(%r10), %rdi
    call rt_alloc_r
    mov %rax, %rdx
    mov %rax, %rdi
    xor %r11, %r11
3:  cmp %r9, %r11
    jge 5f
    mov (%r8,%r11,8), %rsi
4:  movb (%rsi), %al
    test %al, %al
    jz 6f
    movb %al, (%rdi)
    inc %rsi
    inc %rdi
    jmp 4b
6:  inc %r11
    jmp 3b
5:  movb $0, (%rdi)
    mov %rdx, %rax
    ret
rt_write_file:
    # (path, s) -> 1 ok / 0 fail
    mov 16(%rsp), %rdi
    mov $577, %rsi
    mov $420, %rdx
    mov $2, %rax
    syscall
    test %rax, %rax
    js 1f
    push %rax
    mov %rax, %rdi
    mov 16(%rsp), %rsi
    call rt_puts_fd
    pop %rdi
    mov $3, %rax
    syscall
    mov $1, %rax
    ret
1:  xor %eax, %eax
    ret
rt_read_file:
    # (path) -> string, or 0 on failure
    mov 8(%rsp), %rdi
    xor %rsi, %rsi
    mov $2, %rax
    syscall
    test %rax, %rax
    js 9f
    mov %rax, %r8
    mov %r8, %rdi
    xor %rsi, %rsi
    mov $2, %rdx
    mov $8, %rax
    syscall
    mov %rax, %r9
    mov %r8, %rdi
    xor %rsi, %rsi
    xor %rdx, %rdx
    mov $8, %rax
    syscall
    lea 1(%r9), %rdi
    call rt_alloc_r
    mov %rax, %r10
    xor %r11, %r11
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
    mov %r8, %rdi
    mov $3, %rax
    syscall
    mov %r10, %rax
    ret
9:  xor %eax, %eax
    ret
.data
rt_nl: .byte 10
rt_empty: .byte 0
.align 8
rt_hp: .quad 0
rt_argcv: .quad 0
rt_argvp: .quad 0
.bss
.align 16
rt_heap: .space 2147483648
