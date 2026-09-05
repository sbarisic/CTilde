# Draft 0.51 implementation progress

Draft 0.51 is not complete. The compiler still identifies as Draft 0.50, Runtime ABI 22, and Managed Module ABI 3. Flash-mapped loading and safe spans are not available yet.

## Implemented foundation

- Module builds emit `<module>.memory.json` and a console summary. Reports separate linked executable, mutable, and constant sections from per-process stack and overlay requirements. Unknown loader, allocation, and scratch costs are explicit null values.
- Section costs include the streamed loader's code rounding and data alignment. The existing stack analyzer uses the managed entry wrapper and checks known requirements against the process stack. Unknown call boundaries remain unverified.
- Optional `CONFIG_CTILDE_MANAGED_MEMORY_TRACE` records global heap samples at module loading, process creation, stack reservation, readiness, resource cleanup, and SSH key parsing. Records include byte-addressable and executable free heap, largest blocks, and timestamps. These boundary samples do not establish continuous peaks or per-process attribution.
- `managedModule.memoryLimits` accepts `residentRamBytes`, `overlayRamBytes`, and `processStackBytes`. These limits check known linked sections, overlay windows, and the declared stack. They do not certify total runtime memory.
- Process startup creates a blocked task before loading modules. This reserves its stack and task control block. The task enters managed code only after startup succeeds. Failed startup deletes the reserved task before clearing its process slot.
- The streamed ELF loader stores export names in one allocation per module. It checks name bounds and retains cleanup support for the upstream loader's separate allocations.
- After binding, managed modules release ELF name-lookup metadata. Their descriptors retain managed contracts and callable addresses. The loaded code and data remain available.
- The loader reads relocations in 32-record batches. This replaces each complete relocation-table heap allocation with 384 bytes of stack storage.
- SSH AEAD uses multipart PSA operations with a 256-byte staging buffer. It no longer allocates a packet-sized combined ciphertext/tag buffer. Failed operations clear output, and invalid tags return the existing authentication error.
- Nano flushes terminal output through a 1,024-byte buffer instead of retaining a 32,768-byte screen buffer. This removes 31,744 bytes of output capacity without reducing document or terminal limits.

SSH now encrypts and decrypts its private packet body in place. This removes one packet-sized managed array from each encrypted send or receive. Native code rejects partial overlap and clears output on authentication failure. Packet parsing starts only after authentication succeeds. Payload copies and PSA's internal allocations remain. These changes do not establish total SSH peak RAM.

## SSH interoperability corrections

The first hardware connection reached key exchange but OpenSSH rejected the host signature. The investigation found three protocol mismatches:

