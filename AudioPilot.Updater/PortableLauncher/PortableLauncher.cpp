#define WIN32_LEAN_AND_MEAN
#define NOMINMAX

#include <windows.h>
#include <shellapi.h>

#include <cstdint>
#include <cstring>
#include <limits>
#include <optional>
#include <string>
#include <string_view>
#include <unordered_set>
#include <vector>

namespace audiopilot::launcher {

#ifndef PACKAGED_LAUNCHER
#define PACKAGED_LAUNCHER 0
#endif

inline bool g_packagedLauncher = (PACKAGED_LAUNCHER != 0);

constexpr std::string_view kWorkspaceMarker = "AudioPilot updater workspace 1";
constexpr size_t kMaxDeploymentBytes = 8192;
constexpr size_t kMaxLayoutBytes = 4096;
constexpr size_t kMaxRecoveryBytes = 8192;
constexpr size_t kMaxReceiptBytes = 100;
constexpr int kMaxJsonDepth = 32;

constexpr const wchar_t* kMirroredDocs[] = {
    L"ABOUT.txt",
    L"licenses\\LICENSE",
    L"licenses\\THIRD-PARTY-NOTICES.txt",
    L"licenses\\UPDATER-NOTICES.txt",
};

enum class ErrorKind {
    None,
    InvalidState,
    NotFound,
    AccessDenied,
    Elevated,
    ChildExit,
    LaunchFailed,
};

struct LaunchResult {
    ErrorKind kind = ErrorKind::None;
    int exitCode = 0;
};

struct LaunchFailure {
    const char* code;
    const char* message;
    int exitCode;
};

struct DeploymentState {
    int64_t schema = 0;
    std::string current;
    std::optional<std::string> previous;
    bool pendingHealth = false;
};

struct PortableLayout {
    int64_t schema = 0;
    std::string diagnosticId;
    bool managed = false;
};

struct RecoveryRecord {
    int64_t schema = 0;
    std::string diagnosticId;
    std::string dataRoot;
    std::string previousApp;
    std::string snapshot;
    int64_t revision = 0;
    bool completed = false;
};

struct ScopedHandle {
    HANDLE handle = INVALID_HANDLE_VALUE;
    ScopedHandle() = default;
    explicit ScopedHandle(HANDLE h) noexcept : handle(h) {}
    ~ScopedHandle() {
        if (handle != INVALID_HANDLE_VALUE && handle != nullptr) {
            CloseHandle(handle);
        }
    }
    ScopedHandle(const ScopedHandle&) = delete;
    ScopedHandle& operator=(const ScopedHandle&) = delete;
    ScopedHandle(ScopedHandle&& other) noexcept : handle(other.handle) {
        other.handle = INVALID_HANDLE_VALUE;
    }
    ScopedHandle& operator=(ScopedHandle&& other) noexcept {
        if (this != &other) {
            if (handle != INVALID_HANDLE_VALUE && handle != nullptr) {
                CloseHandle(handle);
            }
            handle = other.handle;
            other.handle = INVALID_HANDLE_VALUE;
        }
        return *this;
    }
    bool Valid() const noexcept {
        return handle != INVALID_HANDLE_VALUE && handle != nullptr;
    }
};

inline bool IsAsciiAlpha(wchar_t c) noexcept {
    return (c >= L'A' && c <= L'Z') || (c >= L'a' && c <= L'z');
}

inline bool IsAsciiAlpha(char c) noexcept {
    return (c >= 'A' && c <= 'Z') || (c >= 'a' && c <= 'z');
}

inline bool IsAsciiDigit(char c) noexcept {
    return c >= '0' && c <= '9';
}

inline bool IsLowerHex(char c) noexcept {
    return IsAsciiDigit(c) || (c >= 'a' && c <= 'f');
}

inline bool IsAnyHex(char c) noexcept {
    return IsLowerHex(c) || (c >= 'A' && c <= 'F');
}

inline wchar_t ToLowerAsciiW(wchar_t c) noexcept {
    return (c >= L'A' && c <= L'Z') ? static_cast<wchar_t>(c - L'A' + L'a') : c;
}

inline bool EqualsIgnoreCaseAscii(std::wstring_view a, std::wstring_view b) noexcept {
    if (a.size() != b.size()) return false;
    for (size_t i = 0; i < a.size(); ++i) {
        if (ToLowerAsciiW(a[i]) != ToLowerAsciiW(b[i])) return false;
    }
    return true;
}

inline std::wstring Utf8ToWide(std::string_view input) {
    if (input.empty()) return {};
    if (input.size() > static_cast<size_t>(std::numeric_limits<int>::max())) return {};
    int needed = MultiByteToWideChar(CP_UTF8, MB_ERR_INVALID_CHARS, input.data(), static_cast<int>(input.size()), nullptr, 0);
    if (needed <= 0) return {};
    std::wstring result(static_cast<size_t>(needed), L'\0');
    if (MultiByteToWideChar(CP_UTF8, MB_ERR_INVALID_CHARS, input.data(), static_cast<int>(input.size()), result.data(), needed) <= 0) {
        return {};
    }
    return result;
}

inline std::wstring FileNameOf(std::wstring_view path) {
    size_t pos = path.find_last_of(L"\\/");
    return std::wstring(pos == std::wstring_view::npos ? path : path.substr(pos + 1));
}

inline std::wstring ParentDirOf(std::wstring_view path) {
    while (path.size() > 3 && (path.back() == L'\\' || path.back() == L'/')) {
        path.remove_suffix(1);
    }
    size_t pos = path.find_last_of(L"\\/");
    if (pos == std::wstring_view::npos) return {};
    if (pos == 2 && path.size() >= 3 && IsAsciiAlpha(path[0]) && path[1] == L':') {
        return std::wstring(path.substr(0, 3));
    }
    return std::wstring(path.substr(0, pos));
}

inline std::wstring JoinPath(std::wstring_view a, std::wstring_view b) {
    if (a.empty()) return std::wstring(b);
    std::wstring out(a);
    if (out.back() != L'\\' && out.back() != L'/') out.push_back(L'\\');
    out.append(b);
    return out;
}

inline bool IsCliExecutableName(std::wstring_view path) noexcept {
    std::wstring base = FileNameOf(path);
    return EqualsIgnoreCaseAscii(base, L"AudioPilot.Cli.exe") || EqualsIgnoreCaseAscii(base, L"AudioPilot.Cli");
}

inline bool IsAbsolutePathUtf8(std::string_view path) noexcept {
    if (path.size() >= 3 && IsAsciiAlpha(path[0]) && path[1] == ':' && (path[2] == '\\' || path[2] == '/')) {
        return true;
    }
    if (path.size() >= 5 && (path[0] == '\\' || path[0] == '/') && (path[1] == '\\' || path[1] == '/')) {
        size_t serverEnd = path.find_first_of("\\/", 2);
        if (serverEnd == std::string_view::npos || serverEnd == 2 || serverEnd + 1 >= path.size()) return false;
        size_t shareStart = serverEnd + 1;
        size_t shareEnd = path.find_first_of("\\/", shareStart);
        return shareEnd != shareStart;
    }
    return false;
}

inline std::wstring ToWin32ApiPath(const std::wstring& canonicalPath) {
    if (canonicalPath.size() < MAX_PATH || canonicalPath.starts_with(L"\\\\?\\")) {
        return canonicalPath;
    }
    if (canonicalPath.starts_with(L"\\\\")) {
        return L"\\\\?\\UNC\\" + canonicalPath.substr(2);
    }
    return L"\\\\?\\" + canonicalPath;
}

inline bool NormalizeAbsolutePath(std::wstring_view input, std::wstring& normalized, size_t& rootLength) {
    normalized.clear();
    rootLength = 0;
    if (input.size() < 3) return false;

    std::wstring work(input);
    for (wchar_t& ch : work) {
        if (ch == L'/') ch = L'\\';
    }

    std::wstring prefix;
    size_t pos = 0;
    if (work.size() >= 8 && EqualsIgnoreCaseAscii(std::wstring_view(work).substr(0, 8), L"\\\\?\\UNC\\")) {
        work = L"\\\\" + work.substr(8);
    } else if (work.size() >= 7 && work.compare(0, 4, L"\\\\?\\") == 0 && IsAsciiAlpha(work[4]) && work[5] == L':' && work[6] == L'\\') {
        work.erase(0, 4);
    }

    if (work.size() >= 3 && IsAsciiAlpha(work[0]) && work[1] == L':' && work[2] == L'\\') {
        prefix = work.substr(0, 3);
        pos = 3;
    } else if (work.size() >= 5 && work[0] == L'\\' && work[1] == L'\\' && work[2] != L'\\' && work[2] != L'?' && work[2] != L'.') {
        size_t serverEnd = work.find(L'\\', 2);
        if (serverEnd == std::wstring::npos || serverEnd == 2 || serverEnd + 1 >= work.size()) return false;
        size_t shareStart = serverEnd + 1;
        size_t shareEnd = work.find(L'\\', shareStart);
        std::wstring server = work.substr(2, serverEnd - 2);
        std::wstring share = (shareEnd == std::wstring::npos) ? work.substr(shareStart) : work.substr(shareStart, shareEnd - shareStart);
        if (share.empty() || server.find(L':') != std::wstring::npos || share.find(L':') != std::wstring::npos ||
            server.back() == L'.' || server.back() == L' ' || share.back() == L'.' || share.back() == L' ') {
            return false;
        }
        prefix = L"\\\\" + server + L"\\" + share;
        pos = (shareEnd == std::wstring::npos) ? work.size() : shareEnd + 1;
    } else {
        return false;
    }

    std::vector<std::wstring> parts;
    while (pos < work.size()) {
        while (pos < work.size() && work[pos] == L'\\') ++pos;
        if (pos >= work.size()) break;
        size_t next = work.find(L'\\', pos);
        std::wstring seg = (next == std::wstring::npos) ? work.substr(pos) : work.substr(pos, next - pos);
        pos = (next == std::wstring::npos) ? work.size() : next + 1;
        if (seg == L".") continue;
        if (seg == L"..") {
            if (parts.empty()) return false;
            parts.pop_back();
            continue;
        }
        if (seg.find(L':') != std::wstring::npos || seg.back() == L'.' || seg.back() == L' ') return false;
        parts.push_back(std::move(seg));
    }

    normalized = prefix;
    rootLength = prefix.size();
    bool prefixHasTrailingSlash = !prefix.empty() && prefix.back() == L'\\';
    for (size_t i = 0; i < parts.size(); ++i) {
        if (i > 0 || !prefixHasTrailingSlash) normalized.push_back(L'\\');
        normalized.append(parts[i]);
    }
    return true;
}

inline ErrorKind PathGuardCheck(std::wstring_view rawPath, std::wstring& canonicalPath) {
    size_t rootLength = 0;
    if (!NormalizeAbsolutePath(rawPath, canonicalPath, rootLength)) {
        return ErrorKind::InvalidState;
    }
    std::wstring current = canonicalPath;
    for (;;) {
        std::wstring apiPath = ToWin32ApiPath(current);
        DWORD attrs = GetFileAttributesW(apiPath.c_str());
        if (attrs != INVALID_FILE_ATTRIBUTES) {
            if ((attrs & FILE_ATTRIBUTE_REPARSE_POINT) != 0) {
                return ErrorKind::InvalidState;
            }
        } else {
            DWORD err = GetLastError();
            if (err == ERROR_ACCESS_DENIED || err == ERROR_SHARING_VIOLATION) {
                return ErrorKind::AccessDenied;
            }
            if (err != ERROR_FILE_NOT_FOUND && err != ERROR_PATH_NOT_FOUND) {
                return ErrorKind::InvalidState;
            }
        }
        if (current.size() <= rootLength) {
            return ErrorKind::None;
        }
        size_t slash = current.find_last_of(L'\\');
        if (slash == std::wstring::npos || slash < rootLength) {
            current = canonicalPath.substr(0, rootLength);
        } else {
            current.resize(slash);
            if (current.size() < rootLength) {
                current = canonicalPath.substr(0, rootLength);
            }
        }
    }
}

inline ErrorKind PathGuardCheck(std::wstring_view rawPath) {
    std::wstring canonical;
    return PathGuardCheck(rawPath, canonical);
}

inline ErrorKind ReadRegularFileBounded(const std::wstring& path, size_t maxBytes, std::string& out) {
    out.clear();
    std::wstring canonical;
    ErrorKind guard = PathGuardCheck(path, canonical);
    if (guard != ErrorKind::None) return guard;

    std::wstring apiPath = ToWin32ApiPath(canonical);
    ScopedHandle file(CreateFileW(
        apiPath.c_str(),
        GENERIC_READ,
        FILE_SHARE_READ | FILE_SHARE_WRITE | FILE_SHARE_DELETE,
        nullptr,
        OPEN_EXISTING,
        FILE_ATTRIBUTE_NORMAL | FILE_FLAG_OPEN_REPARSE_POINT,
        nullptr));
    if (!file.Valid()) {
        DWORD err = GetLastError();
        if (err == ERROR_ACCESS_DENIED) return ErrorKind::AccessDenied;
        if (err == ERROR_FILE_NOT_FOUND || err == ERROR_PATH_NOT_FOUND) return ErrorKind::NotFound;
        return ErrorKind::InvalidState;
    }

    BY_HANDLE_FILE_INFORMATION info{};
    if (!GetFileInformationByHandle(file.handle, &info) ||
        (info.dwFileAttributes & (FILE_ATTRIBUTE_REPARSE_POINT | FILE_ATTRIBUTE_DIRECTORY)) != 0) {
        return ErrorKind::InvalidState;
    }

    char buffer[4096];
    for (;;) {
        DWORD toRead = static_cast<DWORD>(std::min<size_t>(sizeof(buffer), (maxBytes + 1) - out.size()));
        DWORD readBytes = 0;
        if (!ReadFile(file.handle, buffer, toRead, &readBytes, nullptr)) {
            DWORD err = GetLastError();
            return (err == ERROR_ACCESS_DENIED) ? ErrorKind::AccessDenied : ErrorKind::InvalidState;
        }
        if (readBytes == 0) break;
        out.append(buffer, readBytes);
        if (out.size() > maxBytes) {
            out.clear();
            return ErrorKind::InvalidState;
        }
    }
    return ErrorKind::None;
}

inline bool IsRegularNonReparseFile(const std::wstring& path, std::wstring* canonicalOut = nullptr) {
    std::wstring canonical;
    if (PathGuardCheck(path, canonical) != ErrorKind::None) return false;
    std::wstring apiPath = ToWin32ApiPath(canonical);
    DWORD attrs = GetFileAttributesW(apiPath.c_str());
    bool ok = attrs != INVALID_FILE_ATTRIBUTES &&
        (attrs & (FILE_ATTRIBUTE_REPARSE_POINT | FILE_ATTRIBUTE_DIRECTORY)) == 0;
    if (ok && canonicalOut) {
        *canonicalOut = std::move(canonical);
    }
    return ok;
}

inline bool IsValidGeneration(std::string_view value) noexcept {
    // ^[0-9]+\.[0-9]+\.[0-9]+_[a-f0-9]{32}$
    size_t i = 0;
    for (int part = 0; part < 3; ++part) {
        size_t start = i;
        while (i < value.size() && IsAsciiDigit(value[i])) ++i;
        if (i == start) return false;
        if (part < 2) {
            if (i >= value.size() || value[i] != '.') return false;
            ++i;
        }
    }
    if (i >= value.size() || value[i] != '_') return false;
    ++i;
    if (value.size() - i != 32) return false;
    for (; i < value.size(); ++i) {
        if (!IsLowerHex(value[i])) return false;
    }
    return true;
}

inline bool IsValidDiagnosticId(std::string_view value, bool rejectAllZero) noexcept {
    if (value.size() != 32) return false;
    bool nonZero = false;
    for (char c : value) {
        if (!IsAnyHex(c)) return false;
        if (c != '0') nonZero = true;
    }
    return !rejectAllZero || nonZero;
}

// Strict RFC 8259 UTF-8 JSON reader with duplicate-key rejection and zero external dependencies.
class StrictJsonReader {
public:
    explicit StrictJsonReader(std::string_view input) noexcept : data_(input), pos_(0) {}

