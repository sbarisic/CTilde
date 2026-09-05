# SSH memory investigation, 2026-09-05

This is the historical baseline before the lower-RAM changes. Some findings below describe code that has since changed. See [current progress and evidence](DRAFT051_PROGRESS.md).

The user-provided ESP32 transcript confirms an SSH startup failure before the daemon entry point runs. The library and application load, but task creation requests an 8,192-byte stack when the largest free block is 7,680 bytes. Total free byte-addressable heap is 15,968 bytes. No successful connection is established by this evidence.

## Findings

1. `start_process_core` reserves the overlay window, loads the dependency graph, and creates module instances before calling `xTaskCreate`. The remaining heap cannot provide the requested contiguous stack. Reserving a stack earlier could avoid this specific failure, but would not increase available memory.
2. The build gate sums resident executable bytes and overlay windows. It excludes resident data, export names, process stacks, packet buffers, crypto allocations, and redirected shell resources. Passing that gate does not establish SSH readiness.
3. `ReceivePacket` allocates separate plaintext and ciphertext arrays. `aes128_gcm_open` also allocates a native ciphertext-plus-tag buffer. At the configured 35,000-byte packet limit, these three simultaneous buffers require 105,016 bytes before array headers, allocator overhead, keys, and other connection state. Sending similarly duplicates packet data and retains the caller's payload. This exceeds the free heap seen at startup.
4. The packaged `system.ssh.ctm` has 189 global function symbols. The loader retains 5,528 bytes of names and 1,512 bytes of export records, plus allocation overhead. Consolidating these allocations could reduce fragmentation. Export removal requires an audit of all runtime lookups and module bindings.
5. `sshd` permits 196,608 managed payload bytes, but that quota does not reserve physical RAM. Raising it cannot solve the failure. Native crypto scratch allocations also consume physical heap.

## Recommended implementation order

1. Measure byte-addressable free heap and largest blocks after overlay reservation, each module load, instance creation, stack allocation, key loading, handshake, and redirected shell startup. Record total memory as well as executable memory.
2. Reserve task stack storage before large module allocations. Use an explicit ownership and cleanup design for normal exit, cancelled startup, and failed loading. Keep the current stack size until real stack measurements justify a change.
3. Remove packet copies. Investigate multipart PSA AEAD operations to avoid the native combined buffer. Reuse managed packet storage or bounded slices where ownership permits. Verify authentication before exposing plaintext. Preserve protocol packet support and test malformed packets, authentication failure, cancellation, and cleanup.
4. Reduce resident and metadata allocations. Consolidate export-name storage, audit unnecessary exports, and measure additional overlay placement. Check call-transition cost and exception behavior before adopting changes.
5. Repeat hardware acceptance with Wi-Fi connected: daemon start/stop, authenticated OpenSSH session, redirected command, maximum-size packets, SFTP transfers, and repeated disconnects. Include the UART shell and its history in the memory budget.

If the measured connection cannot fit after these changes, further compiler or runtime changes are needed to meet the existing ESP32 service scope. Flash-mapped code is one proposed approach. Lowering the stack or packet limit without validation is not an established fix.

## Validation boundary

This investigation used the supplied hardware transcript, current runtime and SSH sources, and linked module symbol measurements. No firmware was flashed and no SSH connection was attempted during this investigation. Startup reservation, packet-buffer changes, and export consolidation remain proposed work.
