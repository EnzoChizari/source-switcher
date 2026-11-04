#pragma once

// The Apple ASBD layout and ALAC cookie offsets are derived from AMExclusive,
// commit c67c2f3799350ea0394d8522b3422660897a55d5, mod/src/HookDll.cpp and
// mod/src/ApplePrivateOffsets.h. Copyright (c) 2026 AMExclusive contributors.
// Used under the MIT License; see this project's third-party license notice.
// This parser adds strict rate validation, source consistency and conflict
// rejection. It observes metadata only; it neither decodes nor routes audio.

#include <array>
#include <cmath>
#include <cstddef>
#include <cstdint>
#include <type_traits>

namespace dx5::appleprobe {

inline constexpr std::uint32_t kAlac = 0x616c6163u; // 'alac'
inline constexpr std::uint32_t kQlac = 0x716c6163u; // protected 'qlac'
inline constexpr std::uint32_t kLpcm = 0x6c70636du; // 'lpcm'
inline constexpr const char* kSourceEvidenceConfidence = "source stream";

// A copy of an observed SOURCE descriptor, never the converter destination.
// A valid source descriptor does not establish that its converter belongs to
// the current track: converter lifetime/activity must be established separately.
struct AppleAsbd final {
    double sampleRate{};
    std::uint32_t formatId{};
    std::uint32_t flags{};
    std::uint32_t bytesPerPacket{};
    std::uint32_t framesPerPacket{};
    std::uint32_t bytesPerFrame{};
    std::uint32_t channels{};
    std::uint32_t bits{};
    std::uint32_t reserved{};
};

static_assert(sizeof(double) == 8);
static_assert(sizeof(AppleAsbd) == 40);
static_assert(offsetof(AppleAsbd, sampleRate) == 0);
static_assert(offsetof(AppleAsbd, formatId) == 8);
static_assert(offsetof(AppleAsbd, channels) == 28);
static_assert(offsetof(AppleAsbd, bits) == 32);
static_assert(offsetof(AppleAsbd, reserved) == 36);
static_assert(std::is_standard_layout_v<AppleAsbd>);
static_assert(std::is_trivially_copyable_v<AppleAsbd>);

enum class ParseStatus : std::uint8_t { Unknown, Valid, Invalid, Conflict };

struct SourceEvidence final {
    ParseStatus status{ParseStatus::Unknown};
    std::uint32_t rateHz{};
    std::uint32_t channels{};
    std::uint32_t bits{}; // Zero means the source descriptor did not provide it.
    std::uint32_t codec{};