    void SkipWhitespace() noexcept {
        while (pos_ < data_.size()) {
            char c = data_[pos_];
            if (c == ' ' || c == '\t' || c == '\r' || c == '\n') {
                ++pos_;
            } else {
                break;
            }
        }
    }

    bool AtEnd() noexcept {
        SkipWhitespace();
        return pos_ == data_.size();
    }

    bool Consume(char expected) noexcept {
        SkipWhitespace();
        if (pos_ < data_.size() && data_[pos_] == expected) {
            ++pos_;
            return true;
        }
        return false;
    }

    char Peek() noexcept {
        SkipWhitespace();
        return pos_ < data_.size() ? data_[pos_] : '\0';
    }

    bool ReadString(std::string& out) {
        out.clear();
        SkipWhitespace();
        if (pos_ >= data_.size() || data_[pos_] != '"') return false;
        ++pos_;
        while (pos_ < data_.size()) {
            unsigned char ch = static_cast<unsigned char>(data_[pos_++]);
            if (ch == '"') return true;
            if (ch < 0x20) return false;
            if (ch != '\\') {
                out.push_back(static_cast<char>(ch));
                continue;
            }
            if (pos_ >= data_.size()) return false;
            char esc = data_[pos_++];
            switch (esc) {
                case '"': out.push_back('"'); break;
                case '\\': out.push_back('\\'); break;
                case '/': out.push_back('/'); break;
                case 'b': out.push_back('\b'); break;
                case 'f': out.push_back('\f'); break;
                case 'n': out.push_back('\n'); break;
                case 'r': out.push_back('\r'); break;
                case 't': out.push_back('\t'); break;
                case 'u': {
                    uint32_t code = 0;
                    if (!ReadHex4(code)) return false;
                    if (code >= 0xD800 && code <= 0xDBFF) {
                        if (pos_ + 2 > data_.size() || data_[pos_] != '\\' || data_[pos_ + 1] != 'u') return false;
                        pos_ += 2;
                        uint32_t low = 0;
                        if (!ReadHex4(low) || low < 0xDC00 || low > 0xDFFF) return false;
                        code = 0x10000u + (((code - 0xD800u) << 10) | (low - 0xDC00u));
                    } else if (code >= 0xDC00 && code <= 0xDFFF) {
                        return false;
                    }
                    AppendUtf8(out, code);
                    break;
                }
                default:
                    return false;
            }
        }
        return false;
    }

