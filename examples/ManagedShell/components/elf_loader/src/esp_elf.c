/*
 * SPDX-FileCopyrightText: 2023-2026 Espressif Systems (Shanghai) CO LTD
 *
 * SPDX-License-Identifier: Apache-2.0
 */

#include <stdatomic.h>
#include <stdio.h>
#include <stdlib.h>
#include <string.h>
#include <sys/errno.h>
#include <sys/param.h>
#include <inttypes.h>
#include <fcntl.h>

#include "esp_log.h"
#include "esp_elf.h"
#include "soc/soc_caps.h"

#if SOC_CACHE_INTERNAL_MEM_VIA_L1CACHE
#include "hal/cache_ll.h"
#endif

#include "private/elf_platform.h"

#define stype(_s, _t)               ((_s)->type == (_t))
#define sflags(_s, _f)              (((_s)->flags & (_f)) == (_f))
#define ADDR_OFFSET                 (0x400)

#ifdef CONFIG_ELF_LOADER_NUMBER_SYMBOLS
#define SYMBOL_TABLES_NO            CONFIG_ELF_LOADER_NUMBER_SYMBOLS
#else
#define SYMBOL_TABLES_NO            (32)
#endif

#ifdef CONFIG_ELF_FILE_SYSTEM_BASE_PATH
#define FS_PATH                     CONFIG_ELF_FILE_SYSTEM_BASE_PATH
#else
#define FS_PATH                     "/storage"
#endif

static const char *TAG = "ELF";
static esp_elf_symbol_table_t *g_symbol_tables[SYMBOL_TABLES_NO];
static _Atomic(symbol_resolver) current_resolver = elf_find_sym_default;

static uint32_t read_u32_le(const uint8_t *value)
{
    return (uint32_t)value[0] | ((uint32_t)value[1] << 8) |
           ((uint32_t)value[2] << 16) | ((uint32_t)value[3] << 24);
}

/**
 * @brief Open and load an ELF file into memory.
 *
 * @param file - Pointer to elf_file_t structure to store loaded file content
 * @param name - Filename (without path) of the ELF file to open
 *
 * @return 0 on success, -1 on failure with errno set. Error cases include:
 *         - Invalid parameters
 *         - Path generation failure
 *         - File open/read errors
 *         - Memory allocation failures
 *
 * @note The actual file path will be constructed as "FS_PATH/name"
 * @note Allocates memory for file content using esp_elf_malloc()
 */
int esp_elf_open(elf_file_t *file, const char *name)
{
    ssize_t ret;
    int fd;
    char *file_path;
    off_t size;
    off_t load_size;
    uint8_t *pbuf;

    if (!file || !name) {
        errno = EINVAL;
        return -1;
    }

    ret = name[0] == '/'
        ? asprintf(&file_path, "%s", name)
        : asprintf(&file_path, FS_PATH"/%s", name);
    if (ret < 0) {
        ESP_LOGE(TAG, "Failed to generate path errno=%d", errno);
        return -1;
    }

    fd = open(file_path, O_RDONLY);
    if (fd < 0) {
        ESP_LOGE(TAG, "Failed to open file %s errno=%d", file_path, errno);
        goto errout_open_file;
    }

    size = lseek(fd, 0, SEEK_END);
    if (size == -1) {
        ESP_LOGE(TAG, "Failed to seek file %s errno=%d", file_path, errno);
        goto errout_lseek_end;
    }

    load_size = size;
    if (size >= 24) {
        uint8_t footer[24];
        ret = lseek(fd, size - (off_t)sizeof(footer), SEEK_SET);
        if (ret == size - (off_t)sizeof(footer) &&
            read(fd, footer, sizeof(footer)) == (ssize_t)sizeof(footer) &&
            memcmp(footer, "CTOVLF3\0", 8) == 0) {
            const uint32_t directory_offset = read_u32_le(footer + 8);
            const uint32_t directory_size = read_u32_le(footer + 12);
            const uint32_t resident_size = read_u32_le(footer + 16);
            if (resident_size >= sizeof(elf32_hdr_t) && resident_size <= directory_offset &&
                (uint64_t)directory_offset + directory_size + sizeof(footer) == (uint64_t)size) {
                load_size = (off_t)resident_size;
            }
        }
    }

    ret = lseek(fd, 0, SEEK_SET);
    if (ret == -1) {
        ESP_LOGE(TAG, "Failed to seek file %s errno=%d", file_path, errno);
        goto errout_lseek_end;
    }

    pbuf = esp_elf_malloc(load_size, false);
    if (!pbuf) {
        ESP_LOGE(TAG, "Failed to malloc %" PRId64 " bytes", (int64_t)load_size);
        goto errout_lseek_end;
    }

    ret = read(fd, pbuf, load_size);
    if (ret != (ssize_t)load_size) {
        ESP_LOGE(TAG, "Failed to read ret=%zd", ret);
        goto errout_read_fs;
    }

    free(file_path);
    close(fd);
    file->payload = pbuf;
    file->size = load_size;

    return 0;

errout_read_fs:
    esp_elf_free(pbuf);
errout_lseek_end:
    close(fd);
errout_open_file:
    free(file_path);
    return -1;
}

/**
 * @brief Close ELF file and release associated resources.
 *
 * @param file - Pointer to opened elf_file_t structure
 *
 * @note Releases memory allocated by esp_elf_open() for payload data
 * @note Should be called paired with esp_elf_open() to prevent memory leaks
 * @note If file is NULL, this function does nothing (null-safe)
 */
void esp_elf_close(elf_file_t *file)
{
    if (!file) {
        return;
    }

    esp_elf_free(file->payload);
    file->payload = NULL;
    file->size = 0;
}

/**
 * @brief Find symbol address by name.
 *
 * @param sym_name - Symbol name
 *
 * @return Symbol address if success or 0 if failed.
 */
uintptr_t elf_find_sym(const char *sym_name)
{
    if (!sym_name) {
        ESP_LOGE(TAG, "Invalid parameter: sym_name is NULL");
        return 0;
    }

    symbol_resolver resolver = atomic_load(&current_resolver);
    return resolver(sym_name);
}

#if CONFIG_ELF_LOADER_BUS_ADDRESS_MIRROR

/**
 * @brief Load ELF section.
 *
 * @param elf - ELF object pointer
 * @param pbuf - ELF data buffer
 *
 * @return ESP_OK if success or other if failed.
 */
