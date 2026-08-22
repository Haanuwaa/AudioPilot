#define WIN32_LEAN_AND_MEAN
#define NOMINMAX
#include <windows.h>
#include <msi.h>
#include <msiquery.h>
#include <shellapi.h>
#include <shlobj.h>
#include <taskschd.h>
#include <wrl/client.h>
#include <string>
#include <vector>
#include <cwctype>
#include <stdexcept>
#include <cstring>

// MSI looks up undecorated entry points; x86 __stdcall exports otherwise include @4.
#ifdef _M_IX86
#pragma comment(linker, "/EXPORT:ValidateDataFolder=_ValidateDataFolder@4")
#pragma comment(linker, "/EXPORT:RecordDataFolderOwnership=_RecordDataFolderOwnership@4")
#pragma comment(linker, "/EXPORT:CleanupOwnedStartup=_CleanupOwnedStartup@4")
#endif

using Microsoft::WRL::ComPtr;
namespace {
const wchar_t* RegistryPath = L"Software\\Haanuwaa\\AudioPilot";
const wchar_t* MarkerName = L".audiopilot-data-owner";
const char* MarkerText = "AudioPilot data directory\n8D18A00C-7CC8-487B-A462-7D3252139D72\n";

std::wstring Property(MSIHANDLE session, const wchar_t* name) {
    DWORD size = 0;
    wchar_t empty = L'\0';
    MsiGetPropertyW(session, name, &empty, &size);
    std::vector<wchar_t> value(static_cast<size_t>(size) + 1);
    ++size;
    if (MsiGetPropertyW(session, name, value.data(), &size) != ERROR_SUCCESS)
        throw std::runtime_error("MSI property read failed");
    return value.data();
}

void Message(MSIHANDLE session, const std::wstring& text, bool error = false) {
    MSIHANDLE record = MsiCreateRecord(0);
    MsiRecordSetStringW(record, 0, text.c_str());
    MsiProcessMessage(session, error ? INSTALLMESSAGE_ERROR : INSTALLMESSAGE_INFO, record);
    MsiCloseHandle(record);
}

std::wstring Normalize(const std::wstring& input) {
    if (input.size() < 3 || !iswalpha(input[0]) || input[1] != L':' ||
        (input[2] != L'\\' && input[2] != L'/') || input.find(L'\0') != std::wstring::npos)
        return {};
    for (size_t start = 3; start < input.size();) {
        size_t end = input.find_first_of(L"\\/", start);
        if (end == std::wstring::npos) end = input.size();
        if (end > start && (input[end - 1] == L'.' || input[end - 1] == L' ')) return {};
        start = end + 1;
    }
    DWORD size = GetFullPathNameW(input.c_str(), 0, nullptr, nullptr);
    if (!size) return {};
    std::vector<wchar_t> value(size);
    if (!GetFullPathNameW(input.c_str(), size, value.data(), nullptr)) return {};
    std::wstring path(value.data());
    while (path.size() > 3 && (path.back() == L'\\' || path.back() == L'/')) path.pop_back();
    if (path.find(L':', 2) != std::wstring::npos) return {};
    return path;
}

bool Equal(const std::wstring& a, const std::wstring& b) {
    return !a.empty() && !b.empty() && CompareStringOrdinal(a.c_str(), -1, b.c_str(), -1, TRUE) == CSTR_EQUAL;
}

bool Contains(const std::wstring& parent, const std::wstring& child) {
    return Equal(parent, child) || (!parent.empty() && child.size() > parent.size() && (parent.back() == L'\\' || child[parent.size()] == L'\\') &&
        CompareStringOrdinal(parent.c_str(), static_cast<int>(parent.size()), child.c_str(), static_cast<int>(parent.size()), TRUE) == CSTR_EQUAL);
}

std::wstring Known(REFKNOWNFOLDERID id) {
    PWSTR raw = nullptr;
    if (FAILED(SHGetKnownFolderPath(id, 0, nullptr, &raw))) return {};
    std::wstring value = Normalize(raw);
    CoTaskMemFree(raw);
    return value;
}

bool HasRedirectedAncestor(std::wstring path) {
    while (path.size() > 3) {
        DWORD attributes = GetFileAttributesW(path.c_str());
        if (attributes != INVALID_FILE_ATTRIBUTES && (attributes & FILE_ATTRIBUTE_REPARSE_POINT)) return true;
        path.resize(path.find_last_of(L'\\'));
    }
    return false;
}

bool SafeDataPath(const std::wstring& path, const std::wstring& install) {
    if (path.size() <= 3 || HasRedirectedAncestor(path) || (!install.empty() && (Contains(path, install) || Contains(install, path)))) return false;
    const KNOWNFOLDERID protectedFolders[] = { FOLDERID_Profile, FOLDERID_RoamingAppData, FOLDERID_LocalAppData,
        FOLDERID_Desktop, FOLDERID_Documents, FOLDERID_Downloads, FOLDERID_Pictures, FOLDERID_Music,
        FOLDERID_Videos, FOLDERID_StartMenu, FOLDERID_Programs, FOLDERID_Startup,
        FOLDERID_Public, FOLDERID_PublicDesktop, FOLDERID_PublicDocuments, FOLDERID_PublicDownloads,
        FOLDERID_PublicPictures, FOLDERID_PublicMusic, FOLDERID_PublicVideos,
        FOLDERID_CommonStartMenu, FOLDERID_CommonPrograms, FOLDERID_CommonStartup,
        FOLDERID_ProgramData, FOLDERID_ProgramFiles, FOLDERID_ProgramFilesX86, FOLDERID_Windows };
    for (const auto& id : protectedFolders) {
        std::wstring protectedPath = Known(id);
        if (!protectedPath.empty() && Contains(path, protectedPath)) return false;
    }
    for (const auto& id : { FOLDERID_Windows, FOLDERID_ProgramFiles, FOLDERID_ProgramFilesX86 }) {
        std::wstring systemPath = Known(id);
        if (!systemPath.empty() && Contains(systemPath, path)) return false;
    }
    DWORD size = GetTempPathW(0, nullptr);
    if (size) {
        std::vector<wchar_t> temporary(size);
        DWORD length = GetTempPathW(size, temporary.data());
        if (length && length < size) {
            std::wstring temporaryPath = Normalize(temporary.data());
            if (!temporaryPath.empty() && Contains(path, temporaryPath)) return false;
        }
    }
    return true;
}

bool Owned(const std::wstring& path) {
    std::wstring marker = path + L"\\" + MarkerName;
    DWORD attributes = GetFileAttributesW(marker.c_str());
    if (attributes == INVALID_FILE_ATTRIBUTES || (attributes & (FILE_ATTRIBUTE_DIRECTORY | FILE_ATTRIBUTE_REPARSE_POINT))) return false;
    HANDLE file = CreateFileW(marker.c_str(), GENERIC_READ, FILE_SHARE_READ, nullptr, OPEN_EXISTING, FILE_FLAG_OPEN_REPARSE_POINT, nullptr);
    if (file == INVALID_HANDLE_VALUE) return false;
    char buffer[128]{};
    DWORD read = 0;
    bool valid = ReadFile(file, buffer, sizeof(buffer), &read, nullptr) &&
        std::string(buffer, read) == MarkerText;
    CloseHandle(file);
    return valid;
}

bool EmptyDirectory(const std::wstring& path) {
    WIN32_FIND_DATAW data{};
    HANDLE find = FindFirstFileW((path + L"\\*").c_str(), &data);
    if (find == INVALID_HANDLE_VALUE) return GetLastError() == ERROR_FILE_NOT_FOUND;
    bool empty = true;
    do {
        if (wcscmp(data.cFileName, L".") && wcscmp(data.cFileName, L"..")) { empty = false; break; }
    } while (FindNextFileW(find, &data));
    if (empty && GetLastError() != ERROR_NO_MORE_FILES) empty = false;
    FindClose(find);
    return empty;
}

bool RedirectedTree(const std::wstring& root) {
    std::vector<std::wstring> pending{ root };
    size_t entries = 0;
    while (!pending.empty()) {
        std::wstring path = pending.back();
        pending.pop_back();
        WIN32_FIND_DATAW data{};
        HANDLE find = FindFirstFileW((path + L"\\*").c_str(), &data);
        if (find == INVALID_HANDLE_VALUE) return true;
        bool unsafe = false;
        do {
            if (!wcscmp(data.cFileName, L".") || !wcscmp(data.cFileName, L"..")) continue;
            if (++entries > 100000 || (data.dwFileAttributes & FILE_ATTRIBUTE_REPARSE_POINT)) { unsafe = true; break; }
            if (data.dwFileAttributes & FILE_ATTRIBUTE_DIRECTORY) pending.push_back(path + L"\\" + data.cFileName);
        } while (FindNextFileW(find, &data));
        if (GetLastError() != ERROR_NO_MORE_FILES && !unsafe) unsafe = true;
        FindClose(find);
        if (unsafe) return true;
    }
    return false;
}

std::wstring RegistryString(const wchar_t* key, const wchar_t* name) {
    DWORD bytes = 0;
    if (RegGetValueW(HKEY_CURRENT_USER, key, name, RRF_RT_REG_SZ | RRF_RT_REG_EXPAND_SZ | RRF_SUBKEY_WOW6464KEY, nullptr, nullptr, &bytes) != ERROR_SUCCESS) return {};
    std::vector<wchar_t> value(static_cast<size_t>(bytes) / sizeof(wchar_t) + 1);
    if (RegGetValueW(HKEY_CURRENT_USER, key, name, RRF_RT_REG_SZ | RRF_RT_REG_EXPAND_SZ | RRF_SUBKEY_WOW6464KEY, nullptr, value.data(), &bytes) != ERROR_SUCCESS) return {};
    return value.data();
}

bool OwnedCommand(const std::wstring& command, const std::wstring& executable) {
    if (command.empty()) return false;
    if (Equal(Normalize(command), executable)) return true;
    int count = 0;
    LPWSTR* args = CommandLineToArgvW(command.c_str(), &count);
    bool owned = args && count > 0 && Equal(Normalize(args[0]), executable);
    if (args) LocalFree(args);
    return owned;
}

bool OwnedTask(IRegisteredTask* task, const std::wstring& executable) {
    ComPtr<ITaskDefinition> definition;
    ComPtr<IActionCollection> actions;
    ComPtr<IAction> action;
    ComPtr<IExecAction> exec;
    LONG count = 0;
    if (FAILED(task->get_Definition(&definition)) || FAILED(definition->get_Actions(&actions)) ||
        FAILED(actions->get_Count(&count)) || count != 1 || FAILED(actions->get_Item(1, &action)) ||
        FAILED(action.As(&exec))) return false;
    BSTR path = nullptr;
    if (FAILED(exec->get_Path(&path))) return false;
    bool owned = path && Equal(Normalize(path), executable);
    SysFreeString(path);
    return owned;
}
}

