#include "SourceFormatEvidence.h"

#include <array>
#include <cstdlib>
#include <iostream>
#include <limits>
#include <string_view>

namespace {
using namespace dx5::appleprobe;

std::size_t checks{};

void Check(bool condition, std::string_view description) {
    ++checks;
    if (!condition) {
        std::cerr << "FAILED: " << description << '\n';
        std::exit(EXIT_FAILURE);
    }
}

void CheckFailure(const SourceEvidence& evidence, ParseStatus status,
                  std::string_view description) {
    Check(evidence.status == status && evidence.rateHz == 0u &&
          evidence.channels == 0u && evidence.bits == 0u && evidence.codec == 0u,
          description);
}

AppleAsbd Source(double rate = 192000.0, std::uint32_t codec = kQlac,
                 std::uint32_t channels = 2u, std::uint32_t bits = 0u) {
    return {rate, codec, 0u, 0u, 4096u, 0u, channels, bits, 0u};
}

template<std::size_t N>
void BigEndian(std::array<std::uint8_t, N>& bytes, std::size_t offset,
               std::uint32_t value) {
    bytes[offset] = static_cast<std::uint8_t>(value >> 24u);
    bytes[offset + 1u] = static_cast<std::uint8_t>(value >> 16u);
    bytes[offset + 2u] = static_cast<std::uint8_t>(value >> 8u);
    bytes[offset + 3u] = static_cast<std::uint8_t>(value);
}

template<std::size_t N>
void Config(std::array<std::uint8_t, N>& bytes, std::size_t offset,
            std::uint32_t rate = 192000u, std::uint8_t channels = 2u,
            std::uint8_t bits = 24u) {
    BigEndian(bytes, offset, 4096u);
    bytes[offset + 4u] = 0u;
    bytes[offset + 5u] = bits;
    bytes[offset + 6u] = 40u;
    bytes[offset + 7u] = 10u;
    bytes[offset + 8u] = 14u;
    bytes[offset + 9u] = channels;
    bytes[offset + 10u] = 0u;
    bytes[offset + 11u] = 255u;
    BigEndian(bytes, offset + 20u, rate);
}

void AsbdCases() {
    const auto source = FromSourceAsbd(Source());
    const auto destination = Source(44100.0, kLpcm, 2u, 32u);
    Check(source == SourceEvidence{ParseStatus::Valid, 192000u, 2u, 0u, kQlac},
          "source 192 kHz remains source evidence when destination is 44.1 kHz");
    Check(FromSourceAsbd(destination) ==
          SourceEvidence{ParseStatus::Unknown, 0u, 0u, 0u, kLpcm},
          "destination PCM is never promoted to encoded-source evidence");
    Check(std::string_view(kSourceEvidenceConfidence) == "source stream",
          "confidence never claims a verified current track");

    for (const auto codec : {kAlac, kQlac}) {
        for (const auto rate : {8000u, 44100u, 48000u, 96000u, 192000u, 768000u}) {
            const auto result = FromSourceAsbd(Source(rate, codec));
            Check(result.status == ParseStatus::Valid && result.rateHz == rate &&
                  result.codec == codec, "integer boundary and common source rates accepted");
        }
    }
    for (const auto bits : {16u, 20u, 24u, 32u}) {
        Check(FromSourceAsbd(Source(96000.0, kAlac, 8u, bits)).bits == bits,
              "supported ALAC source precision retained");
    }
    for (const auto rate : {std::numeric_limits<double>::quiet_NaN(),
                            std::numeric_limits<double>::infinity(),
                            -std::numeric_limits<double>::infinity(),
                            -1.0, 0.0, 7999.0, 768001.0, 44100.5, 96000.00001}) {
        CheckFailure(FromSourceAsbd(Source(rate)), ParseStatus::Invalid,
                     "non-finite, fractional and out-of-range source rates rejected");
    }
    for (const auto channels : {0u, 9u, std::numeric_limits<std::uint32_t>::max()}) {
        CheckFailure(FromSourceAsbd(Source(96000.0, kQlac, channels)), ParseStatus::Invalid,
                     "invalid source channels rejected");
    }
    for (const auto bits : {1u, 8u, 17u, 33u}) {
        CheckFailure(FromSourceAsbd(Source(96000.0, kAlac, 2u, bits)), ParseStatus::Invalid,
                     "unsupported source precision rejected");
    }
    auto reserved = Source();
    reserved.reserved = 1u;
    CheckFailure(FromSourceAsbd(reserved), ParseStatus::Invalid,
                 "nonzero reserved ASBD field rejected");
    Check(FromSourceAsbd(Source(192000.0, 0x71616163u)) ==
          SourceEvidence{ParseStatus::Unknown, 0u, 0u, 0u, 0x71616163u},
          "protected AAC is not mislabeled as ALAC source evidence");
}

void CookieCases() {
    for (const auto offset : {0u, 12u, 24u}) {
        std::array<std::uint8_t, 48> bytes{};
        Config(bytes, offset);
        const auto result = ParseAlacCookie(bytes.data(), offset + 24u);
        Check(result == SourceEvidence{ParseStatus::Valid, 192000u, 2u, 24u, kAlac},
              "ALAC config parsed at each allowed complete offset");
        const auto protectedResult = ParseAlacCookie(bytes.data(), offset + 24u,
                                                     FromSourceAsbd(Source()));
        Check(protectedResult.codec == kQlac && protectedResult.rateHz == 192000u,
              "protected source codec retained after cookie corroboration");
        CheckFailure(ParseAlacCookie(bytes.data(), offset + 23u), ParseStatus::Invalid,
                     "truncation at every config offset rejected");
    }
    for (const auto bits : {16u, 20u, 24u, 32u}) {
        std::array<std::uint8_t, 24> bytes{};
        Config(bytes, 0u, 96000u, 1u, static_cast<std::uint8_t>(bits));
        Check(ParseAlacCookie(bytes.data(), bytes.size()).bits == bits,
              "ALAC config precision includes 20-bit source samples");
    }

    std::array<std::uint8_t, 24> valid{};
    Config(valid, 0u);
    CheckFailure(ParseAlacCookie(nullptr, 0u), ParseStatus::Unknown,
                 "absent cookie has no evidence");
    CheckFailure(ParseAlacCookie(nullptr, valid.size()), ParseStatus::Invalid,
                 "nonempty cookie requires a buffer");
    CheckFailure(ParseAlacCookie(valid.data(), 0u), ParseStatus::Invalid,
                 "buffer with no cookie bytes rejected");
    for (std::size_t length = 1u; length < valid.size(); ++length) {
        CheckFailure(ParseAlacCookie(valid.data(), length), ParseStatus::Invalid,
                     "all short bare-config lengths rejected");
    }

    for (const auto rate : {0u, 7999u, 768001u, 0xffffffffu}) {
        auto bytes = valid;
        BigEndian(bytes, 20u, rate);
        CheckFailure(ParseAlacCookie(bytes.data(), bytes.size()), ParseStatus::Invalid,
                     "malformed ALAC sample rate rejected");
    }
    for (const auto channels : {0u, 9u, 255u}) {
        auto bytes = valid;
        bytes[9] = static_cast<std::uint8_t>(channels);
        CheckFailure(ParseAlacCookie(bytes.data(), bytes.size()), ParseStatus::Invalid,
                     "malformed ALAC channel count rejected");
    }
    for (const auto bits : {0u, 8u, 17u, 255u}) {
        auto bytes = valid;
        bytes[5] = static_cast<std::uint8_t>(bits);
        CheckFailure(ParseAlacCookie(bytes.data(), bytes.size()), ParseStatus::Invalid,
                     "malformed ALAC precision rejected");
    }
    auto version = valid;
    version[4] = 1u;
    CheckFailure(ParseAlacCookie(version.data(), version.size()), ParseStatus::Invalid,
                 "future unsupported ALAC version rejected");
    auto frameLength = valid;
    BigEndian(frameLength, 0u, 0u);
    CheckFailure(ParseAlacCookie(frameLength.data(), frameLength.size()), ParseStatus::Invalid,
                 "zero ALAC frame length rejected");

    const auto source = FromSourceAsbd(Source());
    Check(ParseAlacCookie(valid.data(), valid.size(), source).bits == 24u,
          "cookie can fill unknown precision without changing source rate");
    for (const auto mismatchingSource : {FromSourceAsbd(Source(96000.0)),
                                         FromSourceAsbd(Source(192000.0, kQlac, 1u)),
                                         FromSourceAsbd(Source(192000.0, kQlac, 2u, 16u))}) {
        CheckFailure(ParseAlacCookie(valid.data(), valid.size(), mismatchingSource),
                     ParseStatus::Conflict, "cookie must agree with source rate channels and known bits");
    }
    CheckFailure(ParseAlacCookie(valid.data(), valid.size(),
                                SourceEvidence{ParseStatus::Unknown, 44100u, 2u, 32u, kLpcm}),
                 ParseStatus::Unknown, "cookie cannot upgrade a known unrelated codec");
    CheckFailure(ParseAlacCookie(valid.data(), valid.size(), FromSourceAsbd(Source(44100.0, kLpcm))),
                 ParseStatus::Unknown, "PCM source ASBD itself blocks unrelated ALAC cookie attribution");
    CheckFailure(ParseAlacCookie(valid.data(), valid.size(),
                                SourceEvidence{ParseStatus::Invalid}),
                 ParseStatus::Invalid, "cookie cannot rehabilitate invalid source descriptor");
    CheckFailure(ParseAlacCookie(valid.data(), valid.size(),
                                SourceEvidence{ParseStatus::Conflict}),
                 ParseStatus::Conflict, "source conflict remains a conflict");
    CheckFailure(ParseAlacCookie(valid.data(), valid.size(),
                                SourceEvidence{ParseStatus::Valid, 0u, 2u, 24u, kAlac}),
                 ParseStatus::Invalid, "forged valid source evidence is revalidated");

    std::array<std::uint8_t, 48> duplicate{};
    Config(duplicate, 0u);
    Config(duplicate, 24u);
    Check(ParseAlacCookie(duplicate.data(), duplicate.size()).rateHz == 192000u,
          "identical complete candidates agree");
    for (const auto conflictKind : {0u, 1u, 2u}) {
        auto bytes = duplicate;
        Config(bytes, 24u, conflictKind == 0u ? 96000u : 192000u,
               conflictKind == 1u ? 1u : 2u, conflictKind == 2u ? 16u : 24u);
        CheckFailure(ParseAlacCookie(bytes.data(), bytes.size()), ParseStatus::Conflict,
                     "different complete candidates rejected instead of selecting the first");
    }

    // Overlapping candidate configs (offsets 0 and 12) can both have sane
    // tuples. MaxFrameBytes/avgBitRate are irrelevant to rate evidence, so this
    // adversarial layout must still be compared rather than stopping early.
    std::array<std::uint8_t, 36> overlapping{};
    Config(overlapping, 0u);
    Config(overlapping, 12u, 96000u, 2u, 24u);
    // Config at12 overwrote the first rate's middle bytes. Restore a valid
    // first rate that also gives the second config a valid channel count.
    BigEndian(overlapping, 20u, 0x0002ee00u); // 192000; byte21=2 channels at12+9.
    CheckFailure(ParseAlacCookie(overlapping.data(), overlapping.size()), ParseStatus::Conflict,
                 "conflicting overlapping configs rejected");

    std::array<std::uint8_t, 64> unsupportedOffset{};
    Config(unsupportedOffset, 36u, 48000u);
    CheckFailure(ParseAlacCookie(unsupportedOffset.data(), unsupportedOffset.size()),
                 ParseStatus::Invalid, "arbitrary scanning beyond known offsets is forbidden");
}

} // namespace

int main() {
    AsbdCases();
    CookieCases();
    std::cout << "SourceFormatEvidence: " << checks << " checks passed\n";
    return EXIT_SUCCESS;
}
