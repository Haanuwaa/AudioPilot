// Runs only against temporary fixtures. It never executes an MSI transaction or changes startup registration.
#include "CustomActions.cpp"
#include <filesystem>
#include <iostream>
#include <fstream>
#include <sddl.h>

int wmain(int argc, wchar_t* argv[]) {
    wchar_t temporary[MAX_PATH]{};
    GetTempPathW(MAX_PATH, temporary);
    GUID id{};
    CoCreateGuid(&id);
    wchar_t name[40]{};
    StringFromGUID2(id, name, 40);
    std::wstring root = std::wstring(temporary) + L"AudioPilot-Installer-Policy-" + name;
    std::filesystem::create_directory(root);
    int checks = 0;
    auto check = [&](bool result) { ++checks; if (!result) throw std::runtime_error("Installer policy regression at check " + std::to_string(checks)); };
    try {
        const std::wstring executable = L"C:\\AudioPilot Install\\AudioPilot.exe";
        check(OwnedCommand(L"\"C:\\AudioPilot Install\\AudioPilot.exe\" --startup", executable));
        check(OwnedCommand(executable, executable));
        check(!OwnedCommand(L"\"C:\\Portable\\AudioPilot.exe\" --startup", executable));
        check(!OwnedCommand(L"\"C:\\AudioPilot Install\\AudioPilot.exe.bak\"", executable));
        check(!OwnedCommand(L"", executable));
        check(!SafeDataPath(L"C:\\", executable));
        check(!SafeDataPath(Known(FOLDERID_Profile), executable));
        check(!SafeDataPath(Known(FOLDERID_Documents), executable));
        check(!SafeDataPath(Known(FOLDERID_RoamingAppData), executable));
        check(!SafeDataPath(Known(FOLDERID_Startup), executable));
        check(!SafeDataPath(Known(FOLDERID_PublicDocuments), executable));
        check(!SafeDataPath(Known(FOLDERID_Public), executable));
        check(!SafeDataPath(Normalize(temporary), executable));
        check(Contains(L"C:\\", executable));
        check(!SafeDataPath(L"C:\\AudioPilot Install", L"C:\\AudioPilot Install"));
        check(!SafeDataPath(L"C:\\AudioPilot Install\\data", L"C:\\AudioPilot Install"));
        check(Normalize(L"C:\\Data:stream").empty());
        check(Normalize(L"\\\\server\\share\\data").empty());
        check(Normalize(L"C:\\Data.\\folder").empty());
        check(Normalize(L"C:\\Data ").empty());
        check(!SafeDataPath(Known(FOLDERID_Windows) + L"\\AudioPilot", executable));
        check(SafeDataPath(root, executable));
        check(EmptyDirectory(root));
        check(!Owned(root));
        check(ProbeDataDirectoryWriteAccess(root) == ERROR_SUCCESS);
        check(EmptyDirectory(root));
        check(ProbeDataDirectoryWriteAccess(root + L"\\new\\nested") == ERROR_SUCCESS);
        check(GetFileAttributesW((root + L"\\new").c_str()) == INVALID_FILE_ATTRIBUTES);
        std::wstring blocker = root + L"\\parent-file";
        HANDLE blocked = CreateFileW(blocker.c_str(), GENERIC_WRITE, 0, nullptr, CREATE_NEW, FILE_ATTRIBUTE_NORMAL, nullptr);
        if (blocked == INVALID_HANDLE_VALUE) throw std::runtime_error("Blocked-path fixture creation failed");
        CloseHandle(blocked);
        check(ProbeDataDirectoryWriteAccess(blocker + L"\\data") == ERROR_DIRECTORY);
        DeleteFileW(blocker.c_str());
        std::wstring denied = root + L"\\denied";
        if (!CreateDirectoryW(denied.c_str(), nullptr)) throw std::runtime_error("Permission fixture creation failed");
        DWORD securitySize = 0;
        GetFileSecurityW(denied.c_str(), DACL_SECURITY_INFORMATION, nullptr, 0, &securitySize);
        std::vector<BYTE> originalSecurity(securitySize);
        if (!GetFileSecurityW(denied.c_str(), DACL_SECURITY_INFORMATION, originalSecurity.data(), securitySize, &securitySize))
            throw std::runtime_error("Permission fixture security read failed");
        PSECURITY_DESCRIPTOR restricted = nullptr;
        if (!ConvertStringSecurityDescriptorToSecurityDescriptorW(L"D:(D;;0x2;;;WD)(A;;FA;;;WD)", SDDL_REVISION_1, &restricted, nullptr))
            throw std::runtime_error("Permission fixture descriptor failed");
        BOOL applied = SetFileSecurityW(denied.c_str(), DACL_SECURITY_INFORMATION, restricted);
        LocalFree(restricted);
        DWORD permissionResult = ERROR_SUCCESS;
        try { permissionResult = ProbeDataDirectoryWriteAccess(denied); }
        catch (...) {
            SetFileSecurityW(denied.c_str(), DACL_SECURITY_INFORMATION, originalSecurity.data());
            RemoveDirectoryW(denied.c_str());
            throw;
        }
        BOOL restored = SetFileSecurityW(denied.c_str(), DACL_SECURITY_INFORMATION, originalSecurity.data());
        BOOL removed = RemoveDirectoryW(denied.c_str());
        check(applied && restored && removed);
        check(permissionResult == ERROR_ACCESS_DENIED);
        std::wstring marker = root + L"\\" + MarkerName;
        HANDLE file = CreateFileW(marker.c_str(), GENERIC_WRITE, 0, nullptr, CREATE_NEW, FILE_ATTRIBUTE_NORMAL, nullptr);
        if (file == INVALID_HANDLE_VALUE) throw std::runtime_error("Fixture creation failed");
        DWORD written = 0;
        WriteFile(file, MarkerText, static_cast<DWORD>(strlen(MarkerText)), &written, nullptr);
        CloseHandle(file);
        check(Owned(root));
        check(!EmptyDirectory(root));
        check(!RedirectedTree(root));
        std::wstring emptyChild = root + L"\\empty";
        CreateDirectoryW(emptyChild.c_str(), nullptr);
        check(!RedirectedTree(root));
        RemoveDirectoryW(emptyChild.c_str());
        check(LocalStateFileName(L"window-placement.json", false));
        check(LocalStateFileName(L"DEVICES.txt.0123456789abcdef0123456789abcdef.tmp", false));
        check(!LocalStateFileName(L"DEVICES.txt.other.tmp", false));
        check(!LocalStateFileName(L"AudioPilot.exe", false));
        check(!LocalStateFileName(L"settings.json", false));
        check(LocalStateFileName(L"AudioPilot.log.bak.4", true));
        check(!LocalStateFileName(L"AudioPilot.log.bak.unrelated", true));
        check(!LocalStateFileName(L"settings.json.bak", true));
        std::wstring local = root + L"\\local";
        std::wstring backups = local + L"\\backups";
        std::filesystem::create_directories(backups);
        const std::vector<std::wstring> known = { L"window-placement.json", L"update-notification.json", L"update-check.json", L"DEVICES.txt", L"AudioPilot.log",
            L"DEVICES.txt.0123456789abcdef0123456789abcdef.tmp", L"backups\\AudioPilot.log.bak", L"backups\\AudioPilot.log.bak.4" };
        const std::vector<std::wstring> unknown = { L"AudioPilot.exe", L"settings.json", L"keep.txt", L"backups\\settings.json.bak" };
        auto create = [&](const std::wstring& path) {
            std::ofstream file{ std::filesystem::path(path) };
            file << "fixture";
            if (!file) throw std::runtime_error("Local cleanup fixture creation failed");
        };
        for (const auto& entry : known) create(local + L"\\" + entry);
        for (const auto& entry : unknown) create(local + L"\\" + entry);
        check(CleanupLocalStateFiles(local) == ERROR_ACCESS_DENIED);
        check(std::filesystem::exists(local + L"\\AudioPilot.log"));
        check(WriteOwnershipMarker(local) == ERROR_SUCCESS);
        check(CleanupLocalStateFiles(local) == ERROR_SUCCESS);
        for (const auto& entry : known) check(!std::filesystem::exists(local + L"\\" + entry));
        for (const auto& entry : unknown) check(std::filesystem::exists(local + L"\\" + entry));
        for (const auto& entry : unknown) DeleteFileW((local + L"\\" + entry).c_str());
        RemoveDirectoryW(backups.c_str());
        check(CleanupLocalStateFiles(local) == ERROR_SUCCESS);
        check(!std::filesystem::exists(local));
        std::wstring updates = root + L"\\update-state";
        std::wstring installations = updates + L"\\updater\\installations";
        auto writeText = [&](const std::wstring& path, const std::string& text) {
            std::filesystem::create_directories(std::filesystem::path(path).parent_path());
            std::ofstream file{ std::filesystem::path(path), std::ios::binary };
            file << text;
            if (!file) throw std::runtime_error("Updater cleanup fixture creation failed");
        };
        auto workspace = [&](const wchar_t* name, const std::string& application) {
            std::wstring path = installations + L"\\" + name;
            writeText(path + L"\\.updater", "AudioPilot updater workspace 1");
            writeText(path + L"\\.updater-installation", "AudioPilot installed updater workspace 1\n" + application + "\n");
            writeText(path + L"\\snapshots\\backup\\settings.json", "original private configuration");
            return path;
        };
        std::wstring owned = workspace(L"11111111111111111111111111111111", "C:\\AudioPilot Install\\AudioPilot.exe");
        std::wstring foreign = workspace(L"22222222222222222222222222222222", "C:\\Other Copy\\AudioPilot.exe");
        std::wstring unmarked = installations + L"\\33333333333333333333333333333333";
        writeText(unmarked + L"\\keep.txt", "unowned");
        CleanupUpdaterState(updates, executable);
        check(!std::filesystem::exists(owned));
        check(std::filesystem::exists(foreign + L"\\snapshots\\backup\\settings.json"));
        check(std::filesystem::exists(unmarked + L"\\keep.txt"));

        owned = workspace(L"11111111111111111111111111111111", "C:\\AudioPilot Install\\AudioPilot.exe");
        {
            UpdateHandle busy(CreateFileW((owned + L"\\job.lock").c_str(), GENERIC_READ | GENERIC_WRITE, 0, nullptr, CREATE_ALWAYS, FILE_ATTRIBUTE_NORMAL, nullptr));
            check(busy.Valid());
            CleanupUpdaterState(updates, executable);
            check(std::filesystem::exists(owned + L"\\snapshots\\backup\\settings.json"));
        }
        writeText(owned + L"\\recovery.json", "{\"Completed\":false}");
        check(!CleanupUpdateWorkspace(owned, executable));
        check(std::filesystem::exists(owned + L"\\snapshots\\backup\\settings.json"));
        writeText(owned + L"\\.updater-recovery-settled", "AudioPilot settled recovery 1\nincorrect\n");
        check(!CleanupUpdateWorkspace(owned, executable));
        std::string journal = "{\"Completed\":true}";
        writeText(owned + L"\\recovery.json", journal);
        writeText(owned + L"\\.updater-recovery-settled", "AudioPilot settled recovery 1\n" + UpdateDigest(journal) + "\n");
        writeText(owned + L"\\recovery.json.new", "interrupted intent");
        check(!CleanupUpdateWorkspace(owned, executable));
        DeleteFileW((owned + L"\\recovery.json.new").c_str());
        check(CleanupUpdateWorkspace(owned, executable));
        check(RemoveDirectoryW(owned.c_str()));
        check(!std::filesystem::exists(owned));

        std::wstring pool = updates + L"\\updater\\runtime";
        std::wstring idle = pool + L"\\44444444444444444444444444444444", busy = pool + L"\\55555555555555555555555555555555";
        for (const auto& runtime : { idle, busy }) {
            writeText(runtime + L"\\.pilot-runtime", "AudioPilot pilot runtime 1");
            writeText(runtime + L"\\payload.dll", "synthetic runtime");
        }
        {
            UpdateHandle lease(CreateFileW((busy + L"\\runtime.inuse").c_str(), GENERIC_READ, FILE_SHARE_READ, nullptr, CREATE_ALWAYS, FILE_ATTRIBUTE_NORMAL, nullptr));
            check(lease.Valid());
            CleanupUpdaterState(updates, executable);
            check(std::filesystem::exists(idle)); // Other installations keep their shared helper pool.
            CleanupUpdateRuntimes(pool);
            check(!std::filesystem::exists(idle));
            check(std::filesystem::exists(busy + L"\\payload.dll"));
        }
        CleanupUpdateRuntimes(pool);
        check(!std::filesystem::exists(busy));
        std::filesystem::remove_all(updates);
        DeleteFileW(marker.c_str());
        check(!Owned(root));
        RemoveDirectoryW(root.c_str());
        if (argc > 1) {
            check(HasRedirectedAncestor(Normalize(argv[1])));
            check(RedirectedTree(std::filesystem::path(argv[1]).parent_path().wstring()));
            check(ProbeDataDirectoryWriteAccess(Normalize(argv[1]) + L"\\probe") == ERROR_ACCESS_DENIED);
            check(CleanupLocalStateFiles(Normalize(argv[1])) == ERROR_ACCESS_DENIED);
        }
        std::wcout << L"Native installer policy checks passed (" << checks << L").\n";
        return 0;
    } catch (const std::exception& error) {
        std::cerr << error.what() << '\n';
        DeleteFileW((root + L"\\" + MarkerName).c_str());
        RemoveDirectoryW(root.c_str());
        return 1;
    }
}
