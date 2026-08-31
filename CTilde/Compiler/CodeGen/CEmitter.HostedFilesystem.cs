namespace CTilde;

internal sealed partial class CEmitter
{
    private void EmitHostedFilesystemSupport(CWriter writer)
    {
        var metadata = Model.Types["System.IO.FileMetadata"];
        var metadataType = CTypeName(metadata.Type);
        var timestamp = Model.Types["System.IO.FileTimestamp"];
        var stringArray = NameMangler.Array(CType.String);
        string M(string name) => metadata.Fields.Single(field => field.Name == name).CAccessPath;
        string T(string name) => timestamp.Fields.Single(field => field.Name == name).CAccessPath;

        writer.WriteLine("static void ct_host_validate_path(ct_string* path, const char* operation) { (void)ct_require_nonnull(path, \"<host-io>\", 0); if (memchr(path->Data, 0, (size_t)path->Length) != NULL || !ct_host_utf8_valid(path->Data, (size_t)path->Length)) ct_host_io_throw(operation, EINVAL); }");
        writer.WriteLine("#if defined(_WIN32)");
        writer.WriteLine("static wchar_t* ct_host_wide_path(ct_string* path, const char* operation) { ct_host_validate_path(path, operation); int length = path->Length == 0 ? 0 : MultiByteToWideChar(CP_UTF8, MB_ERR_INVALID_CHARS, (const char*)(const void*)path->Data, path->Length, NULL, 0); if (length == 0 && path->Length != 0) ct_host_io_throw(operation, EILSEQ); wchar_t* result = (wchar_t*)malloc(((size_t)length + 1u) * sizeof(wchar_t)); if (result == NULL) ct_host_io_throw(operation, ENOMEM); if (length != 0) (void)MultiByteToWideChar(CP_UTF8, MB_ERR_INVALID_CHARS, (const char*)(const void*)path->Data, path->Length, result, length); result[length] = L'\\0'; return result; }");
        writer.WriteLine("static ct_string* ct_host_string_from_wide(const wchar_t* value, const char* operation) { int wide_length = (int)wcslen(value); int length = wide_length == 0 ? 0 : WideCharToMultiByte(CP_UTF8, WC_ERR_INVALID_CHARS, value, wide_length, NULL, 0, NULL, NULL); if (length == 0 && wide_length != 0) ct_host_io_throw(operation, EILSEQ); uint8_t* bytes = length == 0 ? NULL : (uint8_t*)malloc((size_t)length); if (length != 0 && bytes == NULL) ct_host_io_throw(operation, ENOMEM); if (length != 0) (void)WideCharToMultiByte(CP_UTF8, WC_ERR_INVALID_CHARS, value, wide_length, (char*)bytes, length, NULL, NULL); ct_string* result = ct_string_from_bytes(bytes, length, \"<host-io>\", 0); free(bytes); return result; }");
        writer.WriteLine("#endif");
        writer.WriteLine("uint8_t ct_host_path_separator(void)");
        writer.WriteLine("{");
        writer.WriteLine("#if defined(_WIN32)");
        writer.WriteLine("    return (uint8_t)'\\\\';");
        writer.WriteLine("#else");
        writer.WriteLine("    return (uint8_t)'/';");
        writer.WriteLine("#endif");
        writer.WriteLine("}");

        writer.WriteLine("bool ct_host_file_exists(ct_string* path)");
        writer.WriteLine("{");
        writer.WriteLine("#if defined(_WIN32)");
        writer.WriteLine("    wchar_t* native = ct_host_wide_path(path, \"File.Exists\"); SetLastError(ERROR_SUCCESS); DWORD attributes = GetFileAttributesW(native); DWORD error = GetLastError(); free(native); if (attributes == INVALID_FILE_ATTRIBUTES) { if (error == ERROR_FILE_NOT_FOUND || error == ERROR_PATH_NOT_FOUND || error == ERROR_INVALID_NAME) return false; ct_host_io_throw(\"File.Exists\", (int)error); } return (attributes & FILE_ATTRIBUTE_DIRECTORY) == 0;");
        writer.WriteLine("#else");
        writer.WriteLine("    ct_host_validate_path(path, \"File.Exists\"); struct stat value; errno = 0; if (stat((const char*)path->Data, &value) == 0) return !S_ISDIR(value.st_mode); if (errno == ENOENT || errno == ENOTDIR) return false; ct_host_io_throw(\"File.Exists\", errno == 0 ? -1 : errno);");
        writer.WriteLine("#endif");
        writer.WriteLine("}");

        writer.WriteLine("void ct_host_file_delete(ct_string* path)");
        writer.WriteLine("{");
        writer.WriteLine("#if defined(_WIN32)");
        writer.WriteLine("    wchar_t* native = ct_host_wide_path(path, \"File.Delete\"); if (!DeleteFileW(native)) { DWORD error = GetLastError(); free(native); if (error == ERROR_FILE_NOT_FOUND || error == ERROR_PATH_NOT_FOUND) return; ct_host_io_throw(\"File.Delete\", (int)error); } free(native);");
        writer.WriteLine("#else");
        writer.WriteLine("    ct_host_validate_path(path, \"File.Delete\"); if (unlink((const char*)path->Data) != 0 && errno != ENOENT) ct_host_io_throw(\"File.Delete\", errno == 0 ? -1 : errno);");
        writer.WriteLine("#endif");
        writer.WriteLine("}");

        writer.WriteLine("void ct_host_file_move(ct_string* source, ct_string* destination, bool overwrite)");
        writer.WriteLine("{");
        writer.WriteLine("#if defined(_WIN32)");
        writer.WriteLine("    wchar_t* from = ct_host_wide_path(source, \"File.Move\"); wchar_t* to = ct_host_wide_path(destination, \"File.Move\"); DWORD flags = overwrite ? MOVEFILE_REPLACE_EXISTING : 0u; if (!MoveFileExW(from, to, flags)) { DWORD error = GetLastError(); free(from); free(to); ct_host_io_throw(\"File.Move\", (int)error); } free(from); free(to);");
        writer.WriteLine("#else");
        writer.WriteLine("    ct_host_validate_path(source, \"File.Move\"); ct_host_validate_path(destination, \"File.Move\"); if (!overwrite) { struct stat existing; if (lstat((const char*)destination->Data, &existing) == 0) ct_host_io_throw(\"File.Move\", EEXIST); if (errno != ENOENT && errno != ENOTDIR) ct_host_io_throw(\"File.Move\", errno); } if (rename((const char*)source->Data, (const char*)destination->Data) != 0) ct_host_io_throw(\"File.Move\", errno == 0 ? -1 : errno);");
        writer.WriteLine("#endif");
        writer.WriteLine("}");

        writer.WriteLine("void ct_host_file_copy(ct_string* source, ct_string* destination, bool overwrite)");
        writer.WriteLine("{");
        writer.WriteLine("#if defined(_WIN32)");
        writer.WriteLine("    wchar_t* from = ct_host_wide_path(source, \"File.Copy\"); wchar_t* to = ct_host_wide_path(destination, \"File.Copy\"); if (!CopyFileW(from, to, overwrite ? FALSE : TRUE)) { DWORD error = GetLastError(); free(from); free(to); ct_host_io_throw(\"File.Copy\", (int)error); } free(from); free(to);");
        writer.WriteLine("#else");
        writer.WriteLine("    ct_host_validate_path(source, \"File.Copy\"); ct_host_validate_path(destination, \"File.Copy\"); if (!overwrite) { struct stat existing; if (lstat((const char*)destination->Data, &existing) == 0) ct_host_io_throw(\"File.Copy\", EEXIST); } FILE* input = fopen((const char*)source->Data, \"rb\"); if (input == NULL) ct_host_io_throw(\"File.Copy\", errno); FILE* output = fopen((const char*)destination->Data, \"wb\"); if (output == NULL) { int error = errno; fclose(input); ct_host_io_throw(\"File.Copy\", error); } uint8_t buffer[16384]; while (!feof(input)) { size_t count = fread(buffer, 1u, sizeof(buffer), input); if (count != 0u && fwrite(buffer, 1u, count, output) != count) { int error = errno; fclose(input); fclose(output); ct_host_io_throw(\"File.Copy\", error); } if (ferror(input)) { int error = errno; fclose(input); fclose(output); ct_host_io_throw(\"File.Copy\", error); } } if (fclose(input) != 0 || fclose(output) != 0) ct_host_io_throw(\"File.Copy\", errno == 0 ? -1 : errno);");
        writer.WriteLine("#endif");
        writer.WriteLine("}");

        writer.WriteLine($"{metadataType} ct_host_file_metadata(ct_string* path)");
        writer.WriteLine("{");
        writer.WriteLine($"    {metadataType} result = {{0}};");
        writer.WriteLine("#if defined(_WIN32)");
        writer.WriteLine("    wchar_t* native = ct_host_wide_path(path, \"File.GetMetadata\"); WIN32_FILE_ATTRIBUTE_DATA data; if (!GetFileAttributesExW(native, GetFileExInfoStandard, &data)) { DWORD error = GetLastError(); free(native); ct_host_io_throw(\"File.GetMetadata\", (int)error); } free(native); uint64_t ticks;");
        writer.WriteLine($"    result.{M("Kind")} = (data.dwFileAttributes & FILE_ATTRIBUTE_REPARSE_POINT) != 0 ? 3u : (data.dwFileAttributes & FILE_ATTRIBUTE_DIRECTORY) != 0 ? 2u : 1u; result.{M("Attributes")} = ((data.dwFileAttributes & FILE_ATTRIBUTE_READONLY) != 0 ? 1u : 0u) | ((data.dwFileAttributes & FILE_ATTRIBUTE_HIDDEN) != 0 ? 2u : 0u) | ((data.dwFileAttributes & FILE_ATTRIBUTE_DIRECTORY) != 0 ? 4u : 0u) | ((data.dwFileAttributes & FILE_ATTRIBUTE_REPARSE_POINT) != 0 ? 8u : 0u); result.{M("Length")} = (int64_t)(((uint64_t)data.nFileSizeHigh << 32) | data.nFileSizeLow);");
        writer.WriteLine($"    ticks = ((uint64_t)data.ftCreationTime.dwHighDateTime << 32) | data.ftCreationTime.dwLowDateTime; if (ticks >= UINT64_C(116444736000000000)) {{ ticks -= UINT64_C(116444736000000000); result.{M("HasCreationTime")} = true; result.{M("CreationTime")}.{T("Seconds")} = (int64_t)(ticks / UINT64_C(10000000)); result.{M("CreationTime")}.{T("Nanoseconds")} = (int32_t)((ticks % UINT64_C(10000000)) * UINT64_C(100)); }}");
        writer.WriteLine($"    ticks = ((uint64_t)data.ftLastAccessTime.dwHighDateTime << 32) | data.ftLastAccessTime.dwLowDateTime; if (ticks >= UINT64_C(116444736000000000)) {{ ticks -= UINT64_C(116444736000000000); result.{M("HasAccessTime")} = true; result.{M("AccessTime")}.{T("Seconds")} = (int64_t)(ticks / UINT64_C(10000000)); result.{M("AccessTime")}.{T("Nanoseconds")} = (int32_t)((ticks % UINT64_C(10000000)) * UINT64_C(100)); }}");
        writer.WriteLine($"    ticks = ((uint64_t)data.ftLastWriteTime.dwHighDateTime << 32) | data.ftLastWriteTime.dwLowDateTime; if (ticks >= UINT64_C(116444736000000000)) {{ ticks -= UINT64_C(116444736000000000); result.{M("HasModificationTime")} = true; result.{M("ModificationTime")}.{T("Seconds")} = (int64_t)(ticks / UINT64_C(10000000)); result.{M("ModificationTime")}.{T("Nanoseconds")} = (int32_t)((ticks % UINT64_C(10000000)) * UINT64_C(100)); }}");
        writer.WriteLine("#else");
        writer.WriteLine("    ct_host_validate_path(path, \"File.GetMetadata\"); struct stat data; if (lstat((const char*)path->Data, &data) != 0) ct_host_io_throw(\"File.GetMetadata\", errno == 0 ? -1 : errno);");
        writer.WriteLine($"    result.{M("Kind")} = S_ISLNK(data.st_mode) ? 3u : S_ISDIR(data.st_mode) ? 2u : S_ISREG(data.st_mode) ? 1u : 4u; result.{M("Attributes")} = ((data.st_mode & S_IWUSR) == 0 ? 1u : 0u) | (S_ISDIR(data.st_mode) ? 4u : 0u) | (S_ISLNK(data.st_mode) ? 8u : 0u); result.{M("Length")} = (int64_t)data.st_size;");
        writer.WriteLine($"    result.{M("HasCreationTime")} = false; result.{M("HasAccessTime")} = true; result.{M("AccessTime")}.{T("Seconds")} = (int64_t)data.st_atim.tv_sec; result.{M("AccessTime")}.{T("Nanoseconds")} = (int32_t)data.st_atim.tv_nsec; result.{M("HasModificationTime")} = true; result.{M("ModificationTime")}.{T("Seconds")} = (int64_t)data.st_mtim.tv_sec; result.{M("ModificationTime")}.{T("Nanoseconds")} = (int32_t)data.st_mtim.tv_nsec;");
        writer.WriteLine("#endif");
        writer.WriteLine("    return result;");
        writer.WriteLine("}");

        writer.WriteLine("bool ct_host_directory_exists(ct_string* path)");
        writer.WriteLine("{");
        writer.WriteLine("#if defined(_WIN32)");
        writer.WriteLine("    wchar_t* native = ct_host_wide_path(path, \"Directory.Exists\"); SetLastError(ERROR_SUCCESS); DWORD attributes = GetFileAttributesW(native); DWORD error = GetLastError(); free(native); if (attributes == INVALID_FILE_ATTRIBUTES) { if (error == ERROR_FILE_NOT_FOUND || error == ERROR_PATH_NOT_FOUND || error == ERROR_INVALID_NAME) return false; ct_host_io_throw(\"Directory.Exists\", (int)error); } return (attributes & FILE_ATTRIBUTE_DIRECTORY) != 0;");
        writer.WriteLine("#else");
        writer.WriteLine("    ct_host_validate_path(path, \"Directory.Exists\"); struct stat value; if (stat((const char*)path->Data, &value) == 0) return S_ISDIR(value.st_mode); if (errno == ENOENT || errno == ENOTDIR) return false; ct_host_io_throw(\"Directory.Exists\", errno == 0 ? -1 : errno);");
        writer.WriteLine("#endif");
        writer.WriteLine("}");

        writer.WriteLine("void ct_host_directory_create(ct_string* path)");
        writer.WriteLine("{");
        writer.WriteLine("#if defined(_WIN32)");
        writer.WriteLine("    wchar_t* native = ct_host_wide_path(path, \"Directory.CreateDirectory\"); size_t length = wcslen(native); for (size_t index = 0u; index <= length; ++index) { if (native[index] != L'\\\\' && native[index] != L'/' && native[index] != L'\\0') continue; if (index == 0u || (index == 2u && native[1] == L':')) continue; wchar_t saved = native[index]; native[index] = L'\\0'; if (!CreateDirectoryW(native, NULL)) { DWORD error = GetLastError(); if (error != ERROR_ALREADY_EXISTS) { free(native); ct_host_io_throw(\"Directory.CreateDirectory\", (int)error); } } native[index] = saved; } free(native);");
        writer.WriteLine("#else");
        writer.WriteLine("    ct_host_validate_path(path, \"Directory.CreateDirectory\"); char* native = (char*)malloc((size_t)path->Length + 1u); if (native == NULL) ct_host_io_throw(\"Directory.CreateDirectory\", ENOMEM); memcpy(native, path->Data, (size_t)path->Length + 1u); for (size_t index = 0u; index <= (size_t)path->Length; ++index) { if (native[index] != '/' && native[index] != '\\0') continue; if (index == 0u) continue; char saved = native[index]; native[index] = '\\0'; if (mkdir(native, 0777) != 0 && errno != EEXIST) { int error = errno; free(native); ct_host_io_throw(\"Directory.CreateDirectory\", error); } native[index] = saved; } free(native);");
        writer.WriteLine("#endif");
        writer.WriteLine("}");

        writer.WriteLine("void ct_host_directory_move(ct_string* source, ct_string* destination) { ct_host_file_move(source, destination, false); }");
        writer.WriteLine("void ct_host_directory_set_current(ct_string* path)");
        writer.WriteLine("{");
        writer.WriteLine("#if defined(_WIN32)");
        writer.WriteLine("    wchar_t* native = ct_host_wide_path(path, \"Directory.SetCurrentDirectory\"); if (!SetCurrentDirectoryW(native)) { DWORD error = GetLastError(); free(native); ct_host_io_throw(\"Directory.SetCurrentDirectory\", (int)error); } free(native);");
        writer.WriteLine("#else");
        writer.WriteLine("    ct_host_validate_path(path, \"Directory.SetCurrentDirectory\"); if (chdir((const char*)path->Data) != 0) ct_host_io_throw(\"Directory.SetCurrentDirectory\", errno == 0 ? -1 : errno);");
        writer.WriteLine("#endif");
        writer.WriteLine("}");
        writer.WriteLine("ct_string* ct_host_directory_get_current(void)");
        writer.WriteLine("{");
        writer.WriteLine("#if defined(_WIN32)");
        writer.WriteLine("    DWORD length = GetCurrentDirectoryW(0u, NULL); if (length == 0u) ct_host_io_throw(\"Directory.GetCurrentDirectory\", (int)GetLastError()); wchar_t* value = (wchar_t*)malloc((size_t)length * sizeof(wchar_t)); if (value == NULL) ct_host_io_throw(\"Directory.GetCurrentDirectory\", ENOMEM); if (GetCurrentDirectoryW(length, value) == 0u) { DWORD error = GetLastError(); free(value); ct_host_io_throw(\"Directory.GetCurrentDirectory\", (int)error); } ct_string* result = ct_host_string_from_wide(value, \"Directory.GetCurrentDirectory\"); free(value); return result;");
        writer.WriteLine("#else");
        writer.WriteLine("    size_t capacity = 256u; for (;;) { char* value = (char*)malloc(capacity); if (value == NULL) ct_host_io_throw(\"Directory.GetCurrentDirectory\", ENOMEM); errno = 0; if (getcwd(value, capacity) != NULL) { size_t length = strlen(value); if (!ct_host_utf8_valid((const uint8_t*)value, length)) { free(value); ct_host_io_throw(\"Directory.GetCurrentDirectory\", EILSEQ); } ct_string* result = ct_string_from_bytes((const uint8_t*)value, (int32_t)length, \"<host-io>\", 0); free(value); return result; } int error = errno; free(value); if (error != ERANGE) ct_host_io_throw(\"Directory.GetCurrentDirectory\", error); if (capacity > (size_t)INT32_MAX / 2u) ct_host_io_throw(\"Directory.GetCurrentDirectory\", ERANGE); capacity *= 2u; }");
        writer.WriteLine("#endif");
        writer.WriteLine("}");

        writer.WriteLine("static int ct_host_string_pointer_compare(const void* left, const void* right) { ct_string* a = *(ct_string* const*)left; ct_string* b = *(ct_string* const*)right; int32_t count = a->Length < b->Length ? a->Length : b->Length; int value = count == 0 ? 0 : memcmp(a->Data, b->Data, (size_t)count); return value != 0 ? value : a->Length < b->Length ? -1 : a->Length > b->Length ? 1 : 0; }");
        writer.WriteLine($"{stringArray}* ct_host_directory_entries(ct_string* path)");
        writer.WriteLine("{");
        writer.WriteLine("    ct_string** values = NULL; size_t count = 0u; size_t capacity = 0u;");
        writer.WriteLine("#if defined(_WIN32)");
        writer.WriteLine("    wchar_t* base = ct_host_wide_path(path, \"Directory.GetFileSystemEntries\"); size_t base_length = wcslen(base); bool add_separator = base_length != 0u && base[base_length - 1u] != L'\\\\' && base[base_length - 1u] != L'/'; size_t prefix_length = base_length + (add_separator ? 1u : 0u); wchar_t* prefix = (wchar_t*)malloc((prefix_length + 1u) * sizeof(wchar_t)); wchar_t* pattern = (wchar_t*)malloc((prefix_length + 2u) * sizeof(wchar_t)); if (prefix == NULL || pattern == NULL) { free(base); free(prefix); free(pattern); ct_host_io_throw(\"Directory.GetFileSystemEntries\", ENOMEM); } memcpy(prefix, base, base_length * sizeof(wchar_t)); if (add_separator) prefix[base_length] = L'\\\\'; prefix[prefix_length] = L'\\0'; memcpy(pattern, prefix, prefix_length * sizeof(wchar_t)); pattern[prefix_length] = L'*'; pattern[prefix_length + 1u] = L'\\0'; WIN32_FIND_DATAW data; HANDLE find = FindFirstFileW(pattern, &data); free(pattern); free(base); if (find == INVALID_HANDLE_VALUE) { DWORD error = GetLastError(); free(prefix); if (error == ERROR_FILE_NOT_FOUND) goto ct_entries_done; ct_host_io_throw(\"Directory.GetFileSystemEntries\", (int)error); } do { if ((data.cFileName[0] == L'.' && data.cFileName[1] == L'\\0') || (data.cFileName[0] == L'.' && data.cFileName[1] == L'.' && data.cFileName[2] == L'\\0')) continue; size_t name_length = wcslen(data.cFileName); wchar_t* full = (wchar_t*)malloc((prefix_length + name_length + 1u) * sizeof(wchar_t)); if (full == NULL) { FindClose(find); free(prefix); ct_host_io_throw(\"Directory.GetFileSystemEntries\", ENOMEM); } memcpy(full, prefix, prefix_length * sizeof(wchar_t)); memcpy(full + prefix_length, data.cFileName, (name_length + 1u) * sizeof(wchar_t)); ct_string* item = ct_host_string_from_wide(full, \"Directory.GetFileSystemEntries\"); free(full); if (count == capacity) { size_t next = capacity == 0u ? 16u : capacity * 2u; ct_string** resized = (ct_string**)realloc(values, next * sizeof(ct_string*)); if (resized == NULL) { FindClose(find); free(prefix); ct_host_io_throw(\"Directory.GetFileSystemEntries\", ENOMEM); } values = resized; capacity = next; } values[count++] = item; } while (FindNextFileW(find, &data)); { DWORD error = GetLastError(); FindClose(find); free(prefix); if (error != ERROR_NO_MORE_FILES) ct_host_io_throw(\"Directory.GetFileSystemEntries\", (int)error); }");
        writer.WriteLine("#else");
        writer.WriteLine("    ct_host_validate_path(path, \"Directory.GetFileSystemEntries\"); DIR* directory = opendir((const char*)path->Data); if (directory == NULL) ct_host_io_throw(\"Directory.GetFileSystemEntries\", errno == 0 ? -1 : errno); struct dirent* entry; while ((entry = readdir(directory)) != NULL) { if (strcmp(entry->d_name, \".\") == 0 || strcmp(entry->d_name, \"..\") == 0) continue; size_t base_length = (size_t)path->Length; bool separator = base_length != 0u && path->Data[base_length - 1u] != (uint8_t)'/'; size_t name_length = strlen(entry->d_name); size_t length = base_length + (separator ? 1u : 0u) + name_length; if (length > (size_t)INT32_MAX || !ct_host_utf8_valid((const uint8_t*)entry->d_name, name_length)) { closedir(directory); ct_host_io_throw(\"Directory.GetFileSystemEntries\", EILSEQ); } uint8_t* full = (uint8_t*)malloc(length); if (length != 0u && full == NULL) { closedir(directory); ct_host_io_throw(\"Directory.GetFileSystemEntries\", ENOMEM); } memcpy(full, path->Data, base_length); if (separator) full[base_length++] = (uint8_t)'/'; memcpy(full + base_length, entry->d_name, name_length); ct_string* item = ct_string_from_bytes(full, (int32_t)length, \"<host-io>\", 0); free(full); if (count == capacity) { size_t next = capacity == 0u ? 16u : capacity * 2u; ct_string** resized = (ct_string**)realloc(values, next * sizeof(ct_string*)); if (resized == NULL) { closedir(directory); ct_host_io_throw(\"Directory.GetFileSystemEntries\", ENOMEM); } values = resized; capacity = next; } values[count++] = item; } if (closedir(directory) != 0) ct_host_io_throw(\"Directory.GetFileSystemEntries\", errno == 0 ? -1 : errno);");
        writer.WriteLine("#endif");
        writer.WriteLine("#if defined(_WIN32)");
        writer.WriteLine("ct_entries_done:");
        writer.WriteLine("#endif");
        writer.WriteLine("    if (count > 1u) qsort(values, count, sizeof(ct_string*), ct_host_string_pointer_compare);");
        writer.WriteLine("    if (count > (size_t)INT32_MAX) ct_host_io_throw(\"Directory.GetFileSystemEntries\", ERANGE);");
        writer.WriteLine($"    {stringArray}* result = ct_new_{stringArray}((int32_t)count, \"<host-io>\", 0);");
        writer.WriteLine("    for (size_t index = 0u; index < count; ++index) result->Data[index] = values[index];");
        writer.WriteLine("    free(values);");
        writer.WriteLine("    return result;");
        writer.WriteLine("}");

        writer.WriteLine("void ct_host_directory_delete(ct_string* path, bool recursive)");
        writer.WriteLine("{");
        writer.WriteLine("    if (recursive) { " + stringArray + "* entries = ct_host_directory_entries(path); for (int32_t index = 0; index < entries->Length; ++index) { " + metadataType + " item = ct_host_file_metadata(entries->Data[index]); if ((uint8_t)item." + M("Kind") + " == 2u) ct_host_directory_delete(entries->Data[index], true); else if ((uint8_t)item." + M("Kind") + " == 3u && ((uint32_t)item." + M("Attributes") + " & 4u) != 0u) ct_host_directory_delete(entries->Data[index], false); else ct_host_file_delete(entries->Data[index]); } ct_release_fast((ct_object*)(void*)entries); }");
        writer.WriteLine("#if defined(_WIN32)");
        writer.WriteLine("    wchar_t* native = ct_host_wide_path(path, \"Directory.Delete\"); if (!RemoveDirectoryW(native)) { DWORD error = GetLastError(); free(native); ct_host_io_throw(\"Directory.Delete\", (int)error); } free(native);");
        writer.WriteLine("#else");
        writer.WriteLine("    ct_host_validate_path(path, \"Directory.Delete\"); if (rmdir((const char*)path->Data) != 0) ct_host_io_throw(\"Directory.Delete\", errno == 0 ? -1 : errno);");
        writer.WriteLine("#endif");
        writer.WriteLine("}");
    }
}
