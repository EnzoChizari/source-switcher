#pragma once
#include "ProbeProtocol.h"
#include <appmodel.h>
#include <sddl.h>
#include <string>
#include <vector>
#include <filesystem>

namespace dx5::appleprobe {
struct NativeHandle {
    HANDLE value{};
    ~NativeHandle() { if (value && value != INVALID_HANDLE_VALUE) CloseHandle(value); }
    NativeHandle() = default;
    explicit NativeHandle(HANDLE handle) : value(handle) {}
    NativeHandle(const NativeHandle&) = delete;
    NativeHandle& operator=(const NativeHandle&) = delete;
};
struct Identity {
    DWORD pid{}, session{};
    std::uint64_t started{};
    std::wstring path, sid, package;
};
inline bool ReadIdentity(HANDLE process, Identity& identity) {
    identity = {};
    identity.pid = GetProcessId(process);
    identity.started = ProcessStarted(process);
    if (!identity.pid || !identity.started || !ProcessIdToSessionId(identity.pid, &identity.session)) return false;
    wchar_t path[32768]{};
    DWORD pathLength = static_cast<DWORD>(std::size(path));
    if (!QueryFullProcessImageNameW(process, 0, path, &pathLength)) return false;
    identity.path.assign(path, pathLength);
    NativeHandle token;
    if (!OpenProcessToken(process, TOKEN_QUERY, &token.value)) return false;
    DWORD needed{};
    GetTokenInformation(token.value, TokenUser, nullptr, 0, &needed);
    if (!needed || needed > 65536) return false;
    std::vector<std::uint8_t> user(needed);
    if (!GetTokenInformation(token.value, TokenUser, user.data(), needed, &needed)) return false;
    LPWSTR sid{};
    if (!ConvertSidToStringSidW(reinterpret_cast<TOKEN_USER*>(user.data())->User.Sid, &sid)) return false;
    identity.sid = sid;
    LocalFree(sid);
    UINT32 packageLength{};
    if (GetPackageFullName(process, &packageLength, nullptr) == ERROR_INSUFFICIENT_BUFFER && packageLength < 4096) {
        std::vector<wchar_t> package(packageLength);
        if (GetPackageFullName(process, &packageLength, package.data()) == ERROR_SUCCESS) identity.package = package.data();
    }
    return true;
}
inline bool SameWindowsPath(const std::wstring& left, const std::wstring& right) noexcept {
    return CompareStringOrdinal(left.c_str(), -1, right.c_str(), -1, TRUE) == CSTR_EQUAL;
}
inline bool IsAllowedAgent(const Identity& identity) {
    if (identity.package != kPackageName) return false;
    const std::filesystem::path path(identity.path);
    return SameWindowsPath(path.filename().wstring(), L"AMPLibraryAgent.exe") &&
        SameWindowsPath(path.parent_path().filename().wstring(), kPackageName) &&
        SameWindowsPath(path.parent_path().parent_path().filename().wstring(), L"WindowsApps");
}
inline std::wstring ModulePath(HMODULE module = nullptr) {
    std::wstring path(32768, L'\0');
    const auto length = GetModuleFileNameW(module, path.data(), static_cast<DWORD>(path.size()));
    if (!length || length >= path.size()) return {};
    path.resize(length);
    return path;
}
inline std::wstring MappingName(DWORD pid) { return L"Local\\DX5AppleSourceProbe-v1-" + std::to_wstring(pid); }
inline bool SameUserSession(const Identity& left, const Identity& right) noexcept {
    return left.sid == right.sid && left.session == right.session;
}
}