static int esp_elf_load_section(esp_elf_t *elf, const uint8_t *pbuf)
{
    uint32_t entry;
    uint32_t size;

    const elf32_hdr_t *ehdr = (const elf32_hdr_t *)pbuf;
    const elf32_shdr_t *shdr = (const elf32_shdr_t *)(pbuf + ehdr->shoff);
    const char *shstrab = (const char *)pbuf + shdr[ehdr->shstrndx].offset;

    /* Calculate ELF image size */

    for (uint32_t i = 0; i < ehdr->shnum; i++) {
        const char *name = shstrab + shdr[i].name;

        if (stype(&shdr[i], SHT_PROGBITS) && sflags(&shdr[i], SHF_ALLOC)) {
            if (sflags(&shdr[i], SHF_EXECINSTR) && !strcmp(ELF_TEXT, name)) {
                ESP_LOGD(TAG, ".text   sec addr=0x%08x size=0x%08x offset=0x%08x",
                         shdr[i].addr, shdr[i].size, shdr[i].offset);

                elf->sec[ELF_SEC_TEXT].v_addr  = shdr[i].addr;
                elf->sec[ELF_SEC_TEXT].size    = ELF_ALIGN(shdr[i].size, 4);
                elf->sec[ELF_SEC_TEXT].offset  = shdr[i].offset;

                ESP_LOGD(TAG, ".text   offset is 0x%lx size is 0x%x",
                         elf->sec[ELF_SEC_TEXT].offset,
                         elf->sec[ELF_SEC_TEXT].size);
            } else if (sflags(&shdr[i], SHF_WRITE) && !strcmp(ELF_DATA, name)) {
                ESP_LOGD(TAG, ".data   sec addr=0x%08x size=0x%08x offset=0x%08x",
                         shdr[i].addr, shdr[i].size, shdr[i].offset);

                elf->sec[ELF_SEC_DATA].v_addr  = shdr[i].addr;
                elf->sec[ELF_SEC_DATA].size    = shdr[i].size;
                elf->sec[ELF_SEC_DATA].offset  = shdr[i].offset;

                ESP_LOGD(TAG, ".data   offset is 0x%lx size is 0x%x",
                         elf->sec[ELF_SEC_DATA].offset,
                         elf->sec[ELF_SEC_DATA].size);
            } else if (!strcmp(ELF_RODATA, name)) {
                ESP_LOGD(TAG, ".rodata sec addr=0x%08x size=0x%08x offset=0x%08x",
                         shdr[i].addr, shdr[i].size, shdr[i].offset);

                elf->sec[ELF_SEC_RODATA].v_addr  = shdr[i].addr;
                elf->sec[ELF_SEC_RODATA].size    = shdr[i].size;
                elf->sec[ELF_SEC_RODATA].offset  = shdr[i].offset;

                ESP_LOGD(TAG, ".rodata offset is 0x%lx size is 0x%x",
                         elf->sec[ELF_SEC_RODATA].offset,
                         elf->sec[ELF_SEC_RODATA].size);
            } else if (!strcmp(ELF_DATA_REL_RO, name)) {
                ESP_LOGD(TAG, ".data.rel.ro sec addr=0x%08x size=0x%08x offset=0x%08x",
                         shdr[i].addr, shdr[i].size, shdr[i].offset);

                elf->sec[ELF_SEC_DRLRO].v_addr  = shdr[i].addr;
                elf->sec[ELF_SEC_DRLRO].size    = shdr[i].size;
                elf->sec[ELF_SEC_DRLRO].offset  = shdr[i].offset;

                ESP_LOGD(TAG, ".data.rel.ro offset is 0x%lx size is 0x%x",
                         elf->sec[ELF_SEC_DRLRO].offset,
                         elf->sec[ELF_SEC_DRLRO].size);
            }
        } else if (stype(&shdr[i], SHT_NOBITS) &&
                   sflags(&shdr[i], SHF_ALLOC | SHF_WRITE) &&
                   !strcmp(ELF_BSS, name)) {
            ESP_LOGD(TAG, ".bss    sec addr=0x%08x size=0x%08x offset=0x%08x",
                     shdr[i].addr, shdr[i].size, shdr[i].offset);

            elf->sec[ELF_SEC_BSS].v_addr  = shdr[i].addr;
            elf->sec[ELF_SEC_BSS].size    = shdr[i].size;
            elf->sec[ELF_SEC_BSS].offset  = shdr[i].offset;

            ESP_LOGD(TAG, ".bss    offset is 0x%lx size is 0x%x",
                     elf->sec[ELF_SEC_BSS].offset,
                     elf->sec[ELF_SEC_BSS].size);
        }
    }

    /* No .text on image */

    if (!elf->sec[ELF_SEC_TEXT].size) {
        return -EINVAL;
    }

    elf->ptext = esp_elf_malloc(elf->sec[ELF_SEC_TEXT].size, true);
    if (!elf->ptext) {
        ESP_LOGE(TAG, "Failed to malloc %"PRIu32" bytes for text section",
                 (uint32_t)elf->sec[ELF_SEC_TEXT].size);
        return -ENOMEM;
    }

    size = elf->sec[ELF_SEC_DATA].size +
           elf->sec[ELF_SEC_RODATA].size +
           elf->sec[ELF_SEC_BSS].size +
           elf->sec[ELF_SEC_DRLRO].size;
    if (size) {
        elf->pdata = esp_elf_malloc(size, false);
        if (!elf->pdata) {
            ESP_LOGE(TAG, "Failed to malloc %"PRIu32" bytes for data section", size);
            esp_elf_free(elf->ptext);
            return -ENOMEM;
        }
    }

    /* Dump ".text" from ELF to executable space memory */

    elf->sec[ELF_SEC_TEXT].addr = (Elf32_Addr)elf->ptext;
    memcpy(elf->ptext, pbuf + elf->sec[ELF_SEC_TEXT].offset,
           elf->sec[ELF_SEC_TEXT].size);

#ifdef CONFIG_ELF_LOADER_SET_MMU
    if (esp_elf_arch_init_mmu(elf)) {
        esp_elf_free(elf->ptext);
        esp_elf_free(elf->pdata);
        return -EIO;
    }
#endif

    /**
     * Dump ".data", ".rodata" and ".bss" from ELF to R/W space memory.
     *
     * Todo: Dump ".rodata" to rodata section by MMU/MPU.
     */

    if (size) {
        uint8_t *pdata = elf->pdata;

        if (elf->sec[ELF_SEC_DATA].size) {
            elf->sec[ELF_SEC_DATA].addr = (uint32_t)pdata;

            memcpy(pdata, pbuf + elf->sec[ELF_SEC_DATA].offset,
                   elf->sec[ELF_SEC_DATA].size);

            pdata += elf->sec[ELF_SEC_DATA].size;
        }

        if (elf->sec[ELF_SEC_RODATA].size) {
            elf->sec[ELF_SEC_RODATA].addr = (uint32_t)pdata;

            memcpy(pdata, pbuf + elf->sec[ELF_SEC_RODATA].offset,
                   elf->sec[ELF_SEC_RODATA].size);

            pdata += elf->sec[ELF_SEC_RODATA].size;
        }

        if (elf->sec[ELF_SEC_DRLRO].size) {
            elf->sec[ELF_SEC_DRLRO].addr = (uint32_t)pdata;

            memcpy(pdata, pbuf + elf->sec[ELF_SEC_DRLRO].offset,
                   elf->sec[ELF_SEC_DRLRO].size);

            pdata += elf->sec[ELF_SEC_DRLRO].size;
        }

        if (elf->sec[ELF_SEC_BSS].size) {
            elf->sec[ELF_SEC_BSS].addr = (uint32_t)pdata;
            memset(pdata, 0, elf->sec[ELF_SEC_BSS].size);
        }
    }

    /* Set ELF entry */

    entry = ehdr->entry + elf->sec[ELF_SEC_TEXT].addr -
            elf->sec[ELF_SEC_TEXT].v_addr;

#ifdef CONFIG_ELF_LOADER_CACHE_OFFSET
    elf->entry = (void *)elf_remap_text(elf, (uintptr_t)entry);
#else
    elf->entry = (void *)entry;
#endif

    return 0;
}

