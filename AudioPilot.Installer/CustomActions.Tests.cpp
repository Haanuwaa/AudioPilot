// Runs only against temporary fixtures. It never executes an MSI transaction or changes startup registration.
#include "CustomActions.cpp"
#include <filesystem>
#include <iostream>

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
        DeleteFileW(marker.c_str());
        check(!Owned(root));
        RemoveDirectoryW(root.c_str());
        if (argc > 1) {
            check(HasRedirectedAncestor(Normalize(argv[1])));
            check(RedirectedTree(std::filesystem::path(argv[1]).parent_path().wstring()));
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
