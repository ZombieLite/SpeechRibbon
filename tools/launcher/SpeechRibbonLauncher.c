#ifndef UNICODE
#define UNICODE
#endif
#ifndef _UNICODE
#define _UNICODE
#endif
#include <windows.h>
#include <shellapi.h>
#include <objbase.h>
#include <bcrypt.h>
#include <stdint.h>
#include <string.h>
#include <wchar.h>

#define TRAILER_MAGIC "SRBNDL01"
#define COPY_BUFFER_SIZE (1024 * 1024)

#pragma pack(push, 1)
typedef struct BundleTrailer {
    uint64_t payloadSize;
    BYTE payloadSha256[32];
    char magic[8];
} BundleTrailer;
#pragma pack(pop)

static int fail(const wchar_t *message, int code) {
    MessageBoxW(NULL, message, L"SpeechRibbon", MB_OK | MB_ICONERROR);
    return code;
}

static BOOL ensure_directory(const wchar_t *path) {
    return CreateDirectoryW(path, NULL) || GetLastError() == ERROR_ALREADY_EXISTS;
}

static BOOL delete_tree(const wchar_t *root) {
    wchar_t pattern[MAX_PATH];
    WIN32_FIND_DATAW entry;
    if (swprintf_s(pattern, MAX_PATH, L"%s\\*", root) < 0) return FALSE;
    HANDLE find = FindFirstFileW(pattern, &entry);
    if (find != INVALID_HANDLE_VALUE) {
        do {
            if (wcscmp(entry.cFileName, L".") == 0 || wcscmp(entry.cFileName, L"..") == 0) continue;
            wchar_t path[MAX_PATH];
            if (swprintf_s(path, MAX_PATH, L"%s\\%s", root, entry.cFileName) < 0) { FindClose(find); return FALSE; }
            if ((entry.dwFileAttributes & FILE_ATTRIBUTE_DIRECTORY) != 0) {
                if ((entry.dwFileAttributes & FILE_ATTRIBUTE_REPARSE_POINT) != 0) {
                    if (!RemoveDirectoryW(path)) { FindClose(find); return FALSE; }
                } else if (!delete_tree(path)) { FindClose(find); return FALSE; }
            } else {
                SetFileAttributesW(path, FILE_ATTRIBUTE_NORMAL);
                if (!DeleteFileW(path)) { FindClose(find); return FALSE; }
            }
        } while (FindNextFileW(find, &entry));
        FindClose(find);
    } else if (GetLastError() != ERROR_FILE_NOT_FOUND) {
        return FALSE;
    }
    return RemoveDirectoryW(root) || GetLastError() == ERROR_PATH_NOT_FOUND;
}

static void cleanup_stale_launches(const wchar_t *versionRoot) {
    wchar_t pattern[MAX_PATH];
    WIN32_FIND_DATAW entry;
    if (swprintf_s(pattern, MAX_PATH, L"%s\\launcher-*", versionRoot) < 0) return;
    HANDLE find = FindFirstFileW(pattern, &entry);
    if (find == INVALID_HANDLE_VALUE) return;
    do {
        if ((entry.dwFileAttributes & FILE_ATTRIBUTE_DIRECTORY) == 0 || (entry.dwFileAttributes & FILE_ATTRIBUTE_REPARSE_POINT) != 0) continue;
        wchar_t root[MAX_PATH], lockPath[MAX_PATH];
        if (swprintf_s(root, MAX_PATH, L"%s\\%s", versionRoot, entry.cFileName) < 0) continue;
        if (swprintf_s(lockPath, MAX_PATH, L"%s\\.lock", root) < 0) continue;
        HANDLE lock = CreateFileW(lockPath, GENERIC_READ | DELETE, 0, NULL, OPEN_EXISTING, FILE_ATTRIBUTE_NORMAL, NULL);
        if (lock != INVALID_HANDLE_VALUE) {
            CloseHandle(lock);
            delete_tree(root);
        }
    } while (FindNextFileW(find, &entry));
    FindClose(find);
}

static BOOL append_quoted(wchar_t *target, size_t capacity, size_t *used, const wchar_t *value) {
    if (*used + 2 >= capacity) return FALSE;
    target[(*used)++] = L'"';
    size_t slashes = 0;
    for (const wchar_t *p = value; ; ++p) {
        if (*p == L'\\') { ++slashes; continue; }
        if (*p == L'"') {
            while (slashes-- > 0) { if (*used + 2 >= capacity) return FALSE; target[(*used)++] = L'\\'; target[(*used)++] = L'\\'; }
            if (*used + 2 >= capacity) return FALSE;
            target[(*used)++] = L'\\'; target[(*used)++] = L'"';
            slashes = 0;
            continue;
        }
        if (*p == L'\0') {
            while (slashes-- > 0) { if (*used + 2 >= capacity) return FALSE; target[(*used)++] = L'\\'; target[(*used)++] = L'\\'; }
            break;
        }
        while (slashes-- > 0) { if (*used + 1 >= capacity) return FALSE; target[(*used)++] = L'\\'; }
        slashes = 0;
        if (*used + 1 >= capacity) return FALSE;
        target[(*used)++] = *p;
    }
    if (*used + 1 >= capacity) return FALSE;
    target[(*used)++] = L'"';
    target[*used] = L'\0';
    return TRUE;
}