#else

/**
 * @brief Load ELF segment.
 *
 * @param elf - ELF object pointer
 * @param pbuf - ELF data buffer
 *
 * @return ESP_OK if success or other if failed.
 */
static int esp_elf_load_segment(esp_elf_t *elf, const uint8_t *pbuf)
{
    uint32_t size;
    bool first_segment = false;
    Elf32_Addr vaddr_s = 0;
    Elf32_Addr vaddr_e = 0;

    const elf32_hdr_t *ehdr = (const elf32_hdr_t *)pbuf;
    const elf32_phdr_t *phdr = (const elf32_phdr_t *)(pbuf + ehdr->phoff);

    for (int i = 0; i < ehdr->phnum; i++) {
        if (phdr[i].type != PT_LOAD) {
            continue;
        }

        if (phdr[i].memsz < phdr[i].filesz) {
            ESP_LOGE(TAG, "Invalid segment[%d], memsz: %d, filesz: %d",
                     i, phdr[i].memsz, phdr[i].filesz);
            return -EINVAL;
        }

        if (first_segment == true) {
            vaddr_s = phdr[i].vaddr;
            vaddr_e = phdr[i].vaddr + phdr[i].memsz;
            first_segment = true;
            if (vaddr_e < vaddr_s) {
                ESP_LOGE(TAG, "Invalid segment[%d], vaddr: 0x%x, memsz: %d",
                         i, phdr[i].vaddr, phdr[i].memsz);
                return -EINVAL;
            }
        } else {
            if (phdr[i].vaddr < vaddr_e) {
                ESP_LOGE(TAG, "Invalid segment[%d], should not overlap, vaddr: 0x%x, vaddr_e: 0x%x",
                         i, phdr[i].vaddr, vaddr_e);
                return -EINVAL;
            }

            if (phdr[i].vaddr > vaddr_e + ADDR_OFFSET) {
                ESP_LOGI(TAG, "Too much padding before segment[%d], padding: %d",
                         i, phdr[i].vaddr - vaddr_e);
            }

            vaddr_e = phdr[i].vaddr + phdr[i].memsz;
            if (vaddr_e < phdr[i].vaddr) {
                ESP_LOGE(TAG, "Invalid segment[%d], address overflow, vaddr: 0x%x, vaddr_e: 0x%x",
                         i, phdr[i].vaddr, vaddr_e);
                return -EINVAL;
            }
        }

        ESP_LOGD(TAG, "LOAD segment[%d], vaddr: 0x%x, memsize: 0x%08x",
                 i, phdr[i].vaddr, phdr[i].memsz);
    }

    size = vaddr_e - vaddr_s;
    if (size == 0) {
        return -EINVAL;
    }

    elf->svaddr = vaddr_s;
    elf->psegment = esp_elf_malloc(size, true);
    if (!elf->psegment) {
        return -ENOMEM;
    }

    memset(elf->psegment, 0, size);

    /* Dump "PT_LOAD" from ELF to memory space */

    for (int i = 0; i < ehdr->phnum; i++) {
        if (phdr[i].type == PT_LOAD) {
            memcpy(elf->psegment + phdr[i].vaddr - vaddr_s,
                   (uint8_t *)pbuf + phdr[i].offset, phdr[i].filesz);
            ESP_LOGD(TAG, "Copy segment[%d], mem_addr: %p, size: 0x%08x",
                     i, (void *)((uint8_t *)elf->psegment + phdr[i].vaddr - vaddr_s), phdr[i].filesz);
        }
    }

#if SOC_CACHE_INTERNAL_MEM_VIA_L1CACHE
    cache_ll_writeback_all(CACHE_LL_LEVEL_INT_MEM, CACHE_TYPE_DATA, CACHE_LL_ID_ALL);
#endif

    elf->entry = (void *)((uint8_t *)elf->psegment + ehdr->entry - vaddr_s);

    return 0;
}
#endif

/**
 * @brief Override the internal symbol resolver.
 * The default resolver is based on static lists that are determined by KConfig.
 * This override allows for an arbitrary implementation.
 *
 * @param resolver the resolver function
 */
void elf_set_symbol_resolver(symbol_resolver resolver)
{
    if (!resolver) {
        ESP_LOGE(TAG, "Invalid resolver: cannot set NULL resolver");
        return;
    }

    atomic_store(&current_resolver, resolver);
}

/**
 * @brief Reset the symbol resolver to the default (static tables from KConfig).
 *
 * Equivalent to elf_set_symbol_resolver(elf_find_sym_default).
 */
void elf_reset_symbol_resolver(void)
{
    atomic_store(&current_resolver, elf_find_sym_default);
}

/**
 * @brief Map symbol's address of ELF to physic space.
 *
 * @param elf - ELF object pointer
 * @param sym - ELF symbol address
 *
 * @return ESP_OK if success or other if failed.
 */
uintptr_t esp_elf_map_sym(esp_elf_t *elf, uintptr_t sym)
{
    for (int i = 0; i < ELF_SECS; i++) {
        if ((sym >= elf->sec[i].v_addr) &&
                (sym < (elf->sec[i].v_addr + elf->sec[i].size))) {
            return sym - elf->sec[i].v_addr + elf->sec[i].addr;
        }
    }

    return 0;
}

/**
 * @brief Initialize ELF object.
 *
 * @param elf - ELF object pointer
 *
 * @return ESP_OK if success or other if failed.
 */
int esp_elf_init(esp_elf_t *elf)
{
    ESP_LOGI(TAG, "ELF loader version: %d.%d.%d", ELF_LOADER_VER_MAJOR, ELF_LOADER_VER_MINOR, ELF_LOADER_VER_PATCH);

    if (!elf) {
        return -EINVAL;
    }

    memset(elf, 0, sizeof(esp_elf_t));

    return 0;
}

/**
 * @brief Decode and relocate ELF data.
 *
 * @param elf - ELF object pointer
 * @param pbuf - ELF data buffer
 *
 * @return ESP_OK if success or other if failed.
 */
