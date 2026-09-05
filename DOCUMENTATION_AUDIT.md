# Draft 0.50 documentation audit

The audit covers every tracked or newly added repository-owned Markdown file, including support documents and preserved upstream material. Generated build trees, downloaded dependencies, ignored artifacts, and Git history are outside this inventory. A reviewed document is changed only when its claims or instructions need correction.

`python Test/Test-Documentation.py` records the exact inventory, SHA-256 hashes, line counts, local Markdown links and anchors, website assets, panel references, and JavaScript syntax in `artifacts/correctness-review/documentation-check.json`. External URLs are inventoried separately; this check does not claim that every remote page is available. Full command execution is limited to the validation lanes listed in the [correctness report](CORRECTNESS_REVIEW.md).

On 2026-09-05, all 32 unique HTTP URLs in that inventory responded successfully to a separate HEAD request. The results are in `artifacts/correctness-review/external-links.json`; they establish availability at check time, not the accuracy of remote content. All 20 recorded Raylib and Ryu file hashes matched their provenance documents.

| Document | Review and disposition |
| --- | --- |
| [README](README.md) | Corrected the 29-project count and native-build example description; added the correctness report and unchanged ABI identities. |
| [Language specification](LANGUAGE.md) | Checked current target, managed-call, ownership, source-module, and deferred-feature contracts; documented cancellation and retained lockfiles. |
| [C ABI](C_ABI.md) | Corrected the generated-header and native-cache claims. Runtime ABI 22 and Module ABI 3 layouts are unchanged. |
| [Architecture](ARCHITECTURE.md) | Updated callable target tracking, allocation locking, object-cache format 2, source membership, and syntax reuse. |
| [Standard library](STDLIB.md) | Corrected incremental UTF-8 validation and stable-sort traversal/complexity claims; updated XML API documentation alongside it. |
| [Implementation status](IMPLEMENTATION_STATUS.md) | Added a separate correctness follow-up. Existing dated build, benchmark, and hardware measurements remain historical. |
| [Roadmap](TODO.md) | Retained runtime-sharing and architecture research; added the measured compact-map follow-up. Acceptance work remains outstanding until its gate passes. |
| [Historical feature design](FUTURE_FEATURES.md) | Reviewed as the explicitly non-normative Draft 0.26–0.34 record. Preserved historical syntax and proposals. |
| [Cosmopolitan design](COSMOPOLITAN.md) | Clarified that Draft 0.50 retains the x64 contract; preserved historical toolchain measurements and deferred Arm64/fat stages. |
| [CLI distribution](CTilde.Cli/DISTRIBUTION.md) | Checked self-contained CLI versus external toolchain requirements; documented independent cache versioning and restore cancellation. |
| [VS Code guide](editors/vscode/README.md) | Updated source membership, syntax reuse, UNC handling, and MI parity. Distinguished source updates from installed packages. |
| [VS Code changelog](editors/vscode/CHANGELOG.md) | Added an unreleased source entry; preserved historical release entries and package version 0.15.0. |
| [VS Code support](editors/vscode/SUPPORT.md) | Reviewed requirements, diagnostic commands, and issue-reporting guidance; no change required. |
| [VS Code notices](editors/vscode/THIRD-PARTY-NOTICES.md) | Preserved upstream licenses and notices. This work changes no packaged dependency versions. |
| [Visual Studio guide](editors/visualstudio/README.md) | Corrected the 30-project total including the standard library; updated language-server and MI behavior. Retained unverified VS 2026 acceptance boundaries. |
| [Visual Studio changelog](editors/visualstudio/CHANGELOG.md) | Added an unreleased source entry; preserved package version and historical releases. |
| [Visual Studio support](editors/visualstudio/SUPPORT.md) | Checked diagnostic and support routes; direct issue links remain authoritative. |
| [Example catalog](examples/README.md) | Reconciled the 29 `Examples.sln` entries with target and runner requirements. Allocator acceptance is a separate runner fixture. |
| [Collections and geometry](examples/CollectionsAndGeometry/README.md) | Checked API names, manifest, command, and five expected True markers; no prose change required. |
| [Cosmopolitan example](examples/Cosmopolitan/README.md) | Clarified current x64 restrictions while preserving the historical introduction and external-toolchain setup. |
| [Freestanding example](examples/Freestanding/README.md) | Checked both manifests, expected exit code, runtime-provider scope, and roadmap link; no change required. |
| [Hosted I/O example](examples/HostedIo/README.md) | Checked renderer configuration, deterministic worker bands, Raylib paths, benchmark scripts, and historical-versus-current hash claims; no change required. |
| [Hosted native imports](examples/HostedNativeImport/README.md) | Corrected the stale Managed Module ABI 1 claim to ABI 3; preserved the separate native C ABI scope. |
| [Language tour](examples/LanguageTour/README.md) | Checked source features and the x64 manifest-backed run command; no change required. |
| [ManagedShell](examples/ManagedShell/README.md) | Documented separate threaded and overlay fixtures, retained non-destructive storage boundaries, and marked older artifact sizes as historical baselines. |
| [QEMU freestanding](examples/QemuFreestanding/README.md) | Checked the runner, Multiboot source, expected marker, and encoded exit status; retained its explicit minimal-kernel limits. |
| [Standard-library tour](examples/StandardLibrary/README.md) | Updated the current draft, retained byte-indexing and target restrictions, and checked the manifest-backed command. |
| [T-CAN485](examples/TCan485/README.md) | Reviewed commands, local-toolchain examples, hardware versus QEMU distinctions, and dated evidence. Historical results are not reused as acceptance for this pass. |
| [elf_loader README](examples/ManagedShell/components/elf_loader/README.md) | Preserved upstream text. CTilde-specific behavior is documented separately. |
| [elf_loader changelog](examples/ManagedShell/components/elf_loader/CHANGELOG.md) | Preserved upstream version history. |
| [Raylib provenance](third_party/raylib/6.0/README.md) | Reviewed pinned file paths, license, release identity, and hash records; no upstream changes. |
| [Ryu provenance](third_party/ryu/4c0618b0/PROVENANCE.md) | Reviewed pinned source closure and dual-license record; preserved provenance and history. |
| [Compact-map experiment](Test/Fixtures/CompactMap/README.md) | Added reproducible workload, payload-count limitations, measurements, ARC checks, and a production recommendation. |
| [CTilde loader changes](examples/ManagedShell/ELF_LOADER_CHANGES.md) | Added a separate account of CTilde's local vendor changes and target limits. |
| [Correctness report](CORRECTNESS_REVIEW.md) | Added finding dispositions, repeatable checks, evidence locations, and explicit acceptance limits. |
| [This audit](DOCUMENTATION_AUDIT.md) | Records coverage and the difference between static checks, executed commands, historical evidence, and pending gates. |

The website remains a local static preview. Its draft/ABI information, feature text, source count, examples, links, assets, and script syntax are checked against this inventory. Its managed-process excerpt now uses supported `if` syntax instead of an unsupported conditional expression. Excerpts are labeled and link to complete target-specific programs.