static BOOL extract_payload(const wchar_t *selfPath, const wchar_t *payloadPath) {
    HANDLE source = CreateFileW(selfPath, GENERIC_READ, FILE_SHARE_READ, NULL, OPEN_EXISTING, FILE_ATTRIBUTE_NORMAL, NULL);
    if (source == INVALID_HANDLE_VALUE) return FALSE;
    LARGE_INTEGER size;
    if (!GetFileSizeEx(source, &size) || size.QuadPart <= (LONGLONG)sizeof(BundleTrailer)) { CloseHandle(source); return FALSE; }
    LARGE_INTEGER trailerPosition;
    trailerPosition.QuadPart = size.QuadPart - (LONGLONG)sizeof(BundleTrailer);
    if (!SetFilePointerEx(source, trailerPosition, NULL, FILE_BEGIN)) { CloseHandle(source); return FALSE; }
    BundleTrailer trailer;
    DWORD read = 0;
    if (!ReadFile(source, &trailer, sizeof(trailer), &read, NULL) || read != sizeof(trailer) || memcmp(trailer.magic, TRAILER_MAGIC, 8) != 0 || trailer.payloadSize == 0 || trailer.payloadSize > (uint64_t)trailerPosition.QuadPart) { CloseHandle(source); return FALSE; }
    LARGE_INTEGER payloadPosition;
    payloadPosition.QuadPart = trailerPosition.QuadPart - (LONGLONG)trailer.payloadSize;
    if (!SetFilePointerEx(source, payloadPosition, NULL, FILE_BEGIN)) { CloseHandle(source); return FALSE; }

    HANDLE target = CreateFileW(payloadPath, GENERIC_WRITE, FILE_SHARE_READ, NULL, CREATE_NEW,
        FILE_ATTRIBUTE_TEMPORARY | FILE_FLAG_SEQUENTIAL_SCAN, NULL);
    if (target == INVALID_HANDLE_VALUE) { CloseHandle(source); return FALSE; }
    BCRYPT_ALG_HANDLE algorithm = NULL;
    BCRYPT_HASH_HANDLE hash = NULL;
    DWORD objectSize = 0, resultSize = 0, hashSize = 0;
    BYTE *hashObject = NULL, *buffer = NULL;
    BYTE actualHash[32];
    BOOL ok = FALSE;
    if (BCryptOpenAlgorithmProvider(&algorithm, BCRYPT_SHA256_ALGORITHM, NULL, 0) < 0) goto cleanup;
    if (BCryptGetProperty(algorithm, BCRYPT_OBJECT_LENGTH, (PUCHAR)&objectSize, sizeof(objectSize), &resultSize, 0) < 0) goto cleanup;
    if (BCryptGetProperty(algorithm, BCRYPT_HASH_LENGTH, (PUCHAR)&hashSize, sizeof(hashSize), &resultSize, 0) < 0 || hashSize != 32) goto cleanup;
    hashObject = (BYTE *)HeapAlloc(GetProcessHeap(), 0, objectSize);
    buffer = (BYTE *)HeapAlloc(GetProcessHeap(), 0, COPY_BUFFER_SIZE);
    if (!hashObject || !buffer || BCryptCreateHash(algorithm, &hash, hashObject, objectSize, NULL, 0, 0) < 0) goto cleanup;

    uint64_t remaining = trailer.payloadSize;
    while (remaining > 0) {
        DWORD requested = remaining > COPY_BUFFER_SIZE ? COPY_BUFFER_SIZE : (DWORD)remaining;
        DWORD written = 0;
        if (!ReadFile(source, buffer, requested, &read, NULL) || read != requested) goto cleanup;
        if (BCryptHashData(hash, buffer, read, 0) < 0) goto cleanup;
        if (!WriteFile(target, buffer, read, &written, NULL) || written != read) goto cleanup;
        remaining -= read;
    }
    if (BCryptFinishHash(hash, actualHash, sizeof(actualHash), 0) < 0 || memcmp(actualHash, trailer.payloadSha256, 32) != 0 || !FlushFileBuffers(target)) goto cleanup;
    ok = TRUE;

cleanup:
    if (hash) BCryptDestroyHash(hash);
    if (algorithm) BCryptCloseAlgorithmProvider(algorithm, 0);
    if (hashObject) HeapFree(GetProcessHeap(), 0, hashObject);
    if (buffer) HeapFree(GetProcessHeap(), 0, buffer);
    CloseHandle(target);
    CloseHandle(source);
    if (!ok) DeleteFileW(payloadPath);
    return ok;
}

