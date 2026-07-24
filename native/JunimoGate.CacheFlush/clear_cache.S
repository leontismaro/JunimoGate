.text
.p2align 2
.global junimogate_clear_cache
.type junimogate_clear_cache, %function

// void junimogate_clear_cache(void* start, void* end)
//
// ARMv8-A cache maintenance sequence based on CTR_EL0. It has no libc or
// Android NDK dependency. x0 is inclusive start and x1 is exclusive end.
junimogate_clear_cache:
    cmp     x0, x1
    b.hs    .Ldone

    mrs     x2, ctr_el0

    // Match Android compiler-rt's conservative AArch64 sequence: always
    // clean data lines and invalidate instruction lines. Do not skip either
    // loop based on CTR_EL0.IDC/DIC; that optimization did not make modified
    // Mono JIT code visible on the validation device.
    ubfx    x3, x2, #16, #4
    mov     x4, #4
    lsl     x4, x4, x3
    sub     x5, x4, #1
    bic     x3, x0, x5
.Ldata_loop:
    dc      cvau, x3
    add     x3, x3, x4
    cmp     x3, x1
    b.lo    .Ldata_loop
    dsb     ish

    and     x3, x2, #0xf
    mov     x4, #4
    lsl     x4, x4, x3
    sub     x5, x4, #1
    bic     x3, x0, x5
.Linstruction_loop:
    ic      ivau, x3
    add     x3, x3, x4
    cmp     x3, x1
    b.lo    .Linstruction_loop
    dsb     ish
    isb
.Ldone:
    ret

.size junimogate_clear_cache, .-junimogate_clear_cache
.section .note.GNU-stack,"",%progbits
