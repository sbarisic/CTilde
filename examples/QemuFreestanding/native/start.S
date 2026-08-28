.set MULTIBOOT_MAGIC, 0x1badb002
.set MULTIBOOT_FLAGS, 0x00000003
.set MULTIBOOT_CHECKSUM, -(MULTIBOOT_MAGIC + MULTIBOOT_FLAGS)

.section .multiboot,"a",@progbits
.balign 4
.long MULTIBOOT_MAGIC
.long MULTIBOOT_FLAGS
.long MULTIBOOT_CHECKSUM

.section .bss,"aw",@nobits
.balign 16
stack_bottom:
.skip 16384
stack_top:

.section .text.boot,"ax",@progbits
.code32
.global _start
.type _start,@function
_start:
    cli
    cld
    movl $stack_top, %esp
    xorl %ebp, %ebp
    call ct_runtime_initialize
    call kernel_main
    movl %eax, %ebx
    call ct_runtime_shutdown
    movl %ebx, %eax
    outl %eax, $0xf4
1:
    hlt
    jmp 1b
.size _start, .-_start

.global qemu_debug_write
.type qemu_debug_write,@function
qemu_debug_write:
    movb 4(%esp), %al
    outb %al, $0xe9
    ret
.size qemu_debug_write, .-qemu_debug_write

.global qemu_debug_exit
.type qemu_debug_exit,@function
qemu_debug_exit:
    movl 4(%esp), %eax
    outl %eax, $0xf4
    ret
.size qemu_debug_exit, .-qemu_debug_exit

.section .note.GNU-stack,"",@progbits