    bool ReadBool(bool& out) noexcept {
        SkipWhitespace();
        if (data_.substr(pos_).starts_with("true")) {
            pos_ += 4;
            out = true;
            return true;
        }
        if (data_.substr(pos_).starts_with("false")) {
            pos_ += 5;
            out = false;
            return true;
        }
        return false;
    }

    bool ReadNull() noexcept {
        SkipWhitespace();
        if (data_.substr(pos_).starts_with("null")) {
            pos_ += 4;
            return true;
        }
        return false;
    }

    bool ReadInt64(int64_t& out) noexcept {
        SkipWhitespace();
        if (pos_ >= data_.size()) return false;
        bool negative = false;
        size_t start = pos_;
        if (data_[pos_] == '-') {
            negative = true;
            ++pos_;
            if (pos_ >= data_.size()) return false;
        }
        if (data_[pos_] == '0') {
            ++pos_;
            if (pos_ < data_.size() && (IsAsciiDigit(data_[pos_]) || data_[pos_] == '.' || data_[pos_] == 'e' || data_[pos_] == 'E')) {
                return false;
            }
            out = 0;
            return true;
        }
        if (!IsAsciiDigit(data_[pos_])) return false;
        uint64_t acc = 0;
        const uint64_t limit = negative ? (static_cast<uint64_t>(std::numeric_limits<int64_t>::max()) + 1u)
                                        : static_cast<uint64_t>(std::numeric_limits<int64_t>::max());
        while (pos_ < data_.size() && IsAsciiDigit(data_[pos_])) {
            uint64_t digit = static_cast<uint64_t>(data_[pos_++] - '0');
            if (acc > (limit - digit) / 10u) return false;
            acc = acc * 10u + digit;
        }
        if (pos_ < data_.size() && (data_[pos_] == '.' || data_[pos_] == 'e' || data_[pos_] == 'E')) {
            return false;
        }
        if (pos_ == start) return false;
        if (negative) {
            out = (acc == limit) ? std::numeric_limits<int64_t>::min() : -static_cast<int64_t>(acc);
        } else {
            out = static_cast<int64_t>(acc);
        }
        return true;
    }