int esp_elf_relocate(esp_elf_t *elf, const uint8_t *pbuf)
{
    int ret;

    const elf32_hdr_t *ehdr;
    const elf32_shdr_t *shdr;
    const char *shstrab;
    const elf32_sym_t *symtab;
    const char *strtab;

    if (!elf || !pbuf) {
        return -EINVAL;
    }

    ehdr    = (const elf32_hdr_t *)pbuf;
    shdr    = (const elf32_shdr_t *)(pbuf + ehdr->shoff);
    shstrab = (const char *)pbuf + shdr[ehdr->shstrndx].offset;

    /* Load section or segment to memory space */

#if CONFIG_ELF_LOADER_BUS_ADDRESS_MIRROR
    ret = esp_elf_load_section(elf, pbuf);
#else
    ret = esp_elf_load_segment(elf, pbuf);
#endif

    if (ret) {
        ESP_LOGE(TAG, "Error to load elf file, ret=%d", ret);
        return ret;
    }

    ESP_LOGI(TAG, "elf->entry=%p", elf->entry);

    /* Relocation section data */

    for (uint32_t i = 0; i < ehdr->shnum; i++) {
        if (stype(&shdr[i], SHT_RELA)) {
            uint32_t nr_reloc;
            const elf32_rela_t *rela;

            nr_reloc = shdr[i].size / sizeof(elf32_rela_t);
            rela     = (const elf32_rela_t *)(pbuf + shdr[i].offset);
            symtab   = (const elf32_sym_t *)(pbuf + shdr[shdr[i].link].offset);
            strtab   = (const char *)(pbuf + shdr[shdr[shdr[i].link].link].offset);

            ESP_LOGD(TAG, "Section %s has %d symbol tables", shstrab + shdr[i].name, (int)nr_reloc);

            for (int i = 0; i < nr_reloc; i++) {
                int type;
                uintptr_t addr = 0;
                elf32_rela_t rela_buf;

                memcpy(&rela_buf, &rela[i], sizeof(elf32_rela_t));

                const elf32_sym_t *sym = &symtab[ELF_R_SYM(rela_buf.info)];

                type = ELF_R_TYPE(rela_buf.info);
                if (type == STT_COMMON || type == STT_OBJECT || type == STT_SECTION) {
                    const char *comm_name = strtab + sym->name;

                    if (comm_name[0]) {
                        addr = elf_find_sym(comm_name);
#if CONFIG_ELF_DYNAMIC_LOAD_SHARED_OBJECT
                        if (!addr && sym->shndx != SHN_UNDEF) {
#if CONFIG_ELF_LOADER_BUS_ADDRESS_MIRROR
                            /* GLOB_DAT also carries function addresses. Resolve the
                               defining section instead of assuming writable data. */
                            addr = esp_elf_map_sym(elf, sym->value);
#else
                            addr = (uintptr_t)(elf->psegment + sym->value - elf->svaddr);
#endif
                        }
#endif
                        if (!addr) {
                            ESP_LOGE(TAG, "Can't find common %s", strtab + sym->name);
                            return -ENOSYS;
                        }

                        ESP_LOGD(TAG, "Find common %s addr=%x", comm_name, addr);
                    }
                } else if (type == STT_FILE) {
                    const char *func_name = strtab + sym->name;

                    if (sym->value) {
                        addr = esp_elf_map_sym(elf, sym->value);
                    } else {
                        addr = elf_find_sym(func_name);
                    }

#if CONFIG_ELF_DYNAMIC_LOAD_SHARED_OBJECT
                    if (!addr && sym->shndx != SHN_UNDEF) {
#if CONFIG_ELF_LOADER_BUS_ADDRESS_MIRROR
                        addr = (uintptr_t)(elf->sec[ELF_SEC_TEXT].addr + sym->value - elf->sec[ELF_SEC_TEXT].v_addr);
#else
                        addr = (uintptr_t)(elf->psegment + sym->value - elf->svaddr);
#endif
                    }
#endif
                    if (!addr) {
                        ESP_LOGE(TAG, "Can't find symbol %s", func_name);
                        return -ENOSYS;
                    }

                    ESP_LOGD(TAG, "Find function %s addr=%x", func_name, addr);
                }

                esp_elf_arch_relocate(elf, &rela_buf, sym, addr);
            }
#if CONFIG_ELF_DYNAMIC_LOAD_SHARED_OBJECT
        } else {
            if (strcmp((const char *)(shstrab + shdr[i].name), ELF_DYNSYM) == 0) {
                int j;
                uint32_t len;
                uint16_t num = 0;
                elf->num = 0;
                symtab   = (const elf32_sym_t *)(pbuf + shdr[i].offset);
                strtab   = (const char *)(pbuf + shdr[shdr[i].link].offset);
                for (j = 0; j < shdr[i].size / sizeof(elf32_sym_t); j++) {
                    if ((ELF_ST_BIND(symtab[j].info) == STB_GLOBAL) &&
                            (ELF_ST_TYPE(symtab[j].info) == STT_FUNC)) {
                        elf->num++;
                    }
                }

                if (elf->num) {
                    elf->symtab = (esp_symtab_t *)esp_elf_malloc(elf->num * sizeof(esp_symtab_t), false);
                    if (!elf->symtab) {
                        ESP_LOGE(TAG, "Failed to malloc for symbol table");
                        return -ENOMEM;
                    }

                    memset(elf->symtab, 0, elf->num * sizeof(esp_symtab_t));
                }

                for (j = 0; j < shdr[i].size / sizeof(elf32_sym_t); j++) {
                    if ((ELF_ST_BIND(symtab[j].info) == STB_GLOBAL) &&
                            (ELF_ST_TYPE(symtab[j].info) == STT_FUNC)) {
                        len = strlen((const char *)(strtab + symtab[j].name)) + 1;
#if CONFIG_ELF_LOADER_BUS_ADDRESS_MIRROR
                        elf->symtab[num].addr =
                            (void *)(elf->ptext + symtab[j].value - elf->sec[ELF_SEC_TEXT].v_addr);
#else
                        elf->symtab[num].addr =
                            (void *)(elf->psegment + symtab[j].value - elf->svaddr);
#endif
                        elf->symtab[num].name = esp_elf_malloc(len, false);
                        if (!elf->symtab[num].name) {
                            ESP_LOGE(TAG, "Failed to malloc for symbol table name");
                            elf->num = num;
                            return -ENOMEM;
                        }

                        memset((void *)elf->symtab[num].name, 0, len);
                        memcpy((void *)elf->symtab[num].name, strtab + symtab[j].name, len);
                        ESP_LOGI(TAG, "elf->symtab[%d], func: %s", num, strtab + symtab[j].name);
                        num++;
                    }
                }
            }
#endif
        }
    }

#ifdef CONFIG_ELF_LOADER_LOAD_PSRAM
    esp_elf_arch_flush();
#endif

    return 0;
}

