// Included inside the custom action's namespace. All deletion requires explicit updater ownership and idle leases.
struct UpdateHandle {
    HANDLE value = INVALID_HANDLE_VALUE;
    explicit UpdateHandle(HANDLE handle) : value(handle) {}
    ~UpdateHandle() { if (value != INVALID_HANDLE_VALUE) CloseHandle(value); }
    UpdateHandle(const UpdateHandle&) = delete;
    UpdateHandle& operator=(const UpdateHandle&) = delete;
    bool Valid() const { return value != INVALID_HANDLE_VALUE; }
};

bool UpdateGuidName(const std::wstring& name) {
    return name.size() == 32 && name.find_first_not_of(L"0123456789abcdefABCDEF") == std::wstring::npos;
}

bool ReadUpdateFile(const std::wstring& path, std::string& text) {
    if (HasRedirectedAncestor(path)) return false;
    UpdateHandle file(CreateFileW(path.c_str(), GENERIC_READ, FILE_SHARE_READ, nullptr, OPEN_EXISTING, FILE_FLAG_OPEN_REPARSE_POINT, nullptr));
    if (!file.Valid()) return false;
    BY_HANDLE_FILE_INFORMATION info{};
    if (!GetFileInformationByHandle(file.value, &info) || info.nFileSizeHigh || info.nFileSizeLow > 8192 ||
        (info.dwFileAttributes & (FILE_ATTRIBUTE_DIRECTORY | FILE_ATTRIBUTE_REPARSE_POINT))) return false;
    text.resize(info.nFileSizeLow);
    DWORD read = 0;
    return ReadFile(file.value, text.data(), info.nFileSizeLow, &read, nullptr) && read == info.nFileSizeLow;
}

std::string UpdateDigest(const std::string& text) {
    HCRYPTPROV provider = 0;
    HCRYPTHASH hash = 0;
    if (!CryptAcquireContextW(&provider, nullptr, nullptr, PROV_RSA_AES, CRYPT_VERIFYCONTEXT | CRYPT_SILENT)) return {};
    std::string result;
    if (CryptCreateHash(provider, CALG_SHA_256, 0, 0, &hash)) {
        BYTE digest[32]{};
        DWORD size = sizeof(digest);
        if (CryptHashData(hash, reinterpret_cast<const BYTE*>(text.data()), static_cast<DWORD>(text.size()), 0) &&
            CryptGetHashParam(hash, HP_HASHVAL, digest, &size, 0) && size == sizeof(digest)) {
            for (BYTE part : digest) { result += "0123456789ABCDEF"[part >> 4]; result += "0123456789ABCDEF"[part & 15]; }
        }
        CryptDestroyHash(hash);
    }
    CryptReleaseContext(provider, 0);
    return result;
}

bool UpdateRecoverySettled(const std::wstring& root) {
    if (GetFileAttributesW((root + L"\\recovery.json.new").c_str()) != INVALID_FILE_ATTRIBUTES) return false;
    DWORD attributes = GetFileAttributesW((root + L"\\recovery.json").c_str());
    if (attributes == INVALID_FILE_ATTRIBUTES) return GetLastError() == ERROR_FILE_NOT_FOUND;
    std::string journal, receipt;
    if (!ReadUpdateFile(root + L"\\recovery.json", journal) || !ReadUpdateFile(root + L"\\.updater-recovery-settled", receipt)) return false;
    std::string digest = UpdateDigest(journal);
    return !digest.empty() && receipt == "AudioPilot settled recovery 1\n" + digest + "\n";
}