    bool SkipValue(int depth = 0) {
        if (depth > kMaxJsonDepth) return false;
        char c = Peek();
        if (c == '"') {
            std::string ignored;
            return ReadString(ignored);
        }
        if (c == 't' || c == 'f') {
            bool ignored = false;
            return ReadBool(ignored);
        }
        if (c == 'n') {
            return ReadNull();
        }
        if (c == '-' || IsAsciiDigit(c)) {
            return SkipNumber();
        }
        if (c == '[') {
            ++pos_;
            if (Consume(']')) return true;
            for (;;) {
                if (!SkipValue(depth + 1)) return false;
                if (Consume(']')) return true;
                if (!Consume(',')) return false;
            }
        }
        if (c == '{') {
            ++pos_;
            if (Consume('}')) return true;
            std::unordered_set<std::string> seen;
            for (;;) {
                std::string key;
                if (!ReadString(key)) return false;
                if (!seen.insert(std::move(key)).second) return false;
                if (!Consume(':')) return false;
                if (!SkipValue(depth + 1)) return false;
                if (Consume('}')) return true;
                if (!Consume(',')) return false;
            }
        }
        return false;
    }

private:
    bool ReadHex4(uint32_t& out) noexcept {
        if (pos_ + 4 > data_.size()) return false;
        uint32_t value = 0;
        for (int i = 0; i < 4; ++i) {
            char c = data_[pos_++];
            uint32_t nibble = 0;
            if (c >= '0' && c <= '9') nibble = static_cast<uint32_t>(c - '0');
            else if (c >= 'a' && c <= 'f') nibble = static_cast<uint32_t>(10 + (c - 'a'));
            else if (c >= 'A' && c <= 'F') nibble = static_cast<uint32_t>(10 + (c - 'A'));
            else return false;
            value = (value << 4) | nibble;
        }
        out = value;
        return true;
    }

    static void AppendUtf8(std::string& out, uint32_t cp) {
        if (cp <= 0x7Fu) {
            out.push_back(static_cast<char>(cp));
        } else if (cp <= 0x7FFu) {
            out.push_back(static_cast<char>(0xC0u | (cp >> 6)));
            out.push_back(static_cast<char>(0x80u | (cp & 0x3Fu)));
        } else if (cp <= 0xFFFFu) {
            out.push_back(static_cast<char>(0xE0u | (cp >> 12)));
            out.push_back(static_cast<char>(0x80u | ((cp >> 6) & 0x3Fu)));
            out.push_back(static_cast<char>(0x80u | (cp & 0x3Fu)));
        } else {
            out.push_back(static_cast<char>(0xF0u | (cp >> 18)));
            out.push_back(static_cast<char>(0x80u | ((cp >> 12) & 0x3Fu)));
            out.push_back(static_cast<char>(0x80u | ((cp >> 6) & 0x3Fu)));
            out.push_back(static_cast<char>(0x80u | (cp & 0x3Fu)));
        }
    }

    bool SkipNumber() noexcept {
        SkipWhitespace();
        if (pos_ < data_.size() && data_[pos_] == '-') ++pos_;
        if (pos_ >= data_.size()) return false;
        if (data_[pos_] == '0') {
            ++pos_;
            if (pos_ < data_.size() && IsAsciiDigit(data_[pos_])) return false;
        } else if (IsAsciiDigit(data_[pos_])) {
            while (pos_ < data_.size() && IsAsciiDigit(data_[pos_])) ++pos_;
        } else {
            return false;
        }
        if (pos_ < data_.size() && data_[pos_] == '.') {
            ++pos_;
            if (pos_ >= data_.size() || !IsAsciiDigit(data_[pos_])) return false;
            while (pos_ < data_.size() && IsAsciiDigit(data_[pos_])) ++pos_;
        }
        if (pos_ < data_.size() && (data_[pos_] == 'e' || data_[pos_] == 'E')) {
            ++pos_;
            if (pos_ < data_.size() && (data_[pos_] == '+' || data_[pos_] == '-')) ++pos_;
            if (pos_ >= data_.size() || !IsAsciiDigit(data_[pos_])) return false;
            while (pos_ < data_.size() && IsAsciiDigit(data_[pos_])) ++pos_;
        }
        return true;
    }