#if CONFIG_ELF_LOADER_BUS_ADDRESS_MIRROR && CONFIG_ELF_DYNAMIC_LOAD_SHARED_OBJECT
static int elf_read_at(int fd, off_t file_size, uint32_t offset, void *buffer, size_t size)
{
    if ((uint64_t)offset + size > (uint64_t)file_size ||
        lseek(fd, (off_t)offset, SEEK_SET) != (off_t)offset) {
        return -ENOEXEC;
    }
    uint8_t *destination = (uint8_t *)buffer;
    size_t completed = 0;
    while (completed < size) {
        const ssize_t count = read(fd, destination + completed, size - completed);
        if (count <= 0) return -EIO;
        completed += (size_t)count;
    }
    return 0;
}

static int elf_copy_at(int fd, off_t file_size, uint32_t offset, void *buffer, size_t size)
{
    if ((uint64_t)offset + size > (uint64_t)file_size ||
        lseek(fd, (off_t)offset, SEEK_SET) != (off_t)offset) {
        return -ENOEXEC;
    }
    uint32_t staging[128];
    uint8_t *destination = (uint8_t *)buffer;
    size_t completed = 0;
    while (completed < size) {
        const size_t requested = MIN(sizeof(staging), size - completed);
        memset(staging, 0, sizeof(staging));
        const ssize_t count = read(fd, staging, requested);
        if (count != (ssize_t)requested) return -EIO;
        volatile uint32_t *output = (volatile uint32_t *)(void *)(destination + completed);
        const size_t words = (requested + sizeof(uint32_t) - 1) / sizeof(uint32_t);
        for (size_t index = 0; index < words; ++index) output[index] = staging[index];
        completed += requested;
    }
    return 0;
}

static int elf_load_sections_from_file(
    esp_elf_t *elf,
    int fd,
    off_t file_size,
    const elf32_hdr_t *ehdr,
    const elf32_shdr_t *shdr,
    const char *shstrab,
    size_t shstrab_size)
{
    uint32_t text_file_size = 0;
    size_t section_alignment[ELF_SECS] = { 1u, 1u, 1u, 1u, 1u };
    for (uint32_t i = 0; i < ehdr->shnum; ++i) {
        if (shdr[i].name >= shstrab_size ||
            memchr(shstrab + shdr[i].name, 0, shstrab_size - shdr[i].name) == NULL) {
            return -ENOEXEC;
        }
        const char *name = shstrab + shdr[i].name;
        if (stype(&shdr[i], SHT_PROGBITS) && sflags(&shdr[i], SHF_ALLOC)) {
            if (sflags(&shdr[i], SHF_EXECINSTR) && !strcmp(ELF_TEXT, name)) {
                elf->sec[ELF_SEC_TEXT].v_addr = shdr[i].addr;
                elf->sec[ELF_SEC_TEXT].size = ELF_ALIGN(shdr[i].size, 4);
                elf->sec[ELF_SEC_TEXT].offset = shdr[i].offset;
                section_alignment[ELF_SEC_TEXT] = shdr[i].addralign == 0u ? 1u : shdr[i].addralign;
                text_file_size = shdr[i].size;
            } else if (sflags(&shdr[i], SHF_WRITE) && !strcmp(ELF_DATA, name)) {
                elf->sec[ELF_SEC_DATA].v_addr = shdr[i].addr;
                elf->sec[ELF_SEC_DATA].size = shdr[i].size;
                elf->sec[ELF_SEC_DATA].offset = shdr[i].offset;
                section_alignment[ELF_SEC_DATA] = shdr[i].addralign == 0u ? 1u : shdr[i].addralign;
            } else if (!strcmp(ELF_RODATA, name)) {
                elf->sec[ELF_SEC_RODATA].v_addr = shdr[i].addr;
                elf->sec[ELF_SEC_RODATA].size = shdr[i].size;
                elf->sec[ELF_SEC_RODATA].offset = shdr[i].offset;
                section_alignment[ELF_SEC_RODATA] = shdr[i].addralign == 0u ? 1u : shdr[i].addralign;
            } else if (!strcmp(ELF_DATA_REL_RO, name)) {
                elf->sec[ELF_SEC_DRLRO].v_addr = shdr[i].addr;
                elf->sec[ELF_SEC_DRLRO].size = shdr[i].size;
                elf->sec[ELF_SEC_DRLRO].offset = shdr[i].offset;
                section_alignment[ELF_SEC_DRLRO] = shdr[i].addralign == 0u ? 1u : shdr[i].addralign;
            }
        } else if (stype(&shdr[i], SHT_NOBITS) && sflags(&shdr[i], SHF_ALLOC | SHF_WRITE) &&
                   !strcmp(ELF_BSS, name)) {
            elf->sec[ELF_SEC_BSS].v_addr = shdr[i].addr;
            elf->sec[ELF_SEC_BSS].size = shdr[i].size;
            elf->sec[ELF_SEC_BSS].offset = shdr[i].offset;
            section_alignment[ELF_SEC_BSS] = shdr[i].addralign == 0u ? 1u : shdr[i].addralign;
        }
    }
    if (elf->sec[ELF_SEC_TEXT].size == 0 || text_file_size == 0) return -EINVAL;

    elf->ptext = esp_elf_malloc(elf->sec[ELF_SEC_TEXT].size, true);
    if (elf->ptext == NULL) return -ENOMEM;
    memset(elf->ptext, 0, elf->sec[ELF_SEC_TEXT].size);
    int result = elf_copy_at(fd, file_size, (uint32_t)elf->sec[ELF_SEC_TEXT].offset,
                             elf->ptext, text_file_size);
    if (result != 0) return result;
    elf->sec[ELF_SEC_TEXT].addr = (Elf32_Addr)elf->ptext;

    size_t section_offset[ELF_SECS] = {0};
    size_t data_size = 0u;
    const int sections[] = { ELF_SEC_DATA, ELF_SEC_RODATA, ELF_SEC_DRLRO, ELF_SEC_BSS };
    for (size_t index = 0; index < sizeof(sections) / sizeof(sections[0]); ++index) {
        const int section_index = sections[index];
        if (elf->sec[section_index].size == 0u) continue;
        const size_t alignment = section_alignment[section_index];
        if ((alignment & (alignment - 1u)) != 0u || alignment > 4096u ||
            data_size > SIZE_MAX - (alignment - 1u)) return -ENOEXEC;
        data_size = (data_size + alignment - 1u) & ~(alignment - 1u);
        section_offset[section_index] = data_size;
        if (data_size > SIZE_MAX - elf->sec[section_index].size) return -ENOEXEC;
        data_size += elf->sec[section_index].size;
    }
    if (data_size != 0) {
        elf->pdata = esp_elf_malloc(data_size, false);
        if (elf->pdata == NULL) return -ENOMEM;
        memset(elf->pdata, 0, data_size);
        for (size_t index = 0; index < sizeof(sections) / sizeof(sections[0]) - 1u; ++index) {
            const int section_index = sections[index];
            esp_elf_sec_t *section = &elf->sec[section_index];
            if (section->size == 0) continue;
            uint8_t *destination = elf->pdata + section_offset[section_index];
            section->addr = (uintptr_t)destination;
            result = elf_read_at(fd, file_size, (uint32_t)section->offset, destination, section->size);
            if (result != 0) return result;
        }
        if (elf->sec[ELF_SEC_BSS].size != 0) {
            uint8_t *destination = elf->pdata + section_offset[ELF_SEC_BSS];
            elf->sec[ELF_SEC_BSS].addr = (uintptr_t)destination;
            memset(destination, 0, elf->sec[ELF_SEC_BSS].size);
        }
    }

    uint32_t entry = ehdr->entry + elf->sec[ELF_SEC_TEXT].addr - elf->sec[ELF_SEC_TEXT].v_addr;
#ifdef CONFIG_ELF_LOADER_CACHE_OFFSET
    elf->entry = (void *)elf_remap_text(elf, (uintptr_t)entry);
#else
    elf->entry = (void *)entry;
#endif
    return 0;
}