extern "C" __declspec(dllexport) UINT __stdcall ValidateDataFolder(MSIHANDLE session) noexcept {
    try {
        bool removing = Property(session, L"REMOVE") == L"ALL";
        if (removing && (Property(session, L"AUDIOPILOT_CLEAN_UNINSTALL") != L"1" || !Property(session, L"UPGRADINGPRODUCTCODE").empty())) return ERROR_SUCCESS;
        std::wstring path = Normalize(removing ? RegistryString(RegistryPath, L"DataFolder") : Property(session, L"AUDIOPILOT_DATA_FOLDER"));
        std::wstring installProperty = Property(session, L"INSTALLFOLDER");
        std::wstring install = installProperty.empty() ? Known(FOLDERID_LocalAppData) + L"\\AudioPilot" : Normalize(installProperty);
        if (install.empty()) throw std::runtime_error("Unsafe installation path");
        if (!SafeDataPath(path, install)) throw std::runtime_error("Unsafe data path");
        DWORD attributes = GetFileAttributesW(path.c_str());
        bool exists = attributes != INVALID_FILE_ATTRIBUTES;
        if (exists && !(attributes & FILE_ATTRIBUTE_DIRECTORY)) throw std::runtime_error("Data path is not a directory");
        if (removing) {
            if (exists && (!Owned(path) || RedirectedTree(path))) throw std::runtime_error("Data directory ownership or redirection check failed");
            MsiSetPropertyW(session, L"AUDIOPILOT_CLEAN_UNINSTALL_FOLDER", exists ? path.c_str() : L"");
        } else {
            bool defaultFolder = Equal(path, Known(FOLDERID_RoamingAppData) + L"\\AudioPilot");
            if (exists && !Owned(path) && !EmptyDirectory(path) && !defaultFolder) throw std::runtime_error("Custom data folder is not dedicated to AudioPilot");
        }
        return ERROR_SUCCESS;
    } catch (...) {
        Message(session, L"AudioPilot could not verify a safe, dedicated data folder. Choose an empty local folder for new installs. For removal, use normal uninstall to preserve data if ownership cannot be verified.", true);
        return ERROR_INSTALL_FAILURE;
    }
}

