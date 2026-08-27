.section .text.boot,"ax",@progbits
.global _start
.type _start,@function
_start:
    call ct_runtime_initialize
    call kernel_main
    call ct_runtime_shutdown
    xorl %edi, %edi
    movl $60, %eax
    syscall
1:
    hlt
    jmp 1b
.size _start, .-_start

.section .note.GNU-stack,"",@progbits