bool UpdateInstallationOwner(const std::wstring& root, const std::wstring& executable) {
    std::string marker, owner;
    const std::string prefix = "AudioPilot installed updater workspace 1\n";
    if (!ReadUpdateFile(root + L"\\.updater", marker) || marker != "AudioPilot updater workspace 1" ||
        !ReadUpdateFile(root + L"\\.updater-installation", owner) || !owner.starts_with(prefix) || owner.back() != '\n') return false;
    std::string path = owner.substr(prefix.size(), owner.size() - prefix.size() - 1);
    int size = MultiByteToWideChar(CP_UTF8, MB_ERR_INVALID_CHARS, path.data(), static_cast<int>(path.size()), nullptr, 0);
    if (!size) return false;
    std::wstring wide(static_cast<size_t>(size), L'\0');
    if (!MultiByteToWideChar(CP_UTF8, MB_ERR_INVALID_CHARS, path.data(), static_cast<int>(path.size()), wide.data(), size)) return false;
    return Equal(Normalize(wide), executable);
}

bool DeleteUpdateContents(const std::wstring& root, const std::vector<std::wstring>& last = {}) {
    if (HasRedirectedAncestor(root)) return false;
    UpdateHandle directory(CreateFileW(root.c_str(), GENERIC_READ, FILE_SHARE_READ | FILE_SHARE_WRITE, nullptr, OPEN_EXISTING,
        FILE_FLAG_BACKUP_SEMANTICS | FILE_FLAG_OPEN_REPARSE_POINT, nullptr));
    BY_HANDLE_FILE_INFORMATION information{};
    if (!directory.Valid() || !GetFileInformationByHandle(directory.value, &information) ||
        !(information.dwFileAttributes & FILE_ATTRIBUTE_DIRECTORY) || (information.dwFileAttributes & FILE_ATTRIBUTE_REPARSE_POINT)) return false;
    WIN32_FIND_DATAW entry{};
    HANDLE find = FindFirstFileW((root + L"\\*").c_str(), &entry);
    if (find == INVALID_HANDLE_VALUE) return GetLastError() == ERROR_FILE_NOT_FOUND;
    bool success = true;
    do {
        std::wstring name = entry.cFileName;
        if (name == L"." || name == L"..") continue;
        bool deferred = false;
        for (const auto& item : last) if (name == item) deferred = true;
        if (deferred) continue;
        if (entry.dwFileAttributes & FILE_ATTRIBUTE_REPARSE_POINT) { success = false; break; }
        std::wstring path = root + L"\\" + name;
        if (entry.dwFileAttributes & FILE_ATTRIBUTE_DIRECTORY) {
            if (!DeleteUpdateContents(path) || !RemoveDirectoryW(path.c_str())) { success = false; break; }
        } else if (!DeleteFileW(path.c_str())) { success = false; break; }
    } while (FindNextFileW(find, &entry));
    if (success && GetLastError() != ERROR_NO_MORE_FILES) success = false;
    FindClose(find);
    if (success) for (const auto& name : last) {
        if (!DeleteFileW((root + L"\\" + name).c_str()) && GetLastError() != ERROR_FILE_NOT_FOUND) return false;
    }
    return success;
}

bool CleanupUpdateWorkspace(const std::wstring& root, const std::wstring& executable) {
    if (!UpdateGuidName(root.substr(root.find_last_of(L'\\') + 1)) || HasRedirectedAncestor(root) || !UpdateInstallationOwner(root, executable)) return false;
    UpdateHandle directory(CreateFileW(root.c_str(), GENERIC_READ, FILE_SHARE_READ | FILE_SHARE_WRITE, nullptr, OPEN_EXISTING,
        FILE_FLAG_BACKUP_SEMANTICS | FILE_FLAG_OPEN_REPARSE_POINT, nullptr));
    if (!directory.Valid()) return false;
    UpdateHandle job(CreateFileW((root + L"\\job.lock").c_str(), GENERIC_READ | GENERIC_WRITE, FILE_SHARE_DELETE, nullptr, OPEN_ALWAYS, FILE_ATTRIBUTE_NORMAL, nullptr));
    UpdateHandle deployment(CreateFileW((root + L"\\update.lock").c_str(), GENERIC_READ | GENERIC_WRITE, FILE_SHARE_DELETE, nullptr, OPEN_ALWAYS, FILE_ATTRIBUTE_NORMAL, nullptr));
    if (!job.Valid() || !deployment.Valid() || !UpdateRecoverySettled(root) || RedirectedTree(root)) return false;
    return DeleteUpdateContents(root, { L"job.lock", L"update.lock", L".updater-installation", L".updater" });
}