extern "C" __declspec(dllexport) UINT __stdcall RecordDataFolderOwnership(MSIHANDLE session) noexcept {
    try {
        std::wstring path = Normalize(Property(session, L"AUDIOPILOT_DATA_FOLDER"));
        if (!SafeDataPath(path, Normalize(Property(session, L"INSTALLFOLDER")))) return ERROR_INSTALL_FAILURE;
        if (SHCreateDirectoryExW(nullptr, path.c_str(), nullptr) != ERROR_SUCCESS && GetFileAttributesW(path.c_str()) == INVALID_FILE_ATTRIBUTES) return ERROR_INSTALL_FAILURE;
        std::wstring marker = path + L"\\" + MarkerName;
        HANDLE file = CreateFileW(marker.c_str(), GENERIC_WRITE, 0, nullptr, CREATE_NEW, FILE_ATTRIBUTE_HIDDEN | FILE_FLAG_OPEN_REPARSE_POINT, nullptr);
        if (file == INVALID_HANDLE_VALUE) return Owned(path) ? ERROR_SUCCESS : ERROR_INSTALL_FAILURE;
        DWORD written = 0;
        DWORD length = static_cast<DWORD>(strlen(MarkerText));
        bool success = WriteFile(file, MarkerText, length, &written, nullptr) && written == length;
        CloseHandle(file);
        return success ? ERROR_SUCCESS : ERROR_INSTALL_FAILURE;
    } catch (...) { return ERROR_INSTALL_FAILURE; }
}