static int elf_relocate_from_file(esp_elf_t *elf, int fd, off_t file_size)
{
    elf32_hdr_t ehdr;
    int result = elf_read_at(fd, file_size, 0, &ehdr, sizeof(ehdr));
    if (result != 0 || memcmp(ehdr.ident, "\x7f" "ELF", 4) != 0 || ehdr.ident[4] != 1 ||
        ehdr.ident[5] != 1 || ehdr.ehsize != sizeof(ehdr) || ehdr.shentsize != sizeof(elf32_shdr_t) ||
        ehdr.shnum == 0 || ehdr.shnum > 256 || ehdr.shstrndx >= ehdr.shnum ||
        (uint64_t)ehdr.shoff + (uint64_t)ehdr.shnum * sizeof(elf32_shdr_t) > (uint64_t)file_size) {
        return -ENOEXEC;
    }

    elf32_shdr_t *shdr = esp_elf_malloc((uint32_t)ehdr.shnum * sizeof(*shdr), false);
    if (shdr == NULL) return -ENOMEM;
    char *shstrab = NULL;
    elf32_sym_t *symtab = NULL;
    char *strtab = NULL;
    result = elf_read_at(fd, file_size, ehdr.shoff, shdr, (size_t)ehdr.shnum * sizeof(*shdr));
    if (result != 0) goto cleanup;

    const elf32_shdr_t *shstr_section = &shdr[ehdr.shstrndx];
    if (shstr_section->type != SHT_STRTAB || shstr_section->size == 0 || shstr_section->size > 65536) {
        result = -ENOEXEC;
        goto cleanup;
    }
    shstrab = esp_elf_malloc(shstr_section->size, false);
    if (shstrab == NULL) { result = -ENOMEM; goto cleanup; }
    result = elf_read_at(fd, file_size, shstr_section->offset, shstrab, shstr_section->size);
    if (result != 0) goto cleanup;

    result = elf_load_sections_from_file(elf, fd, file_size, &ehdr, shdr, shstrab, shstr_section->size);
    if (result != 0) goto cleanup;

    uint32_t symtab_index = UINT32_MAX;
    for (uint32_t i = 0; i < ehdr.shnum; ++i) {
        if (shdr[i].type == SHT_SYNSYM) {
            if (symtab_index != UINT32_MAX) { result = -ENOEXEC; goto cleanup; }
            symtab_index = i;
        }
    }
    if (symtab_index == UINT32_MAX || shdr[symtab_index].link >= ehdr.shnum ||
        shdr[symtab_index].size == 0 || shdr[symtab_index].size % sizeof(elf32_sym_t) != 0) {
        result = -ENOEXEC;
        goto cleanup;
    }
    const elf32_shdr_t *symbol_section = &shdr[symtab_index];
    const elf32_shdr_t *string_section = &shdr[symbol_section->link];
    if (string_section->type != SHT_STRTAB || string_section->size == 0 || string_section->size > 1024 * 1024) {
        result = -ENOEXEC;
        goto cleanup;
    }
    symtab = esp_elf_malloc(symbol_section->size, false);
    strtab = esp_elf_malloc(string_section->size, false);
    if (symtab == NULL || strtab == NULL) { result = -ENOMEM; goto cleanup; }
    result = elf_read_at(fd, file_size, symbol_section->offset, symtab, symbol_section->size);
    if (result == 0) result = elf_read_at(fd, file_size, string_section->offset, strtab, string_section->size);
    if (result != 0) goto cleanup;
    const size_t symbol_count = symbol_section->size / sizeof(*symtab);

    for (uint32_t section_index = 0; section_index < ehdr.shnum; ++section_index) {
        const elf32_shdr_t *section = &shdr[section_index];
        if (section->type != SHT_RELA) continue;
        if (section->link != symtab_index || section->size % sizeof(elf32_rela_t) != 0) {
            result = -ENOEXEC;
            goto cleanup;
        }
        const size_t relocation_count = section->size / sizeof(elf32_rela_t);
        elf32_rela_t *relocations = esp_elf_malloc(section->size, false);
        if (relocations == NULL) { result = -ENOMEM; goto cleanup; }
        result = elf_read_at(fd, file_size, section->offset, relocations, section->size);
        if (result != 0) {
            esp_elf_free(relocations);
            goto cleanup;
        }
        for (size_t relocation_index = 0; relocation_index < relocation_count; ++relocation_index) {
            const elf32_rela_t rela = relocations[relocation_index];
            const size_t symbol_index = ELF_R_SYM(rela.info);
            if (symbol_index >= symbol_count || symtab[symbol_index].name >= string_section->size ||
                memchr(strtab + symtab[symbol_index].name, 0,
                       string_section->size - symtab[symbol_index].name) == NULL) {
                result = -ENOEXEC;
                break;
            }
            const elf32_sym_t *symbol = &symtab[symbol_index];
            uintptr_t address = 0;
            const int type = ELF_R_TYPE(rela.info);
            if (type == STT_COMMON || type == STT_OBJECT || type == STT_SECTION) {
                const char *name = strtab + symbol->name;
                if (name[0] != 0) {
                    address = elf_find_sym(name);
                    if (!address && symbol->shndx != SHN_UNDEF) {
                        /* Function pointers can use GLOB_DAT as well as JMP_SLOT. */
                        address = esp_elf_map_sym(elf, symbol->value);
                    }
                    if (!address) {
                        ESP_LOGE(TAG, "unresolved ELF object symbol '%s'", name);
                        result = -ENOSYS;
                        break;
                    }
                }
            } else if (type == STT_FILE) {
                const char *name = strtab + symbol->name;
                if (symbol->value) address = esp_elf_map_sym(elf, symbol->value);
                else address = elf_find_sym(name);
                if (!address && symbol->shndx != SHN_UNDEF) {
                    address = (uintptr_t)(elf->sec[ELF_SEC_TEXT].addr + symbol->value -
                                          elf->sec[ELF_SEC_TEXT].v_addr);
                }
                if (!address) {
                    ESP_LOGE(TAG, "unresolved ELF function symbol '%s'", name);
                    result = -ENOSYS;
                    break;
                }
            }
            result = esp_elf_arch_relocate(elf, &rela, symbol, (uint32_t)address);
            if (result != 0) break;
        }
        esp_elf_free(relocations);
        if (result != 0) goto cleanup;
    }

    elf->num = 0;
    for (size_t index = 0; index < symbol_count; ++index) {
        if (ELF_ST_BIND(symtab[index].info) == STB_GLOBAL && ELF_ST_TYPE(symtab[index].info) == STT_FUNC)
            ++elf->num;
    }
    if (elf->num != 0) {
        elf->symtab = esp_elf_malloc((uint32_t)elf->num * sizeof(*elf->symtab), false);
        if (elf->symtab == NULL) { result = -ENOMEM; goto cleanup; }
        memset(elf->symtab, 0, (size_t)elf->num * sizeof(*elf->symtab));
        uint16_t exported = 0;
        for (size_t index = 0; index < symbol_count; ++index) {
            if (ELF_ST_BIND(symtab[index].info) != STB_GLOBAL || ELF_ST_TYPE(symtab[index].info) != STT_FUNC)
                continue;
            const char *name = strtab + symtab[index].name;
            const size_t name_length = strlen(name) + 1;
            elf->symtab[exported].addr =
                (void *)(elf->ptext + symtab[index].value - elf->sec[ELF_SEC_TEXT].v_addr);
            elf->symtab[exported].name = esp_elf_malloc(name_length, false);
            if (elf->symtab[exported].name == NULL) {
                elf->num = exported;
                result = -ENOMEM;
                goto cleanup;
            }
            memcpy(elf->symtab[exported].name, name, name_length);
            ++exported;
        }
    }
    result = 0;

cleanup:
    esp_elf_free(strtab);
    esp_elf_free(symtab);
    esp_elf_free(shstrab);
    esp_elf_free(shdr);
    return result;
}
#endif