- The shared-secret byte string must retain network byte order when encoded as an SSH integer. See [RFC 8731 section 3.1](https://www.rfc-editor.org/rfc/rfc8731.html#section-3.1).
- ECDSA hashes the exchange hash as its input message. The native signing function accepts the resulting digest. See [OpenSSH ECDSA signing](https://github.com/openssh/openssh-portable/blob/master/ssh-ecdsa.c).
- GCM padding aligns the encrypted body. It excludes the four-byte authenticated length field. See [OpenSSH packet construction](https://github.com/openssh/openssh-portable/blob/master/packet.c).

The corrected implementation retains the 35,000-byte packet limit. Updated fixtures cover byte order, digest inputs, and padding boundaries.

## Validation to date

- Two compiler conformance tests cover section classification, invalid ELF table bounds, manifest limits, and stack-limit rejection.
- The stack-report regression covers managed entry selection, configured stack rejection, and indirect-call uncertainty.
- All 13 ManagedShell modules and the firmware build passed with existing size budgets.
- The existing four diagnostic and eight SSH fixture checks passed.
- The integrated Fast tier passed: 285 MSVC conformance checks, 114 GCC checks, 114 Clang checks, and the editor and debug-adapter suites. Later SSH and Nano changes passed their focused checks and the complete firmware/module build. The final memory-report adjustment passed the two memory checks and the stack regression.
- The production AEAD helper was extracted and tested against host PSA crypto for lengths 0, 1, 15, 16, 17, 255, 256, 257, and 35,000. Checks cover one-shot ciphertext/tag parity, decrypt round trips, and invalid-tag output clearing.
- The same crypto checks cover exact in-place operation and reject partial overlap in both directions.
- Production ELF metadata cleanup passed AddressSanitizer and UndefinedBehaviorSanitizer checks for contiguous, separate, partial, empty, and repeated cleanup. These host checks do not establish device lifecycle acceptance.
- The production allocator harness passed 64,000 concurrent allocations plus quota reservation, failure rollback, cleanup, and thread-payload checks.
- Nano's production output sink passed sanitizer checks with 80,000 output bytes, write failure, reset, and allocation cleanup. On the ESP32, Nano opened an existing SD file, displayed the editor, and returned to the shell with Ctrl+X without saving. Hello and filesystem help also returned, and the final heap integrity check passed.
- The ESP32 without PSRAM completed 100 paired allocator/overlay cycles, quota rejection, cancellation, and forced termination. Heap integrity passed and only the shell module remained after fixture cleanup. Free heap changed from 114,368 to 113,036 bytes. This 1,332-byte drift includes shell history and network activity; the result is not a leak-free claim.
- Firmware and module storage were flashed through the ROM loader. The partition layout was unchanged. All six non-module storage files were preserved, including SSH credentials. NVS and SFTP partitions were not written.
- OpenSSH passed key exchange, host-signature verification, encrypted negotiation, and public-key acceptance. Authentication and SFTP remain pending because the existing client private key is encrypted and the local SSH agent was stopped.

Run the portable crypto check under Linux or WSL with CMake and a C compiler:

```sh
python3 Test/Test-ManagedAead.py --psa-source /path/to/tf-psa-crypto
```

To collect boundary samples, enable `CONFIG_CTILDE_MANAGED_MEMORY_TRACE` in the firmware configuration and capture the monitor output. Use identical trace settings for before/after comparisons. Extract JSON with:

```sh
python3 Test/Read-ManagedMemoryTrace.py monitor.log --output memory-samples.json
```

`process_resources_released` occurs before FreeRTOS necessarily reclaims the exiting task's stack. It is not a fully idle cleanup measurement. The trace does not yet cover managed handshake/session boundaries or native scratch attribution.

## Remaining release work

Production flash mapping and cache migration, new module format and ABI, safe spans and lifetime contracts, buffer-taking library APIs, allocation escape analysis, memory optimization profile, stronger reachability reduction, complete runtime accounting, and memory-aware placement remain unimplemented.

Failure injection at every reserved-task startup stage, complete SSH interoperability, maximum-size network packets, repeated disconnects, and full workload peak/timing comparisons remain pending. Specifications, editor support, and website changes must describe implemented behavior only.

## Local evidence

[Build measurements](DRAFT051_BUILD_MEASUREMENTS.json) compare all 13 baseline and rebuilt packages. `system.ssh` is 560 bytes smaller. Its old loader retained 7,040 bytes of lookup payload and allocated a 21,336-byte relocation buffer. The new loader releases lookup payload after binding and uses a 384-byte relocation stack buffer. These figures exclude allocator overhead and do not establish device peak RAM.

[Validation results](DRAFT051_VALIDATION.json) record completed and pending gates. A waiting-for-network SSH snapshot improved from 8,696 to 19,668 free bytes. The command sequence was the same, but shell history and network timing differed. These snapshots do not establish a continuous peak or isolate each change's contribution.

Website assets, anchors, and JavaScript syntax passed static checks. Automatic approval review rejected local HTTP server startup with `blocked by policy`, so HTTP preview validation remains pending.

`artifacts/draft051/baseline` contains the pre-change firmware, module packages, Git revision, working patch, and the previously untracked shell support files. Generated reports are beside rebuilt modules. Crypto build and test artifacts are under `artifacts/draft051`.
