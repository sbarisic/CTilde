# Draft 0.51 implementation progress

Draft 0.51 is not complete. The current compiler uses Runtime ABI 23 and Managed Module ABI 4. Rebuild firmware and modules together. Flash-mapped loading and safe spans are not available yet.

## ABI 23 work in progress

The comparison baseline is commit `37e4349870eab9fadef636798f72cae663b4a6b1`. Saved artifacts and reports are in `artifacts/draft051-abi23`.

- Firmware and generated managed C use one runtime contract header. Capability lookup checks the identifier, major version, and minimum table size.
- Module metadata records required core and buffer capabilities. The loader checks requirements before binding or initialization. Older ABI versions require rebuilt modules.
- Managed modules call firmware helpers for byte hashing, UTF-8 validation and encoding, and integer formatting. Standalone programs retain generated implementations.
- The registry freezes at first lookup. Firmware owns each immutable table. Capabilities provide service discovery, not process isolation.
- Network, crypto, filesystem, and process capability tables reuse existing firmware services. Compiler-generated service calls remain unfinished. SSH still uses its native adapter.
- `Compare-Memory.py` compares saved firmware ELF files and module memory reports. It does not infer dynamic peaks from linked sizes.

The first helper comparison adds 152 bytes of firmware static RAM. Linked module RAM changes include `shell` −64, `sshd` +76, and `system.ssh` −48 bytes. These results do not meet the net RAM reduction gate.

The integer-formatting stage retains that firmware static RAM increase. Linked module changes against the baseline are `shell` −232, `sshd` +76, and `system.ssh` −204 bytes. These static values exclude dynamic peaks and allocator overhead. They do not establish the required workload reduction.

The baseline device passed allocator and overlay markers but rejected signed public-key authentication. Inspection found a DER conversion before PSA verification, which requires a fixed-width ECDSA signature. Removing the conversion passed the production PSA host test and authenticated OpenSSH login on the ESP32 with the existing key. Remote commands and SFTP still failed after authentication. Authentication alone does not establish SSH acceptance.

The ABI 23/4 diagnostic firmware completed 100 allocator/overlay lifecycle cycles, quota rejection, cancellation, and forced termination. Heap integrity passed and only the shell module remained after cleanup. The final sampled free heap was 114,964 bytes. This image retained a temporary console logger. Its memory samples cannot serve as a production comparison. The full Fast tier passed in 1,725.06 seconds: 285 MSVC, 114 GCC, and 114 Clang conformance checks, plus editor and debug-adapter checks.

SFTP request framing now allocates only the current request after checking its length. This removes the eager 35,004-byte input array and the second full-request copy. It accepts split headers, split bodies, and multiple requests in one input buffer while retaining the 35,000-byte request limit. A compiled production-framer test covers these cases, invalid lengths, reset, and managed cleanup. Packet arrays still allocate per request. This is an intermediate change, not the planned allocation-free packet path.

The next device test reached SFTP initialization, then rejected OpenSSH's `realpath "."` request. The path normalizer now resolves relative paths from `/sftp`. It still rejects `..`, backslashes, and embedded NULs. Compiled tests cover these cases. This path fix does not change transport packet limits.

The final production image restores SD logging and includes both SFTP fixes. It passed another 100 lifecycle cycles, quota rejection, cancellation, forced termination, and heap integrity. Sampled free heap changed from 112,600 to 110,096 bytes, including shell history. The 3,908-byte boot minimum includes an earlier failed SSH session. OpenSSH authentication passes, but remote commands and SFTP still fail after authentication. The release gates remain open.

The partition layout is unchanged. This stage does not provide cache migration artifacts or production flash mapping.

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

## Earlier foundation validation (ABI 22/3)

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

Production flash mapping and cache migration, the separated mapped-section package format, safe spans and lifetime contracts, buffer-taking library APIs, allocation escape analysis, memory optimization profile, stronger reachability reduction, complete runtime accounting, and memory-aware placement remain unimplemented. ABI 23/4 is implemented, but most shared runtime algorithms and compiler-generated service calls remain unfinished.

Failure injection at every reserved-task startup stage, complete SSH interoperability, maximum-size network packets, repeated disconnects, and full workload peak/timing comparisons remain pending. Specifications, editor support, and website changes must describe implemented behavior only.

## Local evidence

[ABI 23/4 validation](DRAFT051_ABI23_VALIDATION.json) records the current stage. The reports below describe the earlier ABI 22/3 foundation.

[Current shared-memory measurements](DRAFT051_SHARED_MEMORY.json) compare all 13 modules against `37e4349`. The production firmware adds 152 static RAM bytes. After the SFTP fixes, `system.ssh` adds 60 linked RAM bytes. This includes the framing helper and does not measure the removed eager session allocation. Net workload RAM reduction remains unverified.

[Build measurements](DRAFT051_BUILD_MEASUREMENTS.json) compare all 13 baseline and rebuilt packages. `system.ssh` is 560 bytes smaller. Its old loader retained 7,040 bytes of lookup payload and allocated a 21,336-byte relocation buffer. The new loader releases lookup payload after binding and uses a 384-byte relocation stack buffer. These figures exclude allocator overhead and do not establish device peak RAM.

[Validation results](DRAFT051_VALIDATION.json) record completed and pending gates. A waiting-for-network SSH snapshot improved from 8,696 to 19,668 free bytes. The command sequence was the same, but shell history and network timing differed. These snapshots do not establish a continuous peak or isolate each change's contribution.

Website assets, anchors, and JavaScript syntax passed static checks. The current local preview at `http://127.0.0.1:8765` also passed HTTP response checks. The earlier foundation run could not start its preview. No site was published.

`artifacts/draft051/baseline` contains the pre-change firmware, module packages, Git revision, working patch, and the previously untracked shell support files. Generated reports are beside rebuilt modules. Crypto build and test artifacts are under `artifacts/draft051`.