    std::string_view data_;
    size_t pos_;
};

inline bool ParseDeploymentState(std::string_view data, DeploymentState& out) {
    out = {};
    if (data.size() > kMaxDeploymentBytes) return false;
    StrictJsonReader reader(data);
    if (!reader.Consume('{')) return false;

    std::unordered_set<std::string> seen;
    bool hasCurrent = false;
    if (!reader.Consume('}')) {
        for (;;) {
            std::string key;
            if (!reader.ReadString(key)) return false;
            if (!seen.insert(key).second) return false;
            if (!reader.Consume(':')) return false;

            if (key == "Schema") {
                if (!reader.ReadInt64(out.schema)) return false;
            } else if (key == "Current") {
                if (reader.Peek() == 'n') {
                    if (!reader.ReadNull()) return false;
                    hasCurrent = false;
                } else {
                    if (!reader.ReadString(out.current)) return false;
                    hasCurrent = true;
                }
            } else if (key == "Previous") {
                if (reader.Peek() == 'n') {
                    if (!reader.ReadNull()) return false;
                    out.previous.reset();
                } else {
                    std::string prev;
                    if (!reader.ReadString(prev)) return false;
                    out.previous = std::move(prev);
                }
            } else if (key == "PendingHealth") {
                if (!reader.ReadBool(out.pendingHealth)) return false;
            } else {
                return false;
            }

            if (reader.Consume('}')) break;
            if (!reader.Consume(',')) return false;
        }
    }
    if (!reader.AtEnd() || seen.size() != 4) return false;
    if (out.schema != 1 || !hasCurrent || !IsValidGeneration(out.current)) return false;
    if (out.previous.has_value() && !IsValidGeneration(*out.previous)) return false;
    if (out.pendingHealth && !out.previous.has_value()) return false;
    return true;
}

inline ErrorKind ReadDeploymentFile(const std::wstring& path, DeploymentState& out) {
    std::string bytes;
    ErrorKind readErr = ReadRegularFileBounded(path, kMaxDeploymentBytes, bytes);
    if (readErr != ErrorKind::None) return readErr;
    return ParseDeploymentState(bytes, out) ? ErrorKind::None : ErrorKind::InvalidState;
}

inline bool ReadStrictManagedLayout(const std::wstring& path, PortableLayout& out) {
    out = {};
    std::string bytes;
    if (ReadRegularFileBounded(path, kMaxLayoutBytes, bytes) != ErrorKind::None) return false;

    StrictJsonReader reader(bytes);
    if (!reader.Consume('{')) return false;
    std::unordered_set<std::string> seen;
    if (!reader.Consume('}')) {
        for (;;) {
            std::string key;
            if (!reader.ReadString(key)) return false;
            if (!seen.insert(key).second) return false;
            if (!reader.Consume(':')) return false;

            if (key == "Schema") {
                if (!reader.ReadInt64(out.schema)) return false;
            } else if (key == "DiagnosticId") {
                if (!reader.ReadString(out.diagnosticId)) return false;
            } else if (key == "Managed") {
                if (!reader.ReadBool(out.managed)) return false;
            } else {
                return false;
            }

            if (reader.Consume('}')) break;
            if (!reader.Consume(',')) return false;
        }
    }
    if (!reader.AtEnd() || seen.size() != 3) return false;
    return out.schema == 1 && out.managed && IsValidDiagnosticId(out.diagnosticId, true);
}

inline bool HasPendingRecovery(const std::wstring& workspace, std::string_view expectedDiagnosticId) {
    std::wstring newPath = JoinPath(workspace, L"recovery.json.new");
    DWORD newAttrs = GetFileAttributesW(newPath.c_str());
    if (newAttrs != INVALID_FILE_ATTRIBUTES) {
        return true;
    }
    DWORD newErr = GetLastError();
    if (newErr != ERROR_FILE_NOT_FOUND && newErr != ERROR_PATH_NOT_FOUND) {
        return true;
    }

    std::wstring recPath = JoinPath(workspace, L"recovery.json");
    DWORD recAttrs = GetFileAttributesW(recPath.c_str());
    if (recAttrs == INVALID_FILE_ATTRIBUTES) {
        DWORD recErr = GetLastError();
        return recErr != ERROR_FILE_NOT_FOUND && recErr != ERROR_PATH_NOT_FOUND;
    }

    std::string bytes;
    if (ReadRegularFileBounded(recPath, kMaxRecoveryBytes, bytes) != ErrorKind::None) {
        return true;
    }

    StrictJsonReader reader(bytes);
    if (!reader.Consume('{')) return true;
    std::unordered_set<std::string> seen;
    RecoveryRecord rec{};
    if (!reader.Consume('}')) {
        for (;;) {
            std::string key;
            if (!reader.ReadString(key)) return true;
            if (!seen.insert(key).second) return true;
            if (!reader.Consume(':')) return true;

            if (key == "Schema") {
                if (!reader.ReadInt64(rec.schema)) return true;
            } else if (key == "DiagnosticId") {
                if (!reader.ReadString(rec.diagnosticId)) return true;
            } else if (key == "DataRoot") {
                if (!reader.ReadString(rec.dataRoot)) return true;
            } else if (key == "Snapshot") {
                if (!reader.ReadString(rec.snapshot)) return true;
            } else if (key == "PreviousApp") {
                if (!reader.ReadString(rec.previousApp)) return true;
            } else if (key == "Revision") {
                if (!reader.ReadInt64(rec.revision)) return true;
            } else if (key == "Completed") {
                if (!reader.ReadBool(rec.completed)) return true;
            } else {
                if (!reader.SkipValue()) return true;
            }

            if (reader.Consume('}')) break;
            if (!reader.Consume(',')) return true;
        }
    }
    if (!reader.AtEnd()) return true;

    bool settled = rec.schema == 1 &&
        rec.completed &&
        rec.revision > 0 &&
        rec.diagnosticId == expectedDiagnosticId &&
        IsValidDiagnosticId(rec.snapshot, false) &&
        IsAbsolutePathUtf8(rec.dataRoot) &&
        IsAbsolutePathUtf8(rec.previousApp);
    return !settled;
}

inline bool DocumentsSynchronized(const std::wstring& portableRoot, const std::wstring& workspace, std::string_view selected) {
    std::string receipt;
    if (ReadRegularFileBounded(JoinPath(workspace, L".documents-generation"), kMaxReceiptBytes, receipt) != ErrorKind::None) {
        return false;
    }
    if (receipt != selected) return false;
    for (const wchar_t* rel : kMirroredDocs) {
        if (!IsRegularNonReparseFile(JoinPath(portableRoot, rel))) {
            return false;
        }
    }
    return true;
}

inline ScopedHandle TryAcquireExclusiveLock(const std::wstring& path) {
    std::wstring canonical;
    if (PathGuardCheck(path, canonical) != ErrorKind::None) return {};
    std::wstring apiPath = ToWin32ApiPath(canonical);
    ScopedHandle handle(CreateFileW(
        apiPath.c_str(),
        GENERIC_READ | GENERIC_WRITE,
        0,
        nullptr,
        OPEN_ALWAYS,
        FILE_ATTRIBUTE_NORMAL | FILE_FLAG_OPEN_REPARSE_POINT,
        nullptr));
    if (!handle.Valid()) return {};
    BY_HANDLE_FILE_INFORMATION info{};
    if (!GetFileInformationByHandle(handle.handle, &info) ||
        (info.dwFileAttributes & (FILE_ATTRIBUTE_REPARSE_POINT | FILE_ATTRIBUTE_DIRECTORY)) != 0) {
        return {};
    }
    return handle;
}

inline std::wstring QuoteWindowsArg(std::wstring_view arg) {
    if (arg.empty()) return L"\"\"";
    bool needsQuotes = false;
    for (wchar_t c : arg) {
        if (c == L' ' || c == L'\t' || c == L'"') {
            needsQuotes = true;
            break;
        }
    }
    if (!needsQuotes) return std::wstring(arg);

    std::wstring out;
    out.push_back(L'"');
    size_t backslashes = 0;
    for (wchar_t c : arg) {
        if (c == L'\\') {
            ++backslashes;
        } else if (c == L'"') {
            out.append(backslashes * 2 + 1, L'\\');
            out.push_back(L'"');
            backslashes = 0;
        } else {
            if (backslashes > 0) {
                out.append(backslashes, L'\\');
                backslashes = 0;
            }
            out.push_back(c);
        }
    }
    if (backslashes > 0) {
        out.append(backslashes * 2, L'\\');
    }
    out.push_back(L'"');
    return out;
}

inline std::wstring BuildCommandLine(const std::wstring& executable, const std::vector<std::wstring>& args) {
    std::wstring line = QuoteWindowsArg(executable);
    for (const auto& arg : args) {
        line.push_back(L' ');
        line.append(QuoteWindowsArg(arg));
    }
    return line;
}

inline std::optional<std::vector<wchar_t>> BuildStrippedEnvironmentBlock() {
    LPWCH raw = GetEnvironmentStringsW();
    if (!raw) return std::nullopt;
    std::vector<wchar_t> block;
    const wchar_t* cursor = raw;
    while (*cursor != L'\0') {
        size_t len = wcslen(cursor);
        std::wstring_view entry(cursor, len);
        size_t eq = entry.find(L'=');
        std::wstring_view key = (eq == std::wstring_view::npos) ? entry : entry.substr(0, eq);
        bool strip = EqualsIgnoreCaseAscii(key, L"AUDIOPILOT_DIAGNOSTIC_SESSION") ||
            EqualsIgnoreCaseAscii(key, L"AUDIOPILOT_UPDATE_TEST_SESSION") ||
            EqualsIgnoreCaseAscii(key, L"AUDIOPILOT_UPDATE_LIFECYCLE");
        if (!strip) {
            block.insert(block.end(), cursor, cursor + len + 1);
        }
        cursor += len + 1;
    }
    FreeEnvironmentStringsW(raw);
    if (block.empty()) {
        block.push_back(L'\0');
    }
    block.push_back(L'\0');
    return block;
}

inline LaunchResult SpawnProcess(
    const std::wstring& target,
    const std::vector<std::wstring>& args,
    const std::wstring* workingDir,
    const std::vector<wchar_t>* environment,
    bool inheritHandles,
    bool waitForExit) {
    std::wstring cmdLine = BuildCommandLine(target, args);
    std::vector<wchar_t> mutableCmd(cmdLine.begin(), cmdLine.end());
    mutableCmd.push_back(L'\0');

    STARTUPINFOW si{};
    si.cb = sizeof(si);
    PROCESS_INFORMATION pi{};
    DWORD flags = environment ? CREATE_UNICODE_ENVIRONMENT : 0;

    if (!CreateProcessW(
            target.c_str(),
            mutableCmd.data(),
            nullptr,
            nullptr,
            inheritHandles ? TRUE : FALSE,
            flags,
            environment ? const_cast<wchar_t*>(environment->data()) : nullptr,
            workingDir ? workingDir->c_str() : nullptr,
            &si,
            &pi)) {
        DWORD err = GetLastError();
        if (err == ERROR_ACCESS_DENIED) return {ErrorKind::AccessDenied, 75};
        if (err == ERROR_FILE_NOT_FOUND || err == ERROR_PATH_NOT_FOUND) return {ErrorKind::NotFound, 74};
        return {ErrorKind::LaunchFailed, 74};
    }

    ScopedHandle process(pi.hProcess);
    ScopedHandle thread(pi.hThread);
    if (!waitForExit) {
        return {ErrorKind::None, 0};
    }
    if (WaitForSingleObject(process.handle, INFINITE) != WAIT_OBJECT_0) {
        return {ErrorKind::LaunchFailed, 74};
    }
    DWORD exitCode = 1;
    if (!GetExitCodeProcess(process.handle, &exitCode)) {
        return {ErrorKind::LaunchFailed, 74};
    }
    if (exitCode == 0) {
        return {ErrorKind::None, 0};
    }
    return {ErrorKind::ChildExit, static_cast<int>(exitCode)};
}

inline bool TryFastLaunchManagedGui(
    const std::wstring& portableRoot,
    const std::wstring& workspace,
    const std::wstring& folder,
    std::string_view selected,
    const std::vector<std::wstring>& args) {
    if (!g_packagedLauncher) return false;

    ScopedHandle lock = TryAcquireExclusiveLock(JoinPath(workspace, L"lifecycle.lock"));
    if (!lock.Valid()) return false;

    // Re-read deployment state under lifecycle.lock to eliminate TOCTOU with concurrent updates.
    DeploymentState refreshed{};
    if (ReadDeploymentFile(JoinPath(workspace, L"deployment.json"), refreshed) != ErrorKind::None ||
        refreshed.pendingHealth ||
        refreshed.current != selected) {
        return false;
    }

    PortableLayout layout{};
    if (!ReadStrictManagedLayout(JoinPath(workspace, L"portable-layout.json"), layout)) {
        return false;
    }
    if (HasPendingRecovery(workspace, layout.diagnosticId) ||
        !DocumentsSynchronized(portableRoot, workspace, selected)) {
        return false;
    }

    std::wstring target = JoinPath(folder, L"AudioPilot.exe");
    std::wstring canonicalTarget;
    if (!IsRegularNonReparseFile(target, &canonicalTarget)) {
        return false;
    }

    std::optional<std::vector<wchar_t>> env = BuildStrippedEnvironmentBlock();
    if (!env.has_value()) {
        return false;
    }
    LaunchResult launch = SpawnProcess(canonicalTarget, args, &folder, &(*env), false, false);
    return launch.kind == ErrorKind::None;
}

inline ErrorKind RequireUnelevated() noexcept {
    TOKEN_ELEVATION elevation{};
    DWORD size = 0;
    if (!GetTokenInformation(GetCurrentProcessToken(), TokenElevation, &elevation, sizeof(elevation), &size)) {
        DWORD err = GetLastError();
        return (err == ERROR_ACCESS_DENIED) ? ErrorKind::AccessDenied : ErrorKind::InvalidState;
    }
    return elevation.TokenIsElevated != 0 ? ErrorKind::Elevated : ErrorKind::None;
}

template <typename ElevationCheck>
inline LaunchResult RunLauncher(
    const std::wstring& executable,
    const std::vector<std::wstring>& args,
    ElevationCheck&& checkElevation,
    bool forceCli) {
    std::wstring portableRoot = ParentDirOf(executable);
    std::wstring root = JoinPath(portableRoot, L"app");
    ErrorKind guard = PathGuardCheck(root);
    if (guard != ErrorKind::None) return {guard, guard == ErrorKind::AccessDenied ? 75 : 74};

    std::string marker;
    ErrorKind markerErr = ReadRegularFileBounded(JoinPath(root, L".updater"), 64, marker);
    if (markerErr != ErrorKind::None) {
        return {markerErr, markerErr == ErrorKind::AccessDenied ? 75 : 74};
    }
    if (marker != kWorkspaceMarker) {
        return {ErrorKind::InvalidState, 74};
    }

    DeploymentState current{};
    ErrorKind stateErr = ReadDeploymentFile(JoinPath(root, L"deployment.json"), current);
    if (stateErr != ErrorKind::None) {
        return {stateErr, stateErr == ErrorKind::AccessDenied ? 75 : 74};
    }

    const std::string& selected = current.pendingHealth ? *current.previous : current.current;
    std::wstring selectedWide = Utf8ToWide(selected);
    if (selectedWide.empty()) return {ErrorKind::InvalidState, 74};

    std::wstring folder = JoinPath(JoinPath(root, L"versions"), selectedWide);
    bool cli = forceCli || IsCliExecutableName(executable);

    std::wstring targetName;
    std::vector<std::wstring> launchArgs;
    if (cli) {
        targetName = L"AudioPilot.Cli.exe";
        launchArgs = args;
    } else {
        ErrorKind elev = checkElevation();
        if (elev != ErrorKind::None) {
            return {elev, 75};
        }
        if (!current.pendingHealth && TryFastLaunchManagedGui(portableRoot, root, folder, selected, args)) {
            return {ErrorKind::None, 0};
        }
        targetName = L"AudioPilot.Updater.exe";
        launchArgs.reserve(args.size() + 2);
        launchArgs.push_back(L"pilot-launch");
        launchArgs.push_back(root);
        launchArgs.insert(launchArgs.end(), args.begin(), args.end());
    }

    std::wstring target = JoinPath(folder, targetName);
    std::wstring canonicalTarget;
    ErrorKind targetGuard = PathGuardCheck(target, canonicalTarget);
    if (targetGuard != ErrorKind::None) {
        return {targetGuard, targetGuard == ErrorKind::AccessDenied ? 75 : 74};
    }

    return SpawnProcess(canonicalTarget, launchArgs, cli ? nullptr : &folder, nullptr, cli, true);
}

// These exit codes form the small contract with PortableLaunchFailure in UpdateProtocol.
inline LaunchFailure FailureFor(const LaunchResult& result) noexcept {
    if (result.kind == ErrorKind::ChildExit) {
        switch (result.exitCode) {
            case 73:
                return {"launcher-busy", "Another AudioPilot launch or update is in progress. Wait for it to finish, then try again.", 73};
            case 74:
                return {"launcher-state-invalid", "AudioPilot files or update state are missing or invalid. Reopen AudioPilot to recover, or extract or reinstall a complete copy.", 74};
            case 75:
                return {"launcher-access-denied", "AudioPilot could not access its files. Check the folder permissions, then try again.", 75};
            default:
                return {"launcher-start-failed", "AudioPilot could not finish starting. Try again. If this continues, extract or reinstall a complete copy.", result.exitCode};
        }
    }
    if (result.kind == ErrorKind::Elevated) {
        return {"launcher-elevated", "Open AudioPilot without administrator privileges to use the portable launcher.", 75};
    }
    if (result.kind == ErrorKind::AccessDenied) {
        return {"launcher-access-denied", "AudioPilot could not access its files. Check the folder permissions, then try again.", 75};
    }
    if (result.kind == ErrorKind::NotFound) {
        return {"launcher-state-invalid", "AudioPilot files are missing. Extract or reinstall a complete copy.", 74};
    }
    return {"launcher-state-invalid", "AudioPilot files or update state could not be read. Close AudioPilot and reopen it to recover. If this continues, extract or reinstall a complete copy.", 74};
}

inline bool WantsJson(const std::vector<std::wstring>& args) noexcept {
    for (const auto& arg : args) {
        size_t start = 0;
        size_t end = arg.size();
        while (start < end && (arg[start] == L' ' || arg[start] == L'\t' || arg[start] == L'\r' || arg[start] == L'\n')) ++start;
        while (end > start && (arg[end - 1] == L' ' || arg[end - 1] == L'\t' || arg[end - 1] == L'\r' || arg[end - 1] == L'\n')) --end;
        if (EqualsIgnoreCaseAscii(std::wstring_view(arg).substr(start, end - start), L"--json")) {
            return true;
        }
    }
    return false;
}

inline void AppendJsonEscaped(std::string& out, std::string_view value) {
    for (unsigned char ch : value) {
        switch (ch) {
            case '"': out.append("\\\""); break;
            case '\\': out.append("\\\\"); break;
            case '\b': out.append("\\b"); break;
            case '\f': out.append("\\f"); break;
            case '\n': out.append("\\n"); break;
            case '\r': out.append("\\r"); break;
            case '\t': out.append("\\t"); break;
            default:
                if (ch < 0x20) {
                    static constexpr char kHex[] = "0123456789abcdef";
                    out.append("\\u00");
                    out.push_back(kHex[(ch >> 4) & 0x0F]);
                    out.push_back(kHex[ch & 0x0F]);
                } else {
                    out.push_back(static_cast<char>(ch));
                }
                break;
        }
    }
}

inline std::string FormatCliFailure(const std::vector<std::wstring>& args, const LaunchResult& result, bool& writeToStdout) {
    LaunchFailure failure = FailureFor(result);
    if (WantsJson(args)) {
        writeToStdout = true;
        std::string json = "{\"data\":null,\"error\":{\"code\":\"";
        AppendJsonEscaped(json, failure.code);
        json.append("\",\"exitCode\":");
        json.append(std::to_string(failure.exitCode));
        json.append(",\"message\":\"");
        AppendJsonEscaped(json, failure.message);
        json.append("\"},\"ok\":false,\"schemaVersion\":\"1.0.0\"}\n");
        return json;
    }
    writeToStdout = false;
    std::string text = "[diag-code:";
    text.append(failure.code);
    text.append("] ");
    text.append(failure.message);
    text.push_back('\n');
    return text;
}

inline void WriteToStandardHandle(DWORD stdHandleId, std::string_view payload) noexcept {
    HANDLE handle = GetStdHandle(stdHandleId);
    if (handle == INVALID_HANDLE_VALUE || handle == nullptr) return;
    DWORD written = 0;
    WriteFile(handle, payload.data(), static_cast<DWORD>(payload.size()), &written, nullptr);
}

inline void ShowFailure(const LaunchResult& result, const std::vector<std::wstring>& args, bool cli) {
    if (cli) {
        bool toStdout = false;
        std::string payload = FormatCliFailure(args, result, toStdout);
        WriteToStandardHandle(toStdout ? STD_OUTPUT_HANDLE : STD_ERROR_HANDLE, payload);
        return;
    }
    LaunchFailure failure = FailureFor(result);
    std::wstring message = L"AudioPilot could not start.\n\n" + Utf8ToWide(failure.message);
    MessageBoxW(nullptr, message.c_str(), L"AudioPilot", MB_ICONERROR);
}

inline std::wstring GetCurrentExecutablePath() {
    std::wstring buffer(MAX_PATH, L'\0');
    for (;;) {
        DWORD len = GetModuleFileNameW(nullptr, buffer.data(), static_cast<DWORD>(buffer.size()));
        if (len == 0) return {};
        if (len < buffer.size() - 1) {
            buffer.resize(len);
            return buffer;
        }
        if (buffer.size() >= 32768) return {};
        buffer.resize(buffer.size() * 2);
    }
}

inline int ExecuteMain(const std::vector<std::wstring>& args, std::wstring_view argv0, bool compiledCli) {
    std::wstring executable = GetCurrentExecutablePath();
    bool cli = compiledCli || IsCliExecutableName(executable) || (!argv0.empty() && IsCliExecutableName(argv0));
    LaunchResult result = executable.empty()
        ? LaunchResult{ErrorKind::InvalidState, 74}
        : RunLauncher(executable, args, RequireUnelevated, compiledCli);
    if (result.kind != ErrorKind::None) {
        int code = (result.kind == ErrorKind::ChildExit) ? result.exitCode : FailureFor(result).exitCode;
        if (result.kind != ErrorKind::ChildExit || !cli) {
            ShowFailure(result, args, cli);
        }
        return code;
    }
    return 0;
}

}  // namespace audiopilot::launcher

#ifndef PORTABLE_LAUNCHER_TESTING
#ifdef CLI_LAUNCHER
int wmain(int argc, wchar_t* argv[]) {
    std::vector<std::wstring> args;
    if (argc > 1) {
        args.reserve(static_cast<size_t>(argc - 1));
        for (int i = 1; i < argc; ++i) {
            args.emplace_back(argv[i]);
        }
    }
    return audiopilot::launcher::ExecuteMain(args, argc > 0 ? std::wstring_view(argv[0]) : std::wstring_view{}, true);
}
#else
int WINAPI wWinMain(HINSTANCE, HINSTANCE, PWSTR, int) {
    int argc = 0;
    LPWSTR* argv = CommandLineToArgvW(GetCommandLineW(), &argc);
    std::vector<std::wstring> args;
    std::wstring argv0;
    if (argv) {
        if (argc > 0) argv0 = argv[0];
        if (argc > 1) {
            args.reserve(static_cast<size_t>(argc - 1));
            for (int i = 1; i < argc; ++i) {
                args.emplace_back(argv[i]);
            }
        }
        LocalFree(argv);
    }
    return audiopilot::launcher::ExecuteMain(args, argv0, false);
}
#endif
#endif
