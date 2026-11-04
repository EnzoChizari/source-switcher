// Owned test double for the Apple ABI. No audio client, media decoding, Apple
// package or external process is opened by this module.
#include "SourceFormatEvidence.h"
#include <windows.h>
#include <array>
#include <cstdint>
#include <cstring>

using dx5::appleprobe::AppleAsbd;
namespace {
struct FakeConverter { AppleAsbd source{}, destination{}; bool live{}; };
std::array<FakeConverter, 16> converters{};
std::uint32_t nextConverter{}, getCalls{}, fillCalls{}, failedInputQueries{};
bool rejectDescriptions{};
bool emptyFill{};
std::int32_t fillResult{};
}
#define FIXTURE_EXPORT extern "C" __declspec(dllexport)
FIXTURE_EXPORT void __cdecl FixtureRejectDescriptions(BOOL value) { rejectDescriptions = value != FALSE; }
FIXTURE_EXPORT void __cdecl FixtureFillResult(std::int32_t value) { fillResult = value; }
FIXTURE_EXPORT void __cdecl FixtureEmptyFill(BOOL value) { emptyFill = value != FALSE; }
FIXTURE_EXPORT std::uint32_t __cdecl FixtureGetCalls() { return getCalls; }
FIXTURE_EXPORT std::uint32_t __cdecl FixtureFailedQueries() { return failedInputQueries; }
FIXTURE_EXPORT std::uint32_t __cdecl FixtureFillCalls() { return fillCalls; }
FIXTURE_EXPORT std::int32_t __cdecl AudioConverterNew(const AppleAsbd* source, const AppleAsbd* destination, void** result) {
    if (!source || !destination || !result || nextConverter == converters.size()) return -11;
    auto& converter = converters[nextConverter++];
    converter = {*source, *destination, true};
    *result = &converter;
    return 0;
}
FIXTURE_EXPORT std::int32_t __cdecl AudioConverterNewSpecific(const AppleAsbd* source, const AppleAsbd* destination,
    std::uint32_t count, const void* descriptions, void** result) {
    if (count != 1 || descriptions != reinterpret_cast<void*>(0x3456)) return -12;
    return AudioConverterNew(source, destination, result);
}
FIXTURE_EXPORT std::int32_t __cdecl AudioConverterSetProperty(void* converter, std::uint32_t property,
    std::uint32_t bytes, const void* data) {
    if (!converter || !static_cast<FakeConverter*>(converter)->live) return -13;
    if (property == 0x646d6763 && (!data || bytes < 24)) return -14;
    return 0;
}
FIXTURE_EXPORT std::int32_t __cdecl AudioConverterGetProperty(void* converter, std::uint32_t property,
    std::uint32_t* bytes, void* data) {
    ++getCalls;
    if (!converter || !static_cast<FakeConverter*>(converter)->live || !bytes || !data) return -15;
    if (rejectDescriptions) { if (property == 0x61636964) ++failedInputQueries; return -77; }
    if (*bytes != sizeof(AppleAsbd)) return -16;
    const auto& item = *static_cast<FakeConverter*>(converter);
    if (property == 0x61636964) std::memcpy(data, &item.source, sizeof(AppleAsbd));
    else if (property == 0x61636f64) std::memcpy(data, &item.destination, sizeof(AppleAsbd));
    else return -17;
    return 0;
}
FIXTURE_EXPORT std::int32_t __cdecl AudioConverterFillComplexBuffer(void* converter, void* callback,
    void* context, std::uint32_t* packets, void* pcm, void* descriptions) {
    ++fillCalls;
    // Invalid addresses intentionally prove the observer does not read PCM or
    // rewrite/replace the original callback, context or packet-description args.
    if (callback != reinterpret_cast<void*>(0x1234) || context != reinterpret_cast<void*>(0x2345) ||
        pcm != reinterpret_cast<void*>(0x4567) || descriptions != reinterpret_cast<void*>(0x5678)) return -18;
    if (!converter || !static_cast<FakeConverter*>(converter)->live || !packets) return -19;
    *packets = fillResult == 0 && !emptyFill ? 64 : 0;
    return fillResult;
}
FIXTURE_EXPORT std::int32_t __cdecl AudioConverterReset(void* converter) {
    return converter && static_cast<FakeConverter*>(converter)->live ? 0 : -20;
}
FIXTURE_EXPORT std::int32_t __cdecl AudioConverterDispose(void* converter) {
    if (!converter || !static_cast<FakeConverter*>(converter)->live) return -21;
    static_cast<FakeConverter*>(converter)->live = false;
    return 0;
}
