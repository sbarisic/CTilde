# CTilde-specific loader changes

Upstream licenses, notices, and dated history remain unchanged.

The streamed loader reads relocations in batches of 32 records. Its 384-byte stack buffer replaces a heap allocation for each complete relocation section. Section bounds are checked before reads. Dynamic symbols and names remain temporary relocation inputs.

The Draft 0.51 work in progress changes streamed ELF export storage. It allocates one contiguous name buffer per module instead of one allocation per exported name. Name bounds and count limits are checked before allocation. `esp_elf_t.symtab_names` owns this buffer. Cleanup frees it once and retains the upstream cleanup path for independently allocated names.

This is an internal CTilde integration change. All firmware objects using `esp_elf_t` must be rebuilt together. It does not change the managed module descriptor ABI.

`esp_elf_discard_symbols` and `esp_dldiscard_symbols` release lookup metadata without unloading the image. The managed runtime calls this after it resolves the descriptor, runtime binding function, and optional overlay anchor. Managed imports and exports use descriptor tables; they do not need later ELF name lookup. Resolved addresses and section mapping remain valid until unload. Ordinary `dlopen` callers retain their symbol tables unless they explicitly discard them. The caller must own the handle and exclude concurrent lookup or unload.
