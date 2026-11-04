#include "NativeIdentity.h"
#include "SourceFormatEvidence.h"
#include <iostream>
#include <cstdlib>
#include <array>

namespace {
using namespace dx5::appleprobe;
std::uint32_t checks{};
void Check(bool condition, const char* message) {
    ++checks;
    if (!condition) { std::cerr << "FAILED: " << message << '\n'; std::exit(1); }
}
template<class T> T Proc(HMODULE module, const char* name) {
    const auto address = GetProcAddress(module, name);
    Check(address != nullptr, name);
    return reinterpret_cast<T>(address);
}
}
int main() {
    using namespace dx5::appleprobe;
    const auto directory = std::filesystem::path(ModulePath()).parent_path();
    const auto toolbox = LoadLibraryW((directory / L"FixtureCoreAudioToolbox.dll").c_str());
    Check(toolbox != nullptr, "owned fake toolbox loads");
    const auto probe = LoadLibraryW((directory / L"dx5-apple-source-probe-fixture.dll").c_str());
    Check(probe != nullptr, "separate test-only observer loads");
    const auto start = Proc<BOOL(__cdecl*)(HMODULE)>(probe, "Dx5ProbeFixtureStart");
    const auto capture = Proc<BOOL(__cdecl*)(ProbeSnapshot*)>(probe, "Dx5ProbeFixtureCapture");
    const auto stop = Proc<void(__cdecl*)()>(probe, "Dx5ProbeFixtureStop");
    Check(start(toolbox), "actual metadata detours install into owned fixture only");
    const auto create = Proc<std::int32_t(__cdecl*)(const AppleAsbd*, const AppleAsbd*, void**)>(toolbox, "AudioConverterNew");
    const auto specific = Proc<std::int32_t(__cdecl*)(const AppleAsbd*, const AppleAsbd*, std::uint32_t, const void*, void**)>(toolbox, "AudioConverterNewSpecific");
    const auto set = Proc<std::int32_t(__cdecl*)(void*, std::uint32_t, std::uint32_t, const void*)>(toolbox, "AudioConverterSetProperty");
    const auto fill = Proc<std::int32_t(__cdecl*)(void*, void*, void*, std::uint32_t*, void*, void*)>(toolbox, "AudioConverterFillComplexBuffer");
    const auto reset = Proc<std::int32_t(__cdecl*)(void*)>(toolbox, "AudioConverterReset");
    const auto dispose = Proc<std::int32_t(__cdecl*)(void*)>(toolbox, "AudioConverterDispose");
    const auto reject = Proc<void(__cdecl*)(BOOL)>(toolbox, "FixtureRejectDescriptions");
    const auto fillResult = Proc<void(__cdecl*)(std::int32_t)>(toolbox, "FixtureFillResult");
    const auto emptyFill = Proc<void(__cdecl*)(BOOL)>(toolbox, "FixtureEmptyFill");
    const auto failedQueries = Proc<std::uint32_t(__cdecl*)()>(toolbox, "FixtureFailedQueries");
    AppleAsbd source{192000.0, kQlac, 0, 0, 4096, 0, 2, 0, 0};
    AppleAsbd destination{44100.0, kLpcm, 1, 8, 1, 8, 2, 32, 0};
    void* converter{};
    Check(create(&source, &destination, &converter) == 0 && converter, "New arguments and returned handle preserved");
    ProbeSnapshot snapshot{};
    Check(capture(&snapshot) && snapshot.activeConverters == 0, "creation alone is not active source evidence");
    Check(snapshot.newCalls == 1 && snapshot.fillCalls == 0 && !snapshot.hasFillStatus && !snapshot.hasMetadataStatus,
        "raw creation count and absent native result statuses distinguish no Fill call");
    const auto render = [&] {
        std::uint32_t packets = 128;
        const auto result = fill(converter, reinterpret_cast<void*>(0x1234), reinterpret_cast<void*>(0x2345), &packets,
            reinterpret_cast<void*>(0x4567), reinterpret_cast<void*>(0x5678));
        return std::pair{result, packets};
    };
    const auto [result, packets] = render();
    Check(result == 0 && packets == 64, "Fill status and packet count preserved; original opaque pointers untouched");
    Check(capture(&snapshot) && snapshot.reportCount == 1 && snapshot.uniqueSourceRateHz == 192000 &&
        snapshot.reports[0].decodedRateHz == 44100, "encoded source192 remains distinct from decoded44100");
    Check(snapshot.fillCalls == 1 && snapshot.nonemptySuccessfulFills == 1 && snapshot.fillErrors == 0 &&
        snapshot.hasFillStatus && snapshot.lastFillStatus == 0, "nonempty success has distinct raw/native status counters");
    emptyFill(TRUE);
    Check(render().second == 0, "original empty Fill remains empty");
    Check(capture(&snapshot) && snapshot.fillCalls == 2 && snapshot.emptySuccessfulFills == 1 &&
        snapshot.nonemptySuccessfulFills == 1, "empty successful Fill is visible without becoming source activity");
    emptyFill(FALSE);
    std::array<std::uint8_t, 24> cookie{0,0,0x10,0,0,24,40,10,14,2,0,255};
    cookie[20] = 0; cookie[21] = 2; cookie[22] = 0xee; cookie[23] = 0;
    Check(set(converter, 0x646d6763, static_cast<std::uint32_t>(cookie.size()), cookie.data()) == 0, "cookie SetProperty preserves result");
    Check(capture(&snapshot) && snapshot.reports[0].cookieRateHz == 192000, "successful ALAC config reports source192");
    Check(reset(converter) == 0, "reset status preserved");
    Check(capture(&snapshot) && snapshot.activeConverters == 0, "reset fences prior source evidence");
    reject(TRUE);
    const auto queryStart = failedQueries();
    for (int index = 0; index < 20; ++index) Check(render().first == 0, "failed metadata does not fail normal Fill");
    Check(failedQueries() - queryStart <= 1, "failed refresh queries have backoff instead of running per audio Fill");
    Check(capture(&snapshot) && snapshot.uniqueSourceRateHz == 0, "unsupported metadata cannot reuse pre-reset rate");
    Check(snapshot.metadataRefreshAttempts >= 1 && snapshot.metadataRefreshFailures >= 1 &&
        snapshot.hasMetadataStatus && snapshot.lastMetadataStatus == -77, "failed metadata getter's native status is observable");
    reject(FALSE);
    Check(dispose(converter) == 0, "dispose status preserved");
    Check(capture(&snapshot) && snapshot.activeConverters == 0, "dispose retires source lifetime");
    void* selected{};
    Check(specific(&source, &destination, 1, reinterpret_cast<void*>(0x3456), &selected) == 0 && selected,
        "NewSpecific class-description arguments preserved");
    Check(dispose(selected) == 0, "second converter disposed");
    Check(create(&source, &destination, &converter) == 0, "third converter created");
    fillResult(-123);
    Check(render().first == -123, "failed original Fill result is returned unchanged");
    Check(capture(&snapshot) && snapshot.activeConverters == 0, "failed Fill does not promote decoder evidence");
    Check(snapshot.fillErrors == 1 && snapshot.hasFillStatus && snapshot.lastFillStatus == -123,
        "failed original Fill remains observable with its exact Apple OSStatus");
    fillResult(0);
    stop();
    const auto priorRevision = snapshot.revision;
    const auto priorFillCalls = snapshot.fillCalls;
    Check(render().first == 0, "after Stop detours remain harmless passthrough");
    Check(capture(&snapshot) && snapshot.revision == priorRevision, "after Stop metadata observation is disabled");
    Check(snapshot.fillCalls == priorFillCalls, "after Stop raw diagnostic counters also stop");
    std::cout << "PASS: " << checks << " native hook ABI fixture checks; no Apple process or audio client used.\n";
    // Deliberately let the owned fixture process exit with resident pass-through
    // trampolines, exactly matching the production lifetime rule.
    return 0;
}