int WINAPI wWinMain(HINSTANCE instance, HINSTANCE previous, PWSTR commandLine, int showCommand) {
    (void)instance; (void)previous; (void)commandLine;
    wchar_t temp[MAX_PATH], programRoot[MAX_PATH], versionRoot[MAX_PATH], runRoot[MAX_PATH];
    if (GetTempPathW(MAX_PATH, temp) == 0) return fail(L"Не удалось определить временную папку.", 10);
    if (swprintf_s(programRoot, MAX_PATH, L"%sspeechribbon", temp) < 0 ||
        swprintf_s(versionRoot, MAX_PATH, L"%s\\__SPEECHRIBBON_VERSION__", programRoot) < 0 ||
        !ensure_directory(programRoot) || !ensure_directory(versionRoot)) return fail(L"Не удалось подготовить временную папку.", 11);
    cleanup_stale_launches(versionRoot);

    GUID guid;
    wchar_t guidText[40];
    if (CoCreateGuid(&guid) != S_OK || StringFromGUID2(&guid, guidText, 40) == 0) return fail(L"Не удалось создать идентификатор запуска.", 12);
    guidText[wcslen(guidText) - 1] = L'\0';
    if (swprintf_s(runRoot, MAX_PATH, L"%s\\launcher-%s", versionRoot, guidText + 1) < 0 || !ensure_directory(runRoot)) return fail(L"Не удалось создать изолированную папку запуска.", 13);

    wchar_t lockPath[MAX_PATH], payloadPath[MAX_PATH], dotnetRoot[MAX_PATH], selfPath[MAX_PATH];
    swprintf_s(lockPath, MAX_PATH, L"%s\\.lock", runRoot);
    swprintf_s(payloadPath, MAX_PATH, L"%s\\SpeechRibbon.runtime.exe", runRoot);
    swprintf_s(dotnetRoot, MAX_PATH, L"%s\\dotnet", runRoot);
    HANDLE lock = CreateFileW(lockPath, GENERIC_READ | GENERIC_WRITE, 0, NULL, CREATE_NEW, FILE_ATTRIBUTE_HIDDEN, NULL);
    if (lock == INVALID_HANDLE_VALUE) { delete_tree(runRoot); return fail(L"Не удалось закрепить папку запуска.", 14); }
    if (GetModuleFileNameW(NULL, selfPath, MAX_PATH) == 0 || !extract_payload(selfPath, payloadPath)) {
        CloseHandle(lock); delete_tree(runRoot); return fail(L"Внутренний компонент SpeechRibbon повреждён.", 15);
    }
    SetEnvironmentVariableW(L"DOTNET_BUNDLE_EXTRACT_BASE_DIR", dotnetRoot);
    SetEnvironmentVariableW(L"SPEECHRIBBON_BUNDLE_PATH", selfPath);

    int argc = 0;
    LPWSTR *argv = CommandLineToArgvW(GetCommandLineW(), &argc);
    wchar_t childCommand[32768];
    size_t used = 0;
    childCommand[0] = L'\0';
    BOOL commandOk = append_quoted(childCommand, 32768, &used, payloadPath);
    for (int i = 1; commandOk && i < argc; ++i) {
        if (used + 2 >= 32768) { commandOk = FALSE; break; }
        childCommand[used++] = L' '; childCommand[used] = L'\0';
        commandOk = append_quoted(childCommand, 32768, &used, argv[i]);
    }
    if (argv) LocalFree(argv);
    if (!commandOk) { CloseHandle(lock); delete_tree(runRoot); return fail(L"Слишком длинная командная строка.", 16); }

    HANDLE job = CreateJobObjectW(NULL, NULL);
    JOBOBJECT_EXTENDED_LIMIT_INFORMATION limits = {0};
    limits.BasicLimitInformation.LimitFlags = JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE;
    if (!job || !SetInformationJobObject(job, JobObjectExtendedLimitInformation, &limits, sizeof(limits))) {
        if (job) CloseHandle(job); CloseHandle(lock); delete_tree(runRoot); return fail(L"Не удалось создать безопасную группу процессов.", 17);
    }
    STARTUPINFOW startup = {0};
    startup.cb = sizeof(startup);
    startup.dwFlags = STARTF_USESHOWWINDOW;
    startup.wShowWindow = (WORD)showCommand;
    PROCESS_INFORMATION process = {0};
    if (!CreateProcessW(payloadPath, childCommand, NULL, NULL, FALSE, CREATE_SUSPENDED | CREATE_UNICODE_ENVIRONMENT, NULL, NULL, &startup, &process) ||
        !AssignProcessToJobObject(job, process.hProcess)) {
        if (process.hProcess) TerminateProcess(process.hProcess, 18);
        if (process.hThread) CloseHandle(process.hThread);
        if (process.hProcess) CloseHandle(process.hProcess);
        CloseHandle(job); CloseHandle(lock); delete_tree(runRoot); return fail(L"Не удалось безопасно запустить SpeechRibbon.", 18);
    }
    ResumeThread(process.hThread);
    CloseHandle(process.hThread);
    WaitForSingleObject(process.hProcess, INFINITE);
    DWORD exitCode = 19;
    GetExitCodeProcess(process.hProcess, &exitCode);
    CloseHandle(process.hProcess);
    CloseHandle(job);
    CloseHandle(lock);
    if (!delete_tree(runRoot)) return fail(L"SpeechRibbon завершён, но временные файлы не удалось удалить.", 20);
    return (int)exitCode;
}