void CleanupUpdateRuntimes(const std::wstring& root) {
    if (HasRedirectedAncestor(root) || GetFileAttributesW(root.c_str()) == INVALID_FILE_ATTRIBUTES) return;
    UpdateHandle pool(CreateFileW((root + L"\\runtime-pool.lock").c_str(), GENERIC_READ | GENERIC_WRITE, FILE_SHARE_DELETE, nullptr, OPEN_ALWAYS, FILE_ATTRIBUTE_NORMAL, nullptr));
    if (!pool.Valid()) return;
    WIN32_FIND_DATAW entry{};
    HANDLE find = FindFirstFileW((root + L"\\*").c_str(), &entry);
    if (find == INVALID_HANDLE_VALUE) return;
    do {
        if (!(entry.dwFileAttributes & FILE_ATTRIBUTE_DIRECTORY) || (entry.dwFileAttributes & FILE_ATTRIBUTE_REPARSE_POINT) || !UpdateGuidName(entry.cFileName)) continue;
        std::wstring path = root + L"\\" + entry.cFileName;
        std::string owner;
        if (!ReadUpdateFile(path + L"\\.pilot-runtime", owner) || owner != "AudioPilot pilot runtime 1" || RedirectedTree(path)) continue;
        bool cleaned = false;
        {
            UpdateHandle lease(CreateFileW((path + L"\\runtime.inuse").c_str(), GENERIC_READ | GENERIC_WRITE, FILE_SHARE_DELETE, nullptr, OPEN_ALWAYS, FILE_ATTRIBUTE_NORMAL, nullptr));
            if (lease.Valid()) cleaned = DeleteUpdateContents(path, { L"runtime.inuse", L".pilot-runtime" });
        }
        if (cleaned) RemoveDirectoryW(path.c_str());
    } while (FindNextFileW(find, &entry));
    FindClose(find);
    if (EmptyDirectory(root, L"runtime-pool.lock")) DeleteFileW((root + L"\\runtime-pool.lock").c_str());
}

void CleanupUpdaterState(const std::wstring& local, const std::wstring& executable) {
    if (local.empty() || executable.empty() || HasRedirectedAncestor(local)) return;
    for (const auto& channel : { L"updater", L"updater-pilot" }) {
        std::wstring area = local + L"\\" + channel;
        std::wstring installations = area + L"\\installations";
        if (HasRedirectedAncestor(installations)) continue;
        WIN32_FIND_DATAW entry{};
        HANDLE find = FindFirstFileW((installations + L"\\*").c_str(), &entry);
        if (find != INVALID_HANDLE_VALUE) {
            do {
                if (!(entry.dwFileAttributes & FILE_ATTRIBUTE_DIRECTORY) || (entry.dwFileAttributes & FILE_ATTRIBUTE_REPARSE_POINT) || !UpdateGuidName(entry.cFileName)) continue;
                std::wstring path = installations + L"\\" + entry.cFileName;
                if (CleanupUpdateWorkspace(path, executable)) RemoveDirectoryW(path.c_str());
            } while (FindNextFileW(find, &entry));
            FindClose(find);
        }
        // Shared helpers remain available when another installed copy or unfinished recovery still needs them.
        if (GetFileAttributesW(installations.c_str()) == INVALID_FILE_ATTRIBUTES || EmptyDirectory(installations)) {
            CleanupUpdateRuntimes(area + L"\\runtime");
            RemoveDirectoryW((area + L"\\runtime").c_str());
            RemoveDirectoryW(installations.c_str());
            RemoveDirectoryW(area.c_str());
        }
    }
}