extern "C" __declspec(dllexport) UINT __stdcall CleanupOwnedStartup(MSIHANDLE session) noexcept {
    try {
        std::wstring executable = Normalize(Property(session, L"INSTALLFOLDER") + L"\\AudioPilot.exe");
        if (executable.empty()) return ERROR_SUCCESS;
        const wchar_t* runKey = L"Software\\Microsoft\\Windows\\CurrentVersion\\Run";
        if (OwnedCommand(RegistryString(runKey, L"AudioPilot"), executable)) {
            HKEY key = nullptr;
            if (RegOpenKeyExW(HKEY_CURRENT_USER, runKey, 0, KEY_QUERY_VALUE | KEY_SET_VALUE | KEY_WOW64_64KEY, &key) == ERROR_SUCCESS) {
                if (OwnedCommand(RegistryString(runKey, L"AudioPilot"), executable)) RegDeleteValueW(key, L"AudioPilot");
                RegCloseKey(key);
            }
        }
        HRESULT initialized = CoInitializeEx(nullptr, COINIT_APARTMENTTHREADED);
        {
            ComPtr<ITaskService> service;
            ComPtr<ITaskFolder> folder;
            ComPtr<IRegisteredTask> task;
            VARIANT empty{};
            std::wstring name = L"AudioPilot Startup " + Property(session, L"UserSID");
            BSTR root = SysAllocString(L"\\");
            BSTR taskName = SysAllocString(name.c_str());
            if (SUCCEEDED(CoCreateInstance(CLSID_TaskScheduler, nullptr, CLSCTX_INPROC_SERVER, IID_PPV_ARGS(&service))) &&
                SUCCEEDED(service->Connect(empty, empty, empty, empty)) && SUCCEEDED(service->GetFolder(root, &folder)) &&
                SUCCEEDED(folder->GetTask(taskName, &task)) && OwnedTask(task.Get(), executable)) {
                folder->DeleteTask(taskName, 0);
            }
            SysFreeString(root);
            SysFreeString(taskName);
        }
        if (SUCCEEDED(initialized)) CoUninitialize();
        Message(session, L"AudioPilot startup cleanup checked executable ownership; unrelated registrations were preserved.");
    } catch (...) { Message(session, L"AudioPilot startup cleanup could not complete; unrelated registrations were preserved."); }
    return ERROR_SUCCESS;
}