int esp_elf_relocate_file(esp_elf_t *elf, const char *path)
{
    if (elf == NULL || path == NULL || path[0] == 0) return -EINVAL;
#if CONFIG_ELF_LOADER_BUS_ADDRESS_MIRROR && CONFIG_ELF_DYNAMIC_LOAD_SHARED_OBJECT
    int fd = open(path, O_RDONLY);
    if (fd < 0) return -errno;
    const off_t file_size = lseek(fd, 0, SEEK_END);
    if (file_size < (off_t)sizeof(elf32_hdr_t) || file_size > 4 * 1024 * 1024) {
        close(fd);
        return -ENOEXEC;
    }
    const int result = elf_relocate_from_file(elf, fd, file_size);
    close(fd);
    return result;
#else
    elf_file_t file;
    if (esp_elf_open(&file, path) < 0) return -errno;
    const int result = esp_elf_relocate(elf, file.payload);
    esp_elf_close(&file);
    return result;
#endif
}

/**
 * @brief Request running relocated ELF function.
 *
 * @param elf  - ELF object pointer
 * @param opt  - Request options
 * @param argc - Arguments number
 * @param argv - Arguments value array
 *
 * @return ESP_OK if success or other if failed.
 */
int esp_elf_request(esp_elf_t *elf, int opt, int argc, char *argv[])
{
    if (!elf || !(elf->entry)) {
        return -EINVAL;
    }

    elf->entry(argc, argv);

    return 0;
}

/**
 * @brief Deinitialize ELF object.
 *
 * @param elf - ELF object pointer
 *
 * @return None
 *
 * @note This function frees all resources allocated by esp_elf_relocate() and
 *       resets the structure to its initial state (same as after esp_elf_init()).
 */
void esp_elf_deinit(esp_elf_t *elf)
{
    if (!elf) {
        return;
    }

#if CONFIG_ELF_LOADER_BUS_ADDRESS_MIRROR
    if (elf->pdata) {
        esp_elf_free(elf->pdata);
        elf->pdata = NULL;
    }

    if (elf->ptext) {
        esp_elf_free(elf->ptext);
        elf->ptext = NULL;
    }
#else
    if (elf->psegment) {
        esp_elf_free(elf->psegment);
        elf->psegment = NULL;
    }
#endif

#ifdef CONFIG_ELF_LOADER_SET_MMU
    esp_elf_arch_deinit_mmu(elf);
#endif

#if CONFIG_ELF_DYNAMIC_LOAD_SHARED_OBJECT
    if (elf->num && elf->symtab) {
        for (int i = 0; i < elf->num; i++) {
            if (elf->symtab[i].name) {
                esp_elf_free(elf->symtab[i].name);
            }
        }

        esp_elf_free(elf->symtab);
        elf->symtab = NULL;
    }

    elf->num = 0;
#endif

    /* Reset structure to initial state (same as esp_elf_init) */
    memset(elf, 0, sizeof(esp_elf_t));
}

/**
 * @brief Print header description information of ELF.
 *
 * @param pbuf - ELF data buffer
 *
 * @return None
 */
void esp_elf_print_ehdr(const uint8_t *pbuf)
{
    const char *s_bits, *s_endian;
    const elf32_hdr_t *hdr = (const elf32_hdr_t *)pbuf;

    switch (hdr->ident[4]) {
    case 1:
        s_bits = "32-bit";
        break;
    case 2:
        s_bits = "64-bit";
        break;
    default:
        s_bits = "invalid bits";
        break;
    }

    switch (hdr->ident[5]) {
    case 1:
        s_endian = "little-endian";
        break;
    case 2:
        s_endian = "big-endian";
        break;
    default:
        s_endian = "invalid endian";
        break;
    }

    if (hdr->ident[0] == 0x7f) {
        ESP_LOGI(TAG, "%-40s %c%c%c", "Class:",                     hdr->ident[1], hdr->ident[2], hdr->ident[3]);
    }

    ESP_LOGI(TAG, "%-40s %s, %s", "Format:",                        s_bits, s_endian);
    ESP_LOGI(TAG, "%-40s %x", "Version(current):",                  hdr->ident[6]);

    ESP_LOGI(TAG, "%-40s %d", "Type:",                              hdr->type);
    ESP_LOGI(TAG, "%-40s %d", "Machine:",                           hdr->machine);
    ESP_LOGI(TAG, "%-40s %x", "Version:",                           hdr->version);
    ESP_LOGI(TAG, "%-40s %x", "Entry point address:",               hdr->entry);
    ESP_LOGI(TAG, "%-40s %x", "Start of program headers:",          hdr->phoff);
    ESP_LOGI(TAG, "%-40s %d", "Start of section headers:",          hdr->shoff);
    ESP_LOGI(TAG, "%-40s 0x%x", "Flags:",                           hdr->flags);
    ESP_LOGI(TAG, "%-40s %d", "Size of this header(bytes):",        hdr->ehsize);
    ESP_LOGI(TAG, "%-40s %d", "Size of program headers(bytes):",    hdr->phentsize);
    ESP_LOGI(TAG, "%-40s %d", "Number of program headers:",         hdr->phnum);
    ESP_LOGI(TAG, "%-40s %d", "Size of section headers(bytes):",    hdr->shentsize);
    ESP_LOGI(TAG, "%-40s %d", "Number of section headers:",         hdr->shnum);
    ESP_LOGI(TAG, "%-40s %d", "Section header string table i:",     hdr->shstrndx);
}

