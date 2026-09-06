# Practical SSH and production XIP

The comparison baseline is `cda18458`. Runtime ABI 23 and Managed Module ABI 4 remain unchanged.
This work is incomplete. The RAM-loaded SSH checkpoint must pass before XIP implementation starts.
General spans and scoped parameters are deferred. SFTP is outside the default profile and current acceptance scope.

## Current implementation

- The default SSH source set rejects subsystem requests. The optional `Modules/SystemSsh/ctilde.sftp.json` source set retains SFTP.
- Process startup accepts independent redirected pipe capacities. Ordinary callers retain 8,192-byte defaults. SSH requests 1,024 bytes per stream.
- The process capability appends a sized native start operation. It copies borrowed UTF-8 arguments before returning.
- Processes share immutable dependency topology. Each process allocates only its required module states and initialization flags.
- Argument pointers, lengths, and bytes use one allocation. Mailboxes allocate on first use and retain their 16-message depth.
- SSH reuses a 2 KiB output pump buffer. It drains both output streams through EOF before closing a completed command.
- A zero-byte pipe read reports EOF only after buffered output drains. SSH can detect completion even when the peer's window is zero.
- A bounded input queue retains partial pipe writes. The initial channel receive window is 1 KiB. Credit returns after pipe acceptance.
- Allocation failures report their stage, requested bytes, quota, free heap, and largest block through the production logger.
- Overlay packaging consumes exact CMake link inputs and drains tool output streams concurrently.
- Module compilation tracks transitive headers with compiler dependency files. Header-only type changes rebuild unchanged constructors.
- The SSH exit-status writer reserves all 25 protocol bytes. Native `free` output uses the process console bridge for UART and redirected pipes.
- The shell distinguishes an empty input poll from EOF and consumes complete, fragmented CSI sequences. Zero PTY dimensions retain the current terminal size.
- `Memory.TryAllocateBytes` returns null on allocation failure. SSH uses it for receive allocations and closes the connection on shortage. This intermediate receive path still allocates a body and a payload; it is not the final arena design.
- String fields decode from checked ranges. Channel input copies directly from the packet into its asynchronous input queue.

The smaller channel window is a provisional backpressure choice. Transport packet and channel packet-size limits remain unchanged.
Throughput, interactive latency, foreground-child cancellation, and maximum-packet pressure still require device measurements.

## Validation and limits

The full firmware and all 13 default modules rebuilt after the header-dependency and pipe EOF fixes. The existing size budgets passed.
The optional SFTP profile passed an earlier build. It has no device acceptance in this stage.
Focused compiler tests cover the sized process surface and exclusion of stale profile objects.
The CMake dependency test covers changed and deleted transitive headers, unchanged reuse, and paths with spaces.
The pipe EOF test exercises the production read function with zero-window probes, buffered closure, partial reads, and nonblocking calls.
The compiled exit-status test checks zero, positive, and negative codes. The native console test checks memory-table and error-message routing.
Production storage helpers passed ASan/UBSan tests for shared topology, independent states, allocation failure, synchronous copies, and lazy-mailbox races.
The production allocator harness passed 64,000 concurrent allocations, quota reservation, rollback, and cleanup checks.

The firmware ELF has 9,216 fewer static RAM bytes than the baseline. Dynamic module-state and mailbox allocations remain separate costs.
These static results do not establish net workload peaks or SSH readiness.
Artifacts, private device backups, build logs, and test outputs are under `artifacts/ssh-xip`.

A device remote-command test authenticated but then crashed. An unchanged constructor object still allocated 56 bytes for a channel whose new layout requires 76 bytes. The upstream custom compile command depended only on its C source. The CTilde wrapper now adds compiler dependency files without changing vendor sources. The rebuilt Xtensa constructor allocates 76 bytes. The next device test started and completed the child shell without a panic.

That test exposed two further defects. Native `free` output bypassed process redirection, and a 13-byte writer could not encode the 25-byte exit-status message. Both fixes passed focused host tests and subsequent device tests.

The RAM-loaded device now passes authenticated remote `free`, interactive sessions, EOF without Ctrl+D, and immediate reconnect. Device controls also pass resize during input, fragmented CSI input, Ctrl+C at the shell prompt, rekeying, a stalled 1,024-byte client window, and exit status 127 for an unknown command. Ten queued `free` commands drain without loss after the client resumes. UART remains responsive while output is stalled. A separate test disconnects a stalled client and successfully logs in again.

These checks do not prove foreground-child cancellation, separate stderr delivery, maximum transport packets, or the complete SSH checkpoint. The minimum-free counter reached 3,124 bytes during the stalled-disconnect run. This is a since-boot heap low-water mark, not an isolated workload peak. Nine repeated basic sessions passed; they do not replace the final 100-cycle gate.

The earlier working image is preserved under `artifacts/ssh-xip/ram-ssh-working`. The fallible receive candidate is preserved under `artifacts/ssh-xip/ram-fallible-receive` and is now installed. Its remote command, interactive, EOF, and reconnect tests pass. The extended controls also pass on this candidate. A legal 32,768-byte payload closes the low-memory connection, and a subsequent authenticated command succeeds. Correlation with the SD allocation-failure log remains pending; maximum-packet success with sufficient memory is untested. Final UART checks report 39,220 free heap bytes, 523 bytes attributed to the listening SSH process, and zero heap and tasks for the completed child. The since-boot minimum is 2,688 bytes. All 14 focused Draft 0.51 conformance cases pass.

## Outstanding gates

- Complete production device measurements, foreground-child cancellation, separate stderr, partial resize-write handling, maximum-packet success, and the full SSH checkpoint.
- Add small reusable RX/TX storage, checked borrowed ranges, and fallible exact-size transport overflow.
- Finish direct typed capability calls and immutable provider discovery.
- Prove reusable mapped images at different virtual addresses, then implement production XIP and cache lifecycle tests.
- Measure cache/MMU capacity, zero-write reboot reuse, firmware headroom, and mapped graph cleanup.
- Run buffer tradeoff measurements, final Fast validation, and 100 lifecycle cycles on the accepted production image.
- Complete final memory reports and documentation reconciliation.

No partition migration, commit, push, or publication is part of this work.
