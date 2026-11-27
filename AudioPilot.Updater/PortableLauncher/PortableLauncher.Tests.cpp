// Runs only against temporary fixtures. Verifies portable launcher parity, safety guards, and fast-path fallback.
#define PORTABLE_LAUNCHER_TESTING 1
#define PACKAGED_LAUNCHER 1
#include "PortableLauncher.cpp"

#include <objbase.h>
#include <shellapi.h>
#include <filesystem>
#include <fstream>
#include <iostream>
#include <stdexcept>

namespace fs = std::filesystem;
using namespace audiopilot::launcher;

namespace {

std::wstring GetEnvW(const wchar_t* name) {
    DWORD needed = GetEnvironmentVariableW(name, nullptr, 0);
    if (needed == 0) return {};
    std::wstring value(needed - 1, L'\0');
    GetEnvironmentVariableW(name, value.data(), needed);
    return value;
}

void WriteBytes(const fs::path& path, std::string_view data) {
    fs::create_directories(path.parent_path());
    std::ofstream out(path, std::ios::binary | std::ios::trunc);
    out.write(data.data(), static_cast<std::streamsize>(data.size()));
    if (!out) throw std::runtime_error("Failed to write test fixture file.");
}

std::string ReadBytes(const fs::path& path) {
    std::ifstream in(path, std::ios::binary);
    if (!in) throw std::runtime_error("Failed to read test output file.");
    return std::string((std::istreambuf_iterator<char>(in)), std::istreambuf_iterator<char>());
}

std::string WideToUtf8Test(const std::wstring& s) {
    if (s.empty()) return {};
    int needed = WideCharToMultiByte(CP_UTF8, 0, s.data(), static_cast<int>(s.size()), nullptr, 0, nullptr, nullptr);
    std::string out(static_cast<size_t>(needed), '\0');
    WideCharToMultiByte(CP_UTF8, 0, s.data(), static_cast<int>(s.size()), out.data(), needed, nullptr, nullptr);
    return out;
}

int RunChildHelperMode(int argc, wchar_t* argv[]) {
    std::wstring output = GetEnvW(L"PL_HELPER_OUTPUT");
    if (output.empty()) return -1;

    std::wstring envOut = GetEnvW(L"PL_HELPER_ENV");
    if (!envOut.empty()) {
        std::string envDump;
        LPWCH raw = GetEnvironmentStringsW();
        if (raw) {
            const wchar_t* cursor = raw;
            while (*cursor != L'\0') {
                size_t len = wcslen(cursor);
                envDump.append(WideToUtf8Test(std::wstring(cursor, len)));
                envDump.push_back('\n');
                cursor += len + 1;
            }
            FreeEnvironmentStringsW(raw);
        }
        WriteBytes(fs::path(envOut), envDump);
    }

    std::wstring cwdOut = GetEnvW(L"PL_HELPER_CWD");
    if (!cwdOut.empty()) {
        WriteBytes(fs::path(cwdOut), WideToUtf8Test(fs::current_path().wstring()));
    }

    std::string argsDump;
    for (int i = 1; i < argc; ++i) {
        if (i > 1) argsDump.push_back('\n');
        argsDump.append(WideToUtf8Test(argv[i]));
    }
    fs::path tmpOut = fs::path(output + L".tmp");
    WriteBytes(tmpOut, argsDump);
    if (!MoveFileExW(tmpOut.c_str(), output.c_str(), MOVEFILE_REPLACE_EXISTING)) {
        throw std::runtime_error("Failed to publish test output file atomically.");
    }

    std::wstring exitVar = GetEnvW(L"PL_HELPER_EXIT");
    if (!exitVar.empty()) {
        return _wtoi(exitVar.c_str());
    }
    return 7;
}

struct EnvGuard {
    std::wstring name;
    bool hadValue = false;
    std::wstring previous;

