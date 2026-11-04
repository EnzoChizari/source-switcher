#pragma once
#include <windows.h>
#include <cstdint>
#include <type_traits>

namespace dx5::appleprobe {
inline constexpr std::uint32_t kProtocolMagic = 0x35584150;
inline constexpr std::uint32_t kProtocolVersion = 2;
inline constexpr std::uint32_t kMaximumReports = 16;
inline constexpr std::uint64_t kEvidenceTtlMs = 2000;
inline constexpr wchar_t kLoaderName[] = L"dx5-apple-source-probe.exe";
inline constexpr wchar_t kDllName[] = L"dx5-apple-source-probe.dll";
inline constexpr wchar_t kPackageName[] = L"AppleInc.AppleMusicWin_1.1540.23042.0_x64__nzyj5cx40ttqa";
enum class ProbeState : LONG { Waiting, Observing, Stopped, Rejected, HookUnavailable };
struct ConverterReport {
    std::uint64_t generation{};
    std::uint64_t successfulFills{};
    std::uint64_t lastFillAgeMs{};
    std::uint32_t status{};
    std::uint32_t sourceRateHz{};
    std::uint32_t sourceChannels{};
    std::uint32_t sourceBits{};
    std::uint32_t sourceCodec{};
    std::uint32_t decodedRateHz{};
    std::uint32_t cookieRateHz{};
    std::uint32_t cookieBits{};
};
struct ProbeSnapshot {
    std::uint64_t observedTick{};
    std::uint64_t revision{};
    std::uint64_t droppedEvents{};
    std::uint64_t capacityDrops{};
    std::uint64_t newCalls{}, newSpecificCalls{}, setPropertyCalls{}, getPropertyCalls{};
    std::uint64_t fillCalls{}, fillErrors{}, emptySuccessfulFills{}, nonemptySuccessfulFills{};
    std::uint64_t resetCalls{}, disposeCalls{}, metadataRefreshAttempts{}, metadataRefreshFailures{};
    std::int32_t lastFillStatus{}, lastMetadataStatus{};
    std::uint32_t hasFillStatus{}, hasMetadataStatus{};
    std::uint32_t activeConverters{};
    std::uint32_t reportCount{};
    std::uint32_t uniqueSourceRateHz{};
    std::uint32_t conflicting{};
    ConverterReport reports[kMaximumReports]{};
};
struct ProbeMapping {
    std::uint32_t magic{kProtocolMagic};
    std::uint32_t version{kProtocolVersion};
    std::uint32_t bytes{sizeof(ProbeMapping)};
    std::uint32_t targetPid{};
    std::uint64_t targetStarted{};
    std::uint32_t loaderPid{};
    std::uint32_t reserved{};
    std::uint64_t loaderStarted{};
    std::uint64_t stopAtTick{};
    std::uint8_t nonce[32]{};
    volatile LONG stopRequested{};
    volatile LONG state{static_cast<LONG>(ProbeState::Waiting)};
    volatile LONG error{};
    volatile LONG sequence{};
    ProbeSnapshot snapshot{};
};
static_assert(std::is_trivially_copyable_v<ProbeSnapshot>);
static_assert(sizeof(ProbeMapping) < 4096);
inline std::uint64_t FileTimeValue(FILETIME value) noexcept {
    return (static_cast<std::uint64_t>(value.dwHighDateTime) << 32) | value.dwLowDateTime;
}
inline std::uint64_t ProcessStarted(HANDLE process) noexcept {
    FILETIME created{}, exited{}, kernel{}, user{};
    return GetProcessTimes(process, &created, &exited, &kernel, &user) ? FileTimeValue(created) : 0;
}
}