/**
 * @brief Print program header description information of ELF.
 *
 * @param pbuf - ELF data buffer
 *
 * @return None
 */
void esp_elf_print_phdr(const uint8_t *pbuf)
{
    const elf32_hdr_t *ehdr = (const elf32_hdr_t *)pbuf;
    const elf32_phdr_t *phdr = (const elf32_phdr_t *)((size_t)pbuf + ehdr->phoff);

    for (int i = 0; i < ehdr->phnum; i++) {
        ESP_LOGI(TAG, "%-40s %d", "type:",                          phdr->type);
        ESP_LOGI(TAG, "%-40s 0x%x", "offset:",                      phdr->offset);
        ESP_LOGI(TAG, "%-40s 0x%x", "vaddr",                        phdr->vaddr);
        ESP_LOGI(TAG, "%-40s 0x%x", "paddr:",                       phdr->paddr);
        ESP_LOGI(TAG, "%-40s %d", "filesz",                         phdr->filesz);
        ESP_LOGI(TAG, "%-40s %d", "memsz",                          phdr->memsz);
        ESP_LOGI(TAG, "%-40s %d", "flags",                          phdr->flags);
        ESP_LOGI(TAG, "%-40s 0x%x", "align",                        phdr->align);

        phdr = (const elf32_phdr_t *)((size_t)phdr + sizeof(elf32_phdr_t));
    }
}

/**
 * @brief Print section header description information of ELF.
 *
 * @param pbuf - ELF data buffer
 *
 * @return None
 */
void esp_elf_print_shdr(const uint8_t *pbuf)
{
    const elf32_hdr_t *ehdr = (const elf32_hdr_t *)pbuf;
    const elf32_shdr_t *shdr = (const elf32_shdr_t *)((size_t)pbuf + ehdr->shoff);

    for (int i = 0; i < ehdr->shnum; i++) {
        ESP_LOGI(TAG, "%-40s %d", "name:",                          shdr->name);
        ESP_LOGI(TAG, "%-40s %d", "type:",                          shdr->type);
        ESP_LOGI(TAG, "%-40s 0x%x", "flags:",                       shdr->flags);
        ESP_LOGI(TAG, "%-40s %x", "addr",                           shdr->addr);
        ESP_LOGI(TAG, "%-40s %x", "offset:",                        shdr->offset);
        ESP_LOGI(TAG, "%-40s %d", "size",                           shdr->size);
        ESP_LOGI(TAG, "%-40s 0x%x", "link",                         shdr->link);
        ESP_LOGI(TAG, "%-40s %d", "addralign",                      shdr->addralign);
        ESP_LOGI(TAG, "%-40s %d", "entsize",                        shdr->entsize);

        shdr = (const elf32_shdr_t *)((size_t)shdr + sizeof(elf32_shdr_t));
    }
}

/**
 * @brief Print section information of ELF.
 *
 * @param pbuf - ELF data buffer
 *
 * @return None
 */
void esp_elf_print_sec(esp_elf_t *elf)
{
    const char *sec_names[ELF_SECS] = {
        "text", "bss", "data", "rodata"
    };

    for (int i = 0; i < ELF_SECS; i++) {
        ESP_LOGI(TAG, "%s:   0x%08x size 0x%08x", sec_names[i], elf->sec[i].addr, elf->sec[i].size);
    }

    ESP_LOGI(TAG, "entry:  %p", elf->entry);
}

/**
 * @brief Register symbol table to global symbol tables array.
 *
 * @param symbol_table - Pointer to symbol table structure (array of esp_elfsym terminated by ESP_ELFSYM_END)
 *
 * @return 0 if success, -EINVAL if symbol_table is NULL, -EEXIST if already registered, -ENOMEM if no space.
 *
 * @note This function is not thread-safe. External synchronization must be used if calling
 *       this function concurrently from multiple threads.
 */
int esp_elf_register_symbol(esp_elf_symbol_table_t *symbol_table)
{
    if (!symbol_table) {
        return -EINVAL;
    }

    for (int i = 0; i < SYMBOL_TABLES_NO; i++) {
        if (g_symbol_tables[i] == symbol_table) {
            return -EEXIST;
        } else if (g_symbol_tables[i] == NULL) {
            g_symbol_tables[i] = symbol_table;
            return 0;
        }
    }

    return -ENOMEM;
}

/**
 * @brief Unregister symbol table from global symbol tables array.
 *
 * @param symbol_table - Pointer to symbol table structure to remove
 *
 * @return 0 if success, -EINVAL if symbol_table is NULL or symbol table not found.
 *
 * @note This function is not thread-safe. External synchronization must be used if calling
 *       this function concurrently from multiple threads.
 */
int esp_elf_unregister_symbol(esp_elf_symbol_table_t *symbol_table)
{
    if (!symbol_table) {
        return -EINVAL;
    }

    for (int i = 0; i < SYMBOL_TABLES_NO; i++) {
        if (g_symbol_tables[i] == symbol_table) {
            g_symbol_tables[i] = NULL;
            return 0;
        }
    }

    return -EINVAL;
}

/**
 * @brief Find symbol address by symbol name in registered tables.
 *
 * @param sym_name - Symbol name string to search
 *
 * @return Symbol address if found, 0 if not found.
 * @note Search order is registration order (earliest registered first).
 */
uintptr_t esp_elf_find_symbol(const char *sym_name)
{
    if (!sym_name) {
        return 0;
    }

    esp_elf_symbol_table_t *syms;
    for (int i = 0; i < SYMBOL_TABLES_NO; i++) {
        if (g_symbol_tables[i]) {
            syms = g_symbol_tables[i];
            while (syms->name) {
                if (!strcmp(syms->name, sym_name)) {
                    return (uintptr_t)syms->sym;
                }

                syms++;
            }
        }
    }

    return 0;
}