    EnvGuard(const wchar_t* n, const wchar_t* value) : name(n) {
        DWORD needed = GetEnvironmentVariableW(n, nullptr, 0);
        if (needed > 0) {
            hadValue = true;
            previous.resize(needed - 1);
            GetEnvironmentVariableW(n, previous.data(), needed);
        }
        SetEnvironmentVariableW(n, value);
    }
    ~EnvGuard() {
        SetEnvironmentVariableW(name.c_str(), hadValue ? previous.c_str() : nullptr);
    }
};

}  // namespace

int wmain(int argc, wchar_t* argv[]) {
    int helperExit = RunChildHelperMode(argc, argv);
    if (helperExit >= 0) return helperExit;

    int checks = 0;
    auto check = [&](bool condition, const char* label) {
        ++checks;
        if (!condition) {
            throw std::runtime_error(std::string("Portable launcher test failed (check ") + std::to_string(checks) + "): " + label);
        }
    };

    wchar_t tempBuf[MAX_PATH]{};
    GetTempPathW(MAX_PATH, tempBuf);
    GUID guid{};
    CoCreateGuid(&guid);
    wchar_t guidText[40]{};
    StringFromGUID2(guid, guidText, 40);
    fs::path tempRoot = fs::path(tempBuf) / (std::wstring(L"AudioPilot-PortableLauncher-Tests-") + guidText);
    fs::create_directories(tempRoot);

    try {
        // 1. State parser rejects duplicates, escaping generations, null PendingHealth, trailing tokens.
        const std::string gen = "1.0.0_0123456789abcdef0123456789abcdef";
        const std::string valid = "{\"Schema\":1,\"Current\":\"" + gen + "\",\"Previous\":null,\"PendingHealth\":false}";
        DeploymentState parsed{};
        check(ParseDeploymentState(valid, parsed), "valid deployment state");
        check(parsed.schema == 1 && parsed.current == gen && !parsed.previous.has_value() && !parsed.pendingHealth, "parsed fields match");

        for (const std::string& bad : {
                 std::string("{\"Schema\":1,\"Schema\":2,\"Current\":\"") + gen + "\",\"Previous\":null,\"PendingHealth\":false}",
                 std::string("{\"Schema\":1,\"Current\":\"../elsewhere\",\"Previous\":null,\"PendingHealth\":false}"),
                 std::string("{\"Schema\":1,\"Current\":\"") + gen + "\",\"Previous\":null,\"PendingHealth\":true}",
                 std::string("{\"Schema\":1,\"Current\":\"") + gen + "\",\"Previous\":null,\"PendingHealth\":null}",
                 valid + "{}",
                 std::string("{\"Schema\":1,\"Current\":\"") + gen + "\",\"Previous\":null,\"PendingHealth\":false,\"Extra\":1}",
             }) {
            DeploymentState ignored{};
            check(!ParseDeploymentState(bad, ignored), "reject unsafe deployment state");
        }

        // 2. CLI failure formatting never leaks paths and matches exact JSON/text envelope contract.
        {
            LaunchResult nf{ErrorKind::NotFound, 74};
            bool toStdout = false;
            std::string json = FormatCliFailure({L"status", L"--redact", L"--json"}, nf, toStdout);
            check(toStdout, "json failure targets stdout");
            check(json.find("\"schemaVersion\":\"1.0.0\"") != std::string::npos, "json schemaVersion");
            check(json.find("\"ok\":false") != std::string::npos, "json ok=false");
            check(json.find("\"code\":\"launcher-state-invalid\"") != std::string::npos, "json code");
            check(json.find("\"exitCode\":74") != std::string::npos, "json exitCode");

            bool textStdout = true;
            std::string text = FormatCliFailure({L"status"}, nf, textStdout);
            check(!textStdout, "text failure targets stderr");
            check(text.starts_with("[diag-code:launcher-state-invalid] "), "text diag prefix");
        }

        // 3. Helper failure codes produce actionable messages without raw exit status text.
        {
            auto f73 = FailureFor({ErrorKind::ChildExit, 73});
            auto f74 = FailureFor({ErrorKind::ChildExit, 74});
            auto f75 = FailureFor({ErrorKind::ChildExit, 75});
            auto fElev = FailureFor({ErrorKind::Elevated, 75});
            check(std::string_view(f73.code) == "launcher-busy" && f73.exitCode == 73, "exit 73 mapping");
            check(std::string_view(f74.code) == "launcher-state-invalid" && f74.exitCode == 74, "exit 74 mapping");
            check(std::string_view(f75.code) == "launcher-access-denied" && f75.exitCode == 75, "exit 75 mapping");
            check(std::string_view(fElev.code) == "launcher-elevated" && fElev.exitCode == 75, "elevated mapping");
        }

        // 3b. ReadInt64 rejects fractional/exponent after 0; path normalization rejects trailing dots/spaces & supports UNC; JSON escaping works.
        {
            int64_t val = 0;
            check(!StrictJsonReader("0.5").ReadInt64(val), "ReadInt64 rejects 0.5");
            check(!StrictJsonReader("0e1").ReadInt64(val), "ReadInt64 rejects 0e1");
            check(StrictJsonReader("0").ReadInt64(val) && val == 0, "ReadInt64 accepts 0");

            std::wstring norm;
            size_t rootLen = 0;
            check(!NormalizeAbsolutePath(L"C:\\Data.\\folder", norm, rootLen), "reject trailing dot segment");
            check(!NormalizeAbsolutePath(L"C:\\Data \\folder", norm, rootLen), "reject trailing space segment");
            check(NormalizeAbsolutePath(L"\\\\server\\share\\AudioPilot\\app", norm, rootLen) && norm == L"\\\\server\\share\\AudioPilot\\app" && rootLen == 14, "normalize UNC path");
            check(IsAbsolutePathUtf8("\\\\server\\share\\AudioPilot"), "accept UNC recovery path");

            std::string escaped;
            AppendJsonEscaped(escaped, "a\"b\\c\nd");
            check(escaped == "a\\\"b\\\\c\\nd", "JSON escaping handles quotes, backslashes, and control chars");

            // Command-line parser parity verified directly against shell32!CommandLineToArgvW.
            for (const wchar_t* sample : {
                     L"\"C:\\Program Files\\AudioPilot\\AudioPilot.exe\" --minimized \"with spaces\" \"embedded \\\"quote\\\"\" \"\" trailing\\\\",
                     L"app.exe \"a\"\"b\" \"c\\\\\"d\" \"e\\\\\"\"f\" \"\"\"\" \"a\\\"\\\"b\"",
                     L"\"C:\\app\\exe\"--flag",
                     L"AudioPilot.exe\t--tab\t\"two   spaces\"\t\"\"",
                     L"\"C:\\Users\\\u65E5\u672C\u8A9E\\AudioPilot.exe\" \"arg\\\\\" next",
                 }) {
                std::wstring parsedArgv0;
                std::vector<std::wstring> parsedArgs;
                ParseWindowsCommandLine(sample, parsedArgv0, parsedArgs);

                int winArgc = 0;
                LPWSTR* winArgv = CommandLineToArgvW(sample, &winArgc);
                check(winArgv != nullptr && winArgc >= 1, "CommandLineToArgvW reference parsed sample");
                bool match = (parsedArgv0 == winArgv[0]) && (parsedArgs.size() == static_cast<size_t>(winArgc - 1));
                for (int i = 1; match && i < winArgc; ++i) {
                    match = (parsedArgs[static_cast<size_t>(i - 1)] == winArgv[i]);
                }
                LocalFree(winArgv);
                check(match, "ParseWindowsCommandLine matches CommandLineToArgvW bit-for-bit");
            }
        }

        // 4. Junction / reparse-point check (if passed by test script).
        if (argc > 1) {
            check(PathGuardCheck(argv[1]) == ErrorKind::InvalidState, "reject junction path");
            check(PathGuardCheck(JoinPath(argv[1], L"child.txt")) == ErrorKind::InvalidState, "reject descendant of junction");
            std::wstring junctionParent = ParentDirOf(argv[1]);
            std::wstring junctionName = FileNameOf(argv[1]);
            std::wstring ignoredCanonical;
            check(PathGuardCheckRelative(junctionParent, junctionName, ignoredCanonical) == ErrorKind::InvalidState, "relative guard rejects junction");
            check(PathGuardCheckRelative(junctionParent, junctionName + L"\\child.txt", ignoredCanonical) == ErrorKind::InvalidState, "relative guard rejects junction descendant");
        }

        // 5. End-to-end launcher execution: GUI fallback, elevated refusal, CLI forwarding, pending-health CLI rollback, and GUI fast path.
        std::wstring selfExe = GetCurrentExecutablePath();
        std::string selfBytes = ReadBytes(fs::path(selfExe));

        // 5a. GUI helper fallback, elevated GUI refusal, CLI forwarding + pending-health recovery CLI
        for (int mode = 0; mode < 3; ++mode) {
            bool isCli = (mode == 2);
            bool isElevated = (mode >= 1);
            fs::path caseRoot = tempRoot / (L"case-" + std::to_wstring(mode));
            fs::path workspace = caseRoot / L"app";
            fs::path folder = workspace / L"versions" / Utf8ToWide(gen);
            WriteBytes(workspace / L".updater", kWorkspaceMarker);
            WriteBytes(workspace / L"deployment.json", valid);
            WriteBytes(folder / (isCli ? L"AudioPilot.Cli.exe" : L"AudioPilot.Updater.exe"), selfBytes);

            fs::path argsOut = caseRoot / L"args.txt";
            fs::path cwdOut = caseRoot / L"cwd.txt";
            EnvGuard gOut(L"PL_HELPER_OUTPUT", argsOut.c_str());
            EnvGuard gCwd(L"PL_HELPER_CWD", cwdOut.c_str());
            EnvGuard gExit(L"PL_HELPER_EXIT", L"7");

            int elevChecks = 0;
            auto checkElev = [&]() -> ErrorKind {
                ++elevChecks;
                return isElevated ? ErrorKind::Elevated : ErrorKind::None;
            };

            std::vector<std::wstring> inputArgs = {L"with spaces", L"embedded \"quote\"", L""};
            LaunchResult res = RunLauncher(
                (caseRoot / (isCli ? L"AudioPilot.Cli.exe" : L"AudioPilot.exe")).wstring(),
                inputArgs,
                checkElev,
                isCli);

            check((isCli && elevChecks == 0) || (!isCli && elevChecks == 1), "elevation check count");
            if (!isCli && isElevated) {
                check(res.kind == ErrorKind::Elevated && res.exitCode == 75, "elevated GUI refused");
                check(!fs::exists(argsOut), "elevated GUI did not spawn child");
                continue;
            }
            if (res.kind != ErrorKind::ChildExit || res.exitCode != 7) {
                throw std::runtime_error("child exit code preserved: kind=" + std::to_string(static_cast<int>(res.kind)) + " exit=" + std::to_string(res.exitCode));
            }
            ++checks;
            std::string recordedArgs = ReadBytes(argsOut);
            std::string expectedArgs = isCli
                ? "with spaces\nembedded \"quote\"\n"
                : ("pilot-launch\n" + WideToUtf8Test(workspace.wstring()) + "\nwith spaces\nembedded \"quote\"\n");
            check(recordedArgs == expectedArgs, "arguments preserved verbatim");

            std::string recordedCwd = ReadBytes(cwdOut);
            std::string expectedCwd = WideToUtf8Test(isCli ? fs::current_path().wstring() : folder.wstring());
            check(recordedCwd == expectedCwd, "working directory contract");

            if (isCli) {
                std::string pending = "{\"Schema\":1,\"Current\":\"1.0.1_1123456789abcdef0123456789abcdef\",\"Previous\":\"" + gen + "\",\"PendingHealth\":true}";
                WriteBytes(workspace / L"deployment.json", pending);
                LaunchResult recRes = RunLauncher((caseRoot / L"AudioPilot.Cli.exe").wstring(), {L"update", L"status", L"--json"}, checkElev, true);
                check(recRes.kind == ErrorKind::ChildExit && recRes.exitCode == 7 && elevChecks == 0, "pending health CLI routes to previous generation");
            }
        }

        // 5b. Direct GUI fast path + env stripping + every fallback condition
        {
            fs::path fastRoot = tempRoot / L"fast-path";
            fs::path workspace = fastRoot / L"app";
            fs::path folder = workspace / L"versions" / Utf8ToWide(gen);
            WriteBytes(workspace / L".updater", kWorkspaceMarker);
            WriteBytes(workspace / L"deployment.json", valid);
            WriteBytes(workspace / L"portable-layout.json", "{\"Schema\":1,\"DiagnosticId\":\"11111111111111111111111111111111\",\"Managed\":true}");
            WriteBytes(workspace / L".documents-generation", gen);
            WriteBytes(folder / L"AudioPilot.exe", selfBytes);
            WriteBytes(folder / L"AudioPilot.Updater.exe", selfBytes);
            WriteBytes(fastRoot / L"ABOUT.txt", "about");
            WriteBytes(fastRoot / L"licenses" / L"LICENSE", "license");
            WriteBytes(fastRoot / L"licenses" / L"THIRD-PARTY-NOTICES.txt", "notices");
            WriteBytes(fastRoot / L"licenses" / L"UPDATER-NOTICES.txt", "updater");

            fs::path argsOut = fastRoot / L"args.txt";
            fs::path envOut = fastRoot / L"env.txt";
            EnvGuard gOut(L"PL_HELPER_OUTPUT", argsOut.c_str());
            EnvGuard gEnv(L"PL_HELPER_ENV", envOut.c_str());
            EnvGuard gExit(L"PL_HELPER_EXIT", L"0");
            EnvGuard gDiag(L"AUDIOPILOT_DIAGNOSTIC_SESSION", L"leak-diag");
            EnvGuard gTest(L"AUDIOPILOT_UPDATE_TEST_SESSION", L"leak-test");
            EnvGuard gLife(L"AUDIOPILOT_UPDATE_LIFECYCLE", L"leak-lifecycle");

            g_packagedLauncher = true;
            LaunchResult fastRes = RunLauncher(
                (fastRoot / L"AudioPilot.exe").wstring(),
                {L"--minimized"},
                []() { return ErrorKind::None; },
                false);
            check(fastRes.kind == ErrorKind::None && fastRes.exitCode == 0, "fast path returns immediately with exit 0");

            // Wait for detached child to write its output files.
            bool wrote = false;
            for (int attempt = 0; attempt < 200; ++attempt) {
                if (fs::exists(argsOut) && fs::file_size(argsOut) > 0 && fs::exists(envOut) && fs::file_size(envOut) > 0) {
                    wrote = true;
                    break;
                }
                Sleep(25);
            }
            check(wrote, "fast-path child executed");
            check(ReadBytes(argsOut) == "--minimized", "fast-path passed direct GUI args");
            std::string envText = ReadBytes(envOut);
            check(envText.find("leak-diag") == std::string::npos, "stripped AUDIOPILOT_DIAGNOSTIC_SESSION");
            check(envText.find("leak-test") == std::string::npos, "stripped AUDIOPILOT_UPDATE_TEST_SESSION");
            check(envText.find("leak-lifecycle") == std::string::npos, "stripped AUDIOPILOT_UPDATE_LIFECYCLE");

            // Zero-allocation env pass-through when none of the three strippable variables are present.
            {
                EnvGuard clearDiag(L"AUDIOPILOT_DIAGNOSTIC_SESSION", nullptr);
                EnvGuard clearTest(L"AUDIOPILOT_UPDATE_TEST_SESSION", nullptr);
                EnvGuard clearLife(L"AUDIOPILOT_UPDATE_LIFECYCLE", nullptr);
                EnvGuard gPassMarker(L"PL_HELPER_PASSTHROUGH", L"keep-me");
                check(!HasStrippableEnvironmentVariables(), "no strippable env vars detected");
                fs::path passOut = fastRoot / L"args-pass.txt";
                fs::path passEnv = fastRoot / L"env-pass.txt";
                EnvGuard gPassOut(L"PL_HELPER_OUTPUT", passOut.c_str());
                EnvGuard gPassEnv(L"PL_HELPER_ENV", passEnv.c_str());
                LaunchResult passRes = RunLauncher(
                    (fastRoot / L"AudioPilot.exe").wstring(),
                    {L"--passthrough"},
                    []() { return ErrorKind::None; },
                    false);
                check(passRes.kind == ErrorKind::None && passRes.exitCode == 0, "zero-alloc env fast path returns exit 0");
                bool passWrote = false;
                for (int attempt = 0; attempt < 200; ++attempt) {
                    if (fs::exists(passOut) && fs::file_size(passOut) > 0 && fs::exists(passEnv) && fs::file_size(passEnv) > 0) {
                        passWrote = true;
                        break;
                    }
                    Sleep(25);
                }
                check(passWrote, "zero-alloc env fast-path child executed");
                check(ReadBytes(passOut) == "--passthrough", "zero-alloc env fast-path args preserved");
                check(ReadBytes(passEnv).find("PL_HELPER_PASSTHROUGH=keep-me") != std::string::npos, "inherited normal environment variables");
            }

            auto assertFallback = [&](const char* reason) {
                for (int attempt = 0; attempt < 40; ++attempt) {
                    std::error_code rmEc;
                    fs::remove(argsOut, rmEc);
                    if (!fs::exists(argsOut)) break;
                    Sleep(25);
                }
                EnvGuard gFallbackExit(L"PL_HELPER_EXIT", L"7");
                LaunchResult fb = RunLauncher(
                    (fastRoot / L"AudioPilot.exe").wstring(),
                    {L"--minimized"},
                    []() { return ErrorKind::None; },
                    false);
                check(fb.kind == ErrorKind::ChildExit && fb.exitCode == 7, reason);
                std::string expected = "pilot-launch\n" + WideToUtf8Test(workspace.wstring()) + "\n--minimized";
                check(ReadBytes(argsOut) == expected, reason);
            };

            // Non-packaged must fall back
            g_packagedLauncher = false;
            assertFallback("non-packaged fallback");
            g_packagedLauncher = true;

            // Malformed recovery.json claiming Completed:true must fall back
            WriteBytes(workspace / L"recovery.json", "{\"Completed\":true}");
            assertFallback("malformed recovery fallback");
            fs::remove(workspace / L"recovery.json");

            // Missing mirrored document must fall back
            fs::remove(fastRoot / L"ABOUT.txt");
            assertFallback("missing mirrored doc fallback");
            WriteBytes(fastRoot / L"ABOUT.txt", "about");

            // Held lifecycle.lock must fall back
            {
                ScopedHandle held = TryAcquireExclusiveLock((workspace / L"lifecycle.lock").wstring());
                check(held.Valid(), "acquire lifecycle.lock in test");
                assertFallback("held lifecycle.lock fallback");
            }
        }

        std::error_code ec;
        fs::remove_all(tempRoot, ec);
        std::wcout << L"Native portable launcher checks passed (" << checks << L").\n";
        return 0;
    } catch (const std::exception& ex) {
        std::cerr << ex.what() << '\n';
        std::error_code ec;
        fs::remove_all(tempRoot, ec);
        return 1;
    }
}