    constexpr bool operator==(const SourceEvidence&) const noexcept = default;
};

namespace detail {

inline constexpr bool IsLosslessCodec(std::uint32_t codec) noexcept {
    return codec == kAlac || codec == kQlac;
}

inline constexpr bool IsRate(std::uint32_t rate) noexcept {
    return rate >= 8000u && rate <= 768000u;
}

inline constexpr bool IsChannels(std::uint32_t channels) noexcept {
    return channels >= 1u && channels <= 8u;
}

inline constexpr bool IsBits(std::uint32_t bits) noexcept {
    return bits == 16u || bits == 20u || bits == 24u || bits == 32u;
}

inline constexpr SourceEvidence Failed(ParseStatus status) noexcept {
    // Never return a plausible rate alongside failed or conflicting evidence.
    return {status, 0u, 0u, 0u, 0u};
}

inline constexpr std::uint32_t BigEndian32(const std::uint8_t* bytes) noexcept {
    return (static_cast<std::uint32_t>(bytes[0]) << 24u) |
           (static_cast<std::uint32_t>(bytes[1]) << 16u) |
           (static_cast<std::uint32_t>(bytes[2]) << 8u) |
           static_cast<std::uint32_t>(bytes[3]);
}

inline SourceEvidence CookieCandidate(const std::uint8_t* config) noexcept {
    // ALACSpecificConfig is a 24-byte big-endian structure. Version zero is
    // the supported ALAC version; zero frames is not a complete configuration.
    if (BigEndian32(config) == 0u || config[4] != 0u ||
        !IsBits(config[5]) || !IsChannels(config[9])) {
        return Failed(ParseStatus::Invalid);
    }
    const auto rate = BigEndian32(config + 20u);
    if (!IsRate(rate)) return Failed(ParseStatus::Invalid);
    return {ParseStatus::Valid, rate, config[9], config[5], kAlac};
}

} // namespace detail

inline SourceEvidence FromSourceAsbd(const AppleAsbd& source) noexcept {
    // PCM may already have been resampled by Apple. It must not be promoted to
    // encoded-source/native evidence merely because it has a valid rate.
    if (!detail::IsLosslessCodec(source.formatId)) {
        // Preserve only the codec so a later cookie cannot accidentally turn
        // a known PCM/AAC converter into an ALAC source association.
        return {ParseStatus::Unknown, 0u, 0u, 0u, source.formatId};
    }
    if (!std::isfinite(source.sampleRate) || source.sampleRate < 8000.0 ||
        source.sampleRate > 768000.0 ||
        std::trunc(source.sampleRate) != source.sampleRate ||
        !detail::IsChannels(source.channels) ||
        (source.bits != 0u && !detail::IsBits(source.bits)) ||
        source.reserved != 0u) {
        return detail::Failed(ParseStatus::Invalid);
    }
    return {ParseStatus::Valid, static_cast<std::uint32_t>(source.sampleRate),
            source.channels, source.bits, source.formatId};
}

inline SourceEvidence ParseAlacCookie(const void* data, std::size_t size,
                                     const SourceEvidence& source = {}) noexcept {
    if (source.status == ParseStatus::Conflict || source.status == ParseStatus::Invalid) {
        return detail::Failed(source.status);
    }
    if (source.codec != 0u && !detail::IsLosslessCodec(source.codec)) {
        return {}; // A cookie must not be attributed to a different source codec.
    }
    if (source.status == ParseStatus::Valid &&
        (!detail::IsLosslessCodec(source.codec) || !detail::IsRate(source.rateHz) ||
         !detail::IsChannels(source.channels) ||
         (source.bits != 0u && !detail::IsBits(source.bits)))) {
        return detail::Failed(ParseStatus::Invalid);
    }
    if (size == 0u && data == nullptr) return {};
    if (data == nullptr || size < 24u) return detail::Failed(ParseStatus::Invalid);

    const auto* bytes = static_cast<const std::uint8_t*>(data);
    SourceEvidence candidate{};
    // Bare config, one 12-byte atom prefix, or two prefixes. Examine every
    // complete position: accepting the first valid tuple would hide conflicts.
    // Invalid positions are ordinary wrapper bytes, not alternate evidence.
    constexpr std::array<std::size_t, 3> offsets{0u, 12u, 24u};
    for (const auto offset : offsets) {
        if (size < offset + 24u) continue;
        const auto parsed = detail::CookieCandidate(bytes + offset);
        if (parsed.status != ParseStatus::Valid) continue;
        if (candidate.status == ParseStatus::Valid &&
            (candidate.rateHz != parsed.rateHz || candidate.channels != parsed.channels ||
             candidate.bits != parsed.bits)) {
            return detail::Failed(ParseStatus::Conflict);
        }
        candidate = parsed;
    }
    if (candidate.status != ParseStatus::Valid) return detail::Failed(ParseStatus::Invalid);
    if (source.status == ParseStatus::Valid &&
        (candidate.rateHz != source.rateHz || candidate.channels != source.channels ||
         (source.bits != 0u && candidate.bits != source.bits))) {
        return detail::Failed(ParseStatus::Conflict);
    }
    if (detail::IsLosslessCodec(source.codec)) candidate.codec = source.codec;
    return candidate;
}

} // namespace dx5::appleprobe
