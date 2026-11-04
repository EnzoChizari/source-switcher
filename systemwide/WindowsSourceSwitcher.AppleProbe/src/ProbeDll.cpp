// Metadata-only adaptation of AMExclusive's AudioConverter observation approach.
// AMExclusive c67c2f3799350ea0394d8522b3422660897a55d5 (MIT); notices included.
// No upstream renderer, quality policy, private RVA, UI, or Windows COM hook is used.
#include "ConverterEvidenceTable.h"
#include "NativeIdentity.h"
#include <MinHook.h>
#include <atomic>

namespace dx5::appleprobe {
namespace {
using Status = std::int32_t;
using NewFn = Status(__cdecl*)(const AppleAsbd*, const AppleAsbd*, void**);
using NewSpecificFn = Status(__cdecl*)(const AppleAsbd*, const AppleAsbd*, std::uint32_t, const void*, void**);
using SetPropertyFn = Status(__cdecl*)(void*, std::uint32_t, std::uint32_t, const void*);
using GetPropertyFn = Status(__cdecl*)(void*, std::uint32_t, std::uint32_t*, void*);
using FillFn = Status(__cdecl*)(void*, void*, void*, std::uint32_t*, void*, void*);
using UnaryFn = Status(__cdecl*)(void*);
NewFn originalNew{};
NewSpecificFn originalSpecific{};
SetPropertyFn originalSet{};
GetPropertyFn originalGet{};
FillFn originalFill{};
UnaryFn originalReset{}, originalDispose{};
ConverterEvidenceTable evidence;
std::atomic<bool> observing{};
struct DiagnosticCounters {
    std::atomic<std::uint64_t> newCalls{}, specificCalls{}, setCalls{}, getCalls{}, fillCalls{}, fillErrors{}, emptyFills{}, nonemptyFills{};
    std::atomic<std::uint64_t> resetCalls{}, disposeCalls{}, refreshAttempts{}, refreshFailures{};
    std::atomic<std::int32_t> lastFillStatus{}, lastMetadataStatus{};
    std::atomic<bool> hasFillStatus{}, hasMetadataStatus{};
} counters;
HMODULE ownModule{};
constexpr std::uint32_t kInputDescription = 0x61636964;
constexpr std::uint32_t kOutputDescription = 0x61636f64;
constexpr std::uint32_t kMagicCookie = 0x646d6763;

// Apple owns these buffers. Copy only bounded format metadata while its native
// API call still guarantees the parameter's lifetime. Never dereference PCM.
bool CopyAsbd(const AppleAsbd* input, AppleAsbd& output) noexcept {
    if (!input) return false;
    __try { output = *input; return true; }
    __except (EXCEPTION_EXECUTE_HANDLER) { return false; }
}
bool CopyCookie(const void* input, std::uint32_t length, std::uint8_t* output, std::uint32_t& copied) noexcept {
    copied = 0;
    if (!input || length > 4096) return false;
    const auto count = length < 48 ? length : 48;
    __try {
        const auto* bytes = static_cast<const std::uint8_t*>(input);
        for (std::uint32_t index = 0; index < count; ++index) output[index] = bytes[index];
        copied = count;
        return true;
    } __except (EXCEPTION_EXECUTE_HANDLER) { return false; }
}
bool ProducedPackets(const std::uint32_t* packets) noexcept {
    if (!packets) return false;
    __try { return *packets > 0; }
    __except (EXCEPTION_EXECUTE_HANDLER) { return false; }
}
void ObserveDescriptions(void* converter, const AppleAsbd* source, const AppleAsbd* destination) noexcept {
    AppleAsbd input{}, output{};
    if (!observing.load(std::memory_order_relaxed) || !converter) return;
    if (!CopyAsbd(source, input)) { evidence.ReportObservationLoss(); return; }
    const bool hasOutput = CopyAsbd(destination, output);
    evidence.ObserveSource(converter, input, hasOutput ? &output : nullptr, GetTickCount64());
}
void ObserveCookie(void* converter, const void* data, std::uint32_t size) noexcept {
    std::uint8_t bytes[48]{};
    std::uint32_t copied{};
    if (!observing.load(std::memory_order_relaxed)) return;
    if (CopyCookie(data, size, bytes, copied)) evidence.ObserveCookie(converter, bytes, copied, GetTickCount64());
    else evidence.ObserveCookie(converter, nullptr, size, GetTickCount64());
}
// Metadata queries run on the same native callback thread after Apple's Fill
// has returned, through the original trampoline. They neither call Fill again
// nor inspect/copy outputData. Bounded refresh also supports an already-created
// decoder; failure leaves metadata unknown/stale rather than guessing a rate.
void RefreshDescriptions(void* converter, bool force = false) noexcept {
    if (!originalGet || !observing.load(std::memory_order_relaxed)) return;
    const auto now = GetTickCount64();
    if (!evidence.TryBeginMetadataRefresh(converter, now, force)) return;
    counters.refreshAttempts.fetch_add(1, std::memory_order_relaxed);
    AppleAsbd source{}, output{};
    std::uint32_t sourceSize = sizeof(source), outputSize = sizeof(output);
    const auto sourceResult = originalGet(converter, kInputDescription, &sourceSize, &source);
    counters.lastMetadataStatus.store(sourceResult, std::memory_order_relaxed);
    counters.hasMetadataStatus.store(true, std::memory_order_release);
    if (sourceResult != 0 || sourceSize != sizeof(source)) {
        counters.refreshFailures.fetch_add(1, std::memory_order_relaxed);
        return;
    }
    const auto outputResult = originalGet(converter, kOutputDescription, &outputSize, &output);
    const bool hasOutput = outputResult == 0 && outputSize == sizeof(output);
    if (!hasOutput) {
        counters.refreshFailures.fetch_add(1, std::memory_order_relaxed);
        counters.lastMetadataStatus.store(outputResult, std::memory_order_relaxed);
    }
    evidence.ObserveSource(converter, source, hasOutput ? &output : nullptr, GetTickCount64());
}
Status __cdecl HookNew(const AppleAsbd* source, const AppleAsbd* output, void** converter) {
    if (observing.load(std::memory_order_relaxed)) counters.newCalls.fetch_add(1, std::memory_order_relaxed);
    const auto result = originalNew(source, output, converter);
    if (result == 0 && converter && observing.load(std::memory_order_relaxed)) {
        evidence.Created(*converter);
        ObserveDescriptions(*converter, source, output);
    }
    return result;
}
Status __cdecl HookSpecific(const AppleAsbd* source, const AppleAsbd* output, std::uint32_t count,
                            const void* descriptions, void** converter) {
    if (observing.load(std::memory_order_relaxed)) counters.specificCalls.fetch_add(1, std::memory_order_relaxed);
    const auto result = originalSpecific(source, output, count, descriptions, converter);
    if (result == 0 && converter && observing.load(std::memory_order_relaxed)) {
        evidence.Created(*converter);
        ObserveDescriptions(*converter, source, output);
    }
    return result;
}
Status __cdecl HookSet(void* converter, std::uint32_t property, std::uint32_t size, const void* data) {
    if (observing.load(std::memory_order_relaxed)) counters.setCalls.fetch_add(1, std::memory_order_relaxed);
    const auto result = originalSet(converter, property, size, data);
    if (result == 0 && observing.load(std::memory_order_relaxed) && property == kMagicCookie) {
        RefreshDescriptions(converter, true);
        ObserveCookie(converter, data, size);
    }
    return result;
}
Status __cdecl HookGet(void* converter, std::uint32_t property, std::uint32_t* size, void* data) {
    if (observing.load(std::memory_order_relaxed)) counters.getCalls.fetch_add(1, std::memory_order_relaxed);
    const auto result = originalGet(converter, property, size, data);
    if (result == 0 && observing.load(std::memory_order_relaxed) && size) {
        if (property == kInputDescription && *size == sizeof(AppleAsbd)) ObserveDescriptions(converter, static_cast<AppleAsbd*>(data), nullptr);
        else if (property == kMagicCookie) ObserveCookie(converter, data, *size);
    }
    return result;
}
Status __cdecl HookFill(void* converter, void* callback, void* user, std::uint32_t* packets, void* data, void* descriptions) {
    if (observing.load(std::memory_order_relaxed)) counters.fillCalls.fetch_add(1, std::memory_order_relaxed);
    const auto result = originalFill(converter, callback, user, packets, data, descriptions);
    if (observing.load(std::memory_order_relaxed)) {
        counters.lastFillStatus.store(result, std::memory_order_relaxed);
        counters.hasFillStatus.store(true, std::memory_order_release);
        if (result != 0) counters.fillErrors.fetch_add(1, std::memory_order_relaxed);
        else if (!ProducedPackets(packets)) counters.emptyFills.fetch_add(1, std::memory_order_relaxed);
        else {
            counters.nonemptyFills.fetch_add(1, std::memory_order_relaxed);
            const auto now = GetTickCount64();
            RefreshDescriptions(converter);
            evidence.Filled(converter, now);
        }
    }
    return result;
}
Status __cdecl HookReset(void* converter) {
    // Fence before the original reset; any later successful Fill must refresh
    // descriptions, so pre-seek/pre-track evidence cannot remain active.
    if (observing.load(std::memory_order_relaxed)) {
        counters.resetCalls.fetch_add(1, std::memory_order_relaxed);
        evidence.Reset(converter);
    }
    return originalReset(converter);
}
Status __cdecl HookDispose(void* converter) {
    if (observing.load(std::memory_order_relaxed)) {
        counters.disposeCalls.fetch_add(1, std::memory_order_relaxed);
        evidence.Retire(converter);
    }
    return originalDispose(converter);
}
bool InstallHooks(HMODULE toolbox) noexcept {
    if (MH_Initialize() != MH_OK) return false;
    struct Hook { const char* name; LPVOID function; LPVOID* original; };
    const Hook hooks[] = {
        {"AudioConverterNew", reinterpret_cast<LPVOID>(&HookNew), reinterpret_cast<LPVOID*>(&originalNew)},
        {"AudioConverterNewSpecific", reinterpret_cast<LPVOID>(&HookSpecific), reinterpret_cast<LPVOID*>(&originalSpecific)},
        {"AudioConverterSetProperty", reinterpret_cast<LPVOID>(&HookSet), reinterpret_cast<LPVOID*>(&originalSet)},
        {"AudioConverterGetProperty", reinterpret_cast<LPVOID>(&HookGet), reinterpret_cast<LPVOID*>(&originalGet)},
        {"AudioConverterFillComplexBuffer", reinterpret_cast<LPVOID>(&HookFill), reinterpret_cast<LPVOID*>(&originalFill)},
        {"AudioConverterReset", reinterpret_cast<LPVOID>(&HookReset), reinterpret_cast<LPVOID*>(&originalReset)},
        {"AudioConverterDispose", reinterpret_cast<LPVOID>(&HookDispose), reinterpret_cast<LPVOID*>(&originalDispose)},
    };
    for (const auto& hook : hooks) {
        const auto target = reinterpret_cast<LPVOID>(GetProcAddress(toolbox, hook.name));
        if (!target || MH_CreateHook(target, hook.function, hook.original) != MH_OK || MH_QueueEnableHook(target) != MH_OK) return false;
    }
    // One coordinated thread suspension, rather than one per export.
    return MH_ApplyQueued() == MH_OK;
}
struct MappingView final {
    ProbeMapping* value{};
    ~MappingView() { if (value) UnmapViewOfFile(value); }
};
bool CaptureSnapshot(ProbeSnapshot& snapshot) noexcept {
    if (!evidence.Capture(GetTickCount64(), snapshot)) return false;
    snapshot.newCalls = counters.newCalls.load(std::memory_order_relaxed);
    snapshot.newSpecificCalls = counters.specificCalls.load(std::memory_order_relaxed);
    snapshot.setPropertyCalls = counters.setCalls.load(std::memory_order_relaxed);
    snapshot.getPropertyCalls = counters.getCalls.load(std::memory_order_relaxed);
    snapshot.fillCalls = counters.fillCalls.load(std::memory_order_relaxed);
    snapshot.fillErrors = counters.fillErrors.load(std::memory_order_relaxed);
    snapshot.emptySuccessfulFills = counters.emptyFills.load(std::memory_order_relaxed);
    snapshot.nonemptySuccessfulFills = counters.nonemptyFills.load(std::memory_order_relaxed);
    snapshot.resetCalls = counters.resetCalls.load(std::memory_order_relaxed);
    snapshot.disposeCalls = counters.disposeCalls.load(std::memory_order_relaxed);
    snapshot.metadataRefreshAttempts = counters.refreshAttempts.load(std::memory_order_relaxed);
    snapshot.metadataRefreshFailures = counters.refreshFailures.load(std::memory_order_relaxed);
    snapshot.hasFillStatus = counters.hasFillStatus.load(std::memory_order_acquire) ? 1 : 0;
    snapshot.lastFillStatus = counters.lastFillStatus.load(std::memory_order_relaxed);
    snapshot.hasMetadataStatus = counters.hasMetadataStatus.load(std::memory_order_acquire) ? 1 : 0;
    snapshot.lastMetadataStatus = counters.lastMetadataStatus.load(std::memory_order_relaxed);
    return true;
}
DWORD WINAPI ObserveWorker(void*) noexcept {
    NativeHandle mapping;
    MappingView view;
    try {
    mapping.value = OpenFileMappingW(FILE_MAP_READ | FILE_MAP_WRITE, FALSE, MappingName(GetCurrentProcessId()).c_str());
    if (!mapping.value) return 1;
    auto* shared = view.value = static_cast<ProbeMapping*>(MapViewOfFile(mapping.value, FILE_MAP_READ | FILE_MAP_WRITE, 0, 0, sizeof(ProbeMapping)));
    if (!shared) return 2;
    Identity self{}, loader{};
    NativeHandle loaderProcess;
    bool valid = shared->magic == kProtocolMagic && shared->version == kProtocolVersion && shared->bytes == sizeof(ProbeMapping) &&
        shared->targetPid == GetCurrentProcessId() && shared->targetStarted == ProcessStarted(GetCurrentProcess()) &&
        shared->stopAtTick > GetTickCount64() && shared->stopAtTick - GetTickCount64() <= 35000;
    if (valid) loaderProcess.value = OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION | SYNCHRONIZE, FALSE, shared->loaderPid);
    valid = valid && loaderProcess.value && ReadIdentity(GetCurrentProcess(), self) && IsAllowedAgent(self) &&
        ReadIdentity(loaderProcess.value, loader) && loader.started == shared->loaderStarted && SameUserSession(self, loader) &&
        SameWindowsPath(loader.path, (std::filesystem::path(ModulePath(ownModule)).parent_path() / kLoaderName).wstring());
    if (!valid) {
        InterlockedExchange(&shared->state, static_cast<LONG>(ProbeState::Rejected));
        return 3;
    }
    HMODULE toolbox{};
    const auto waitUntil = GetTickCount64() + 5000;
    while (!toolbox && GetTickCount64() < waitUntil && GetTickCount64() < shared->stopAtTick &&
        !InterlockedCompareExchange(&shared->stopRequested, 0, 0) && WaitForSingleObject(loaderProcess.value, 0) == WAIT_TIMEOUT) {
        toolbox = GetModuleHandleW(L"CoreAudioToolbox.dll");
        if (!toolbox) Sleep(50);
    }
    const bool expectedToolbox = toolbox && SameWindowsPath(std::filesystem::path(ModulePath(toolbox)).parent_path().wstring(),
        std::filesystem::path(self.path).parent_path().wstring());
    if (GetTickCount64() >= shared->stopAtTick || InterlockedCompareExchange(&shared->stopRequested, 0, 0) ||
        WaitForSingleObject(loaderProcess.value, 0) != WAIT_TIMEOUT) {
        InterlockedExchange(&shared->state, static_cast<LONG>(ProbeState::Stopped));
        return 0;
    }
    if (!expectedToolbox || !InstallHooks(toolbox)) {
        InterlockedExchange(&shared->state, static_cast<LONG>(ProbeState::HookUnavailable));
        return 4;
    }
    observing.store(true, std::memory_order_release);
    InterlockedExchange(&shared->state, static_cast<LONG>(ProbeState::Observing));
    while (GetTickCount64() < shared->stopAtTick && !InterlockedCompareExchange(&shared->stopRequested, 0, 0) &&
        WaitForSingleObject(loaderProcess.value, 0) == WAIT_TIMEOUT) {
        ProbeSnapshot snapshot{};
        if (CaptureSnapshot(snapshot)) {
            InterlockedIncrement(&shared->sequence);
            shared->snapshot = snapshot;
            MemoryBarrier();
            InterlockedIncrement(&shared->sequence);
        }
        Sleep(100);
    }
    observing.store(false, std::memory_order_release);
    // Do not free/remove trampolines while any Apple thread might return through
    // them. These pass-through hooks and this DLL stay resident until the exact
    // AMPLibraryAgent process exits. All telemetry and worker/MMF resources stop.
    InterlockedExchange(&shared->state, static_cast<LONG>(ProbeState::Stopped));
    return 0;
    } catch (...) {
        // Identity/path/setup allocation failures must remain local to this
        // diagnostic worker instead of terminating the host application.
        observing.store(false, std::memory_order_release);
        if (view.value) {
            InterlockedExchange(&view.value->error, ERROR_UNHANDLED_EXCEPTION);
            InterlockedExchange(&view.value->state, static_cast<LONG>(ProbeState::HookUnavailable));
        }
        return 5;
    }
}
}
}
#if defined(DX5_APPLE_PROBE_FIXTURE)
// Built only into the distinct, non-distributed owned fixture DLL. Production
// builds have neither these exports nor an identity-validation bypass.
extern "C" __declspec(dllexport) BOOL Dx5ProbeFixtureStart(HMODULE toolbox) noexcept {
    try {
        if (!dx5::appleprobe::InstallHooks(toolbox)) return FALSE;
        dx5::appleprobe::observing.store(true, std::memory_order_release);
        return TRUE;
    } catch (...) { return FALSE; }
}
extern "C" __declspec(dllexport) BOOL Dx5ProbeFixtureCapture(dx5::appleprobe::ProbeSnapshot* snapshot) noexcept {
    return snapshot && dx5::appleprobe::CaptureSnapshot(*snapshot) ? TRUE : FALSE;
}
extern "C" __declspec(dllexport) void Dx5ProbeFixtureStop() noexcept {
    dx5::appleprobe::observing.store(false, std::memory_order_release);
}
#endif
BOOL WINAPI DllMain(HINSTANCE instance, DWORD reason, LPVOID) {
    if (reason == DLL_PROCESS_ATTACH) {
        dx5::appleprobe::ownModule = instance;
        // This target uses the static CRT; retain its required thread attach /
        // detach notifications instead of calling DisableThreadLibraryCalls.
#if !defined(DX5_APPLE_PROBE_FIXTURE)
        if (const auto worker = CreateThread(nullptr, 0, dx5::appleprobe::ObserveWorker, nullptr, 0, nullptr)) CloseHandle(worker);
#endif
    }
    return TRUE;
}
