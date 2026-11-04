#include "NativeIdentity.h"
#include <tlhelp32.h>
#include <bcrypt.h>
#include <iostream>
#include <sstream>
#include <stdexcept>
#include <array>
#include <cstring>

namespace dx5::appleprobe {
namespace {
struct MappedView {
    ProbeMapping* value{};
    ~MappedView() { if (value) UnmapViewOfFile(value); }
};
struct LocalMemory {
    void* value{};
    ~LocalMemory() { if (value) LocalFree(value); }
};
struct RemotePathBuffer {
    HANDLE process{};
    void* value{};
    bool inUse{};
    ~RemotePathBuffer() { if (value && !inUse) VirtualFreeEx(process, value, 0, MEM_RELEASE); }
    void ReleaseCompleted() { inUse = false; if (value) VirtualFreeEx(process, value, 0, MEM_RELEASE); value = nullptr; }
};
struct ObservationStopGuard {
    ProbeMapping* mapping{};
    ~ObservationStopGuard() { if (mapping) InterlockedExchange(&mapping->stopRequested, 1); }
};
void Require(bool condition, const char* message) {
    if (!condition) throw std::runtime_error(message);
}
DWORD ParseUnsigned(const wchar_t* text, DWORD maximum) {
    Require(text && *text, "A positive integer is required.");
    std::uint64_t value{};
    for (const auto* at = text; *at; ++at) {
        Require(*at >= L'0' && *at <= L'9', "A positive integer is required.");
        value = value * 10 + (*at - L'0');
        Require(value <= maximum, "Integer argument is outside the permitted range.");
    }
    Require(value != 0, "Zero is not permitted.");
    return static_cast<DWORD>(value);
}
bool TargetIsStillAllowed(HANDLE process, const Identity& expected, const Identity& self) {
    Identity current{};
    return WaitForSingleObject(process, 0) == WAIT_TIMEOUT && ReadIdentity(process, current) &&
        IsAllowedAgent(current) && SameUserSession(current, self) &&
        current.pid == expected.pid && current.started == expected.started && SameWindowsPath(current.path, expected.path);
}
bool HasModule(DWORD pid, const wchar_t* name) {
    NativeHandle snapshot(CreateToolhelp32Snapshot(TH32CS_SNAPMODULE | TH32CS_SNAPMODULE32, pid));
    Require(snapshot.value != INVALID_HANDLE_VALUE, "Cannot inspect the target's loaded modules.");
    MODULEENTRY32W module{sizeof(module)};
    if (!Module32FirstW(snapshot.value, &module)) {
        Require(GetLastError() == ERROR_NO_MORE_FILES, "Target module enumeration failed.");
        return false;
    }
    do { if (_wcsicmp(module.szModule, name) == 0) return true; }
    while (Module32NextW(snapshot.value, &module));
    Require(GetLastError() == ERROR_NO_MORE_FILES, "Target module enumeration changed or failed; refusing attachment.");
    return false;
}
LPTHREAD_START_ROUTINE RemoteLoadLibrary(DWORD pid) {
    const auto function = GetProcAddress(GetModuleHandleW(L"kernel32.dll"), "LoadLibraryW");
    Require(function != nullptr, "Cannot locate the Windows loader entry point.");
    HMODULE owner{};
    Require(GetModuleHandleExW(GET_MODULE_HANDLE_EX_FLAG_FROM_ADDRESS | GET_MODULE_HANDLE_EX_FLAG_UNCHANGED_REFCOUNT,
        reinterpret_cast<LPCWSTR>(function), &owner) != FALSE, "Cannot identify the Windows loader module.");
    const auto ownerPath = ModulePath(owner);
    const auto offset = reinterpret_cast<std::uintptr_t>(function) - reinterpret_cast<std::uintptr_t>(owner);
    NativeHandle snapshot(CreateToolhelp32Snapshot(TH32CS_SNAPMODULE | TH32CS_SNAPMODULE32, pid));
    Require(snapshot.value != INVALID_HANDLE_VALUE, "Cannot inspect target loader modules.");
    MODULEENTRY32W module{sizeof(module)};
    if (Module32FirstW(snapshot.value, &module)) {
        do {
            if (SameWindowsPath(module.szExePath, ownerPath) && offset < module.modBaseSize)
                return reinterpret_cast<LPTHREAD_START_ROUTINE>(module.modBaseAddr + offset);
        } while (Module32NextW(snapshot.value, &module));
    }
    throw std::runtime_error("The target does not have the expected Windows loader module.");
}
std::string Describe(const ProbeSnapshot& snapshot, ProbeState state, DWORD pid, DWORD error) {
    std::ostringstream output;
    output << "{\"experimental\":true,\"targetPid\":" << pid
        << ",\"state\":" << static_cast<LONG>(state) << ",\"error\":" << error
        << ",\"confidence\":\"source stream candidate\",\"currentTrackVerified\":false,\"tick\":" << snapshot.observedTick
        << ",\"revision\":" << snapshot.revision << ",\"sourceCandidateHz\":" << snapshot.uniqueSourceRateHz
        << ",\"conflicting\":" << (snapshot.conflicting ? "true" : "false")
        << ",\"droppedEvents\":" << snapshot.droppedEvents << ",\"capacityDrops\":" << snapshot.capacityDrops
        << ",\"activeConverters\":" << snapshot.activeConverters
        << ",\"raw\":{\"new\":" << snapshot.newCalls << ",\"newSpecific\":" << snapshot.newSpecificCalls
        << ",\"setProperty\":" << snapshot.setPropertyCalls << ",\"getProperty\":" << snapshot.getPropertyCalls
        << ",\"fill\":" << snapshot.fillCalls << ",\"fillErrors\":" << snapshot.fillErrors
        << ",\"emptySuccessfulFills\":" << snapshot.emptySuccessfulFills << ",\"nonemptySuccessfulFills\":" << snapshot.nonemptySuccessfulFills
        << ",\"reset\":" << snapshot.resetCalls << ",\"dispose\":" << snapshot.disposeCalls
        << ",\"metadataRefreshAttempts\":" << snapshot.metadataRefreshAttempts << ",\"metadataRefreshFailures\":" << snapshot.metadataRefreshFailures
        << ",\"lastFillStatus\":" << (snapshot.hasFillStatus ? std::to_string(snapshot.lastFillStatus) : "null")
        << ",\"lastMetadataStatus\":" << (snapshot.hasMetadataStatus ? std::to_string(snapshot.lastMetadataStatus) : "null")
        << "},\"reports\":[";
    const auto count = snapshot.reportCount > kMaximumReports ? kMaximumReports : snapshot.reportCount;
    for (std::uint32_t index = 0; index < count; ++index) {
        const auto& report = snapshot.reports[index];
        if (index) output << ',';
        output << "{\"generation\":" << report.generation << ",\"status\":" << report.status
            << ",\"sourceRateHz\":" << report.sourceRateHz << ",\"sourceChannels\":" << report.sourceChannels
            << ",\"sourceBits\":" << report.sourceBits << ",\"sourceCodec\":" << report.sourceCodec
            << ",\"decodedRateHz\":" << report.decodedRateHz << ",\"cookieRateHz\":" << report.cookieRateHz
            << ",\"cookieBits\":" << report.cookieBits << ",\"fills\":" << report.successfulFills
            << ",\"lastFillAgeMs\":" << report.lastFillAgeMs << '}';
    }
    output << "]}\n";
    return output.str();
}
bool ReadSnapshot(ProbeMapping* mapping, ProbeSnapshot& snapshot) noexcept {
    for (int attempt = 0; attempt < 4; ++attempt) {
        const auto before = InterlockedCompareExchange(&mapping->sequence, 0, 0);
        if (before & 1) continue;
        snapshot = mapping->snapshot;
        MemoryBarrier();
        if (before == InterlockedCompareExchange(&mapping->sequence, 0, 0)) return true;
    }
    return false;
}
int Inspect(DWORD pid) {
    NativeHandle target(OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION | SYNCHRONIZE, FALSE, pid));
    Require(target.value != nullptr, "Cannot open the requested process for inspection.");
    Identity identity{}, self{};
    Require(ReadIdentity(target.value, identity) && ReadIdentity(GetCurrentProcess(), self), "Cannot read process identity.");
    const bool allowed = IsAllowedAgent(identity) && SameUserSession(identity, self);
    std::wcout << L"PID: " << identity.pid << L"\nStarted: " << identity.started << L"\nPath: " << identity.path
        << L"\nPackage: " << identity.package << L"\nSame user/session: " << (SameUserSession(identity, self) ? L"yes" : L"no")
        << L"\nAllowed target: " << (allowed ? L"yes" : L"no") << L"\nNo injection performed.\n";
    return allowed ? 0 : 3;
}
int Run(DWORD pid, DWORD seconds, const std::filesystem::path& outputPath) {
    Require(outputPath.is_absolute() && outputPath.has_filename(), "Output must be an explicit absolute path to a new JSONL file.");
    NativeHandle target(OpenProcess(PROCESS_CREATE_THREAD | PROCESS_QUERY_INFORMATION | PROCESS_QUERY_LIMITED_INFORMATION |
        PROCESS_VM_OPERATION | PROCESS_VM_WRITE | PROCESS_VM_READ | SYNCHRONIZE, FALSE, pid));
    Require(target.value != nullptr, "Cannot acquire the explicitly requested process rights. No elevation is attempted.");
    Identity identity{}, self{};
    Require(ReadIdentity(target.value, identity) && ReadIdentity(GetCurrentProcess(), self), "Cannot validate target identity.");
    Require(IsAllowedAgent(identity), "Refusing target: only the pinned official Apple Music AMPLibraryAgent package is permitted.");
    Require(SameUserSession(identity, self), "Refusing a different user or Windows session.");
    Require(!HasModule(pid, kDllName), "Probe DLL is already resident. Restart this exact Apple agent before another run.");
    Require(!HasModule(pid, L"am-exclusive-hook.dll"), "An AMExclusive hook is already loaded; do not chain this experimental probe with it.");
    const auto dll = std::filesystem::path(ModulePath()).parent_path() / kDllName;
    Require(std::filesystem::is_regular_file(dll), "The adjacent reporting DLL is missing.");
    const auto dllPath = dll.wstring();
    Require(dllPath.size() < 32767, "DLL path is too long.");
    LocalMemory descriptor;
    const std::wstring sddl = L"D:P(A;;GA;;;" + self.sid + L")S:(ML;;NW;;;ME)";
    Require(ConvertStringSecurityDescriptorToSecurityDescriptorW(sddl.c_str(), SDDL_REVISION_1,
        reinterpret_cast<PSECURITY_DESCRIPTOR*>(&descriptor.value), nullptr) != FALSE, "Cannot secure the diagnostic objects.");
    SECURITY_ATTRIBUTES security{sizeof(security), descriptor.value, FALSE};
    SetLastError(ERROR_SUCCESS);
    NativeHandle mapping(CreateFileMappingW(INVALID_HANDLE_VALUE, &security, PAGE_READWRITE, 0, sizeof(ProbeMapping), MappingName(pid).c_str()));
    const auto mappingError = GetLastError();
    Require(mapping.value && mappingError != ERROR_ALREADY_EXISTS, "Diagnostic mapping already exists or cannot be created; nothing attached.");
    MappedView view;
    view.value = static_cast<ProbeMapping*>(MapViewOfFile(mapping.value, FILE_MAP_READ | FILE_MAP_WRITE, 0, 0, sizeof(ProbeMapping)));
    Require(view.value != nullptr, "Cannot map diagnostic memory.");
    new (view.value) ProbeMapping{};
    ObservationStopGuard stopOnExit{view.value};
    view.value->targetPid = pid;
    view.value->targetStarted = identity.started;
    view.value->loaderPid = self.pid;
    view.value->loaderStarted = self.started;
    view.value->stopAtTick = GetTickCount64() + seconds * 1000ULL;
    Require(BCryptGenRandom(nullptr, view.value->nonce, sizeof(view.value->nonce), BCRYPT_USE_SYSTEM_PREFERRED_RNG) >= 0,
        "Cannot generate the diagnostic session nonce.");
    NativeHandle output(CreateFileW(outputPath.c_str(), GENERIC_WRITE, 0, &security, CREATE_NEW, FILE_ATTRIBUTE_NORMAL, nullptr));
    Require(output.value != INVALID_HANDLE_VALUE, "Output already exists or cannot be created; nothing attached.");
    const auto loadLibrary = RemoteLoadLibrary(pid);
    Require(TargetIsStillAllowed(target.value, identity, self), "Target changed before attachment.");
    const auto bytes = (dllPath.size() + 1) * sizeof(wchar_t);
    RemotePathBuffer remote{target.value, VirtualAllocEx(target.value, nullptr, bytes, MEM_COMMIT | MEM_RESERVE, PAGE_READWRITE)};
    Require(remote.value != nullptr, "Cannot allocate the bounded DLL-path buffer in the target.");
    SIZE_T written{};
    if (!WriteProcessMemory(target.value, remote.value, dllPath.c_str(), bytes, &written) || written != bytes ||
        !TargetIsStillAllowed(target.value, identity, self)) {
        throw std::runtime_error("Target changed or DLL path could not be written; no remote thread started.");
    }
    NativeHandle thread(CreateRemoteThread(target.value, nullptr, 0, loadLibrary, remote.value, 0, nullptr));
    if (!thread.value) {
        throw std::runtime_error("Cannot start the explicit one-time DLL load.");
    }
    remote.inUse = true;
    const auto loaded = WaitForSingleObject(thread.value, 5000);
    if (loaded == WAIT_OBJECT_0) remote.ReleaseCompleted();
    if (loaded != WAIT_OBJECT_0) {
        InterlockedExchange(&view.value->stopRequested, 1);
        throw std::runtime_error("Remote loader did not complete in five seconds. No thread was terminated; bounded path memory may remain until process exit.");
    }
    // Never trust a truncated x64 HMODULE returned as a thread exit DWORD.
    if (!HasModule(pid, kDllName)) {
        InterlockedExchange(&view.value->stopRequested, 1);
        throw std::runtime_error("Reporting DLL did not appear in the target module list.");
    }
    std::uint64_t outputBytes{};
    ProbeSnapshot snapshot{};
    ProbeState state = ProbeState::Waiting;
    const auto emit = [&] {
        const auto line = Describe(snapshot, state, pid, static_cast<DWORD>(InterlockedCompareExchange(&view.value->error, 0, 0)));
        Require(outputBytes + line.size() <= 1024 * 1024, "Diagnostic output reached its one MiB bound.");
        DWORD count{};
        Require(WriteFile(output.value, line.data(), static_cast<DWORD>(line.size()), &count, nullptr) && count == line.size(),
            "Cannot write the local diagnostic record.");
        outputBytes += count;
        std::cout << line;
    };
    try {
        while (GetTickCount64() < view.value->stopAtTick && WaitForSingleObject(target.value, 0) == WAIT_TIMEOUT) {
            state = static_cast<ProbeState>(InterlockedCompareExchange(&view.value->state, 0, 0));
            if (ReadSnapshot(view.value, snapshot)) emit();
            if (state == ProbeState::Rejected || state == ProbeState::HookUnavailable || state == ProbeState::Stopped) break;
            Sleep(200);
        }
        InterlockedExchange(&view.value->stopRequested, 1);
        const auto stopDeadline = GetTickCount64() + 2000;
        while (GetTickCount64() < stopDeadline &&
            InterlockedCompareExchange(&view.value->state, 0, 0) == static_cast<LONG>(ProbeState::Observing)) Sleep(25);
        state = static_cast<ProbeState>(InterlockedCompareExchange(&view.value->state, 0, 0));
        ReadSnapshot(view.value, snapshot);
        emit();
    } catch (...) {
        InterlockedExchange(&view.value->stopRequested, 1);
        throw;
    }
    return state == ProbeState::Stopped ? 0 : 4;
}
}
}
int wmain(int argc, wchar_t** argv) {
    using namespace dx5::appleprobe;
    try {
        if (argc == 2 && std::wstring(argv[1]) == L"--validate-self") {
            Identity identity{};
            Require(ReadIdentity(GetCurrentProcess(), identity), "Cannot inspect self.");
            Require(!IsAllowedAgent(identity), "Self unexpectedly accepted as Apple target.");
            std::cout << "PASS: ordinary process rejected without allocation, writes or injection.\n";
            return 0;
        }
        if (argc == 3 && std::wstring(argv[1]) == L"--inspect-pid") return Inspect(ParseUnsigned(argv[2], MAXDWORD));
        if (argc == 7 && std::wstring(argv[1]) == L"--pid" && std::wstring(argv[3]) == L"--seconds" && std::wstring(argv[5]) == L"--output")
            return Run(ParseUnsigned(argv[2], MAXDWORD), ParseUnsigned(argv[4], 30), argv[6]);
        std::cerr << "Diagnostic only. Current-track binding is unverified.\n"
            "Read-only inspection: --inspect-pid PID\n"
            "Explicit attachment: --pid PID --seconds 1..30 --output ABSOLUTE_NEW_JSONL\n"
            "The injected DLL/pass-through detours remain until this exact Apple agent exits.\n";
        return 2;
    } catch (const std::exception& error) {
        std::cerr << "Probe stopped: " << error.what() << '\n';
        return 1;
    }
}
