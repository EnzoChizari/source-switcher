#include "ConverterEvidenceTable.h"

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

void* Key(std::uintptr_t value = 1u) {
    return reinterpret_cast<void*>(value);
}

AppleAsbd Source(double rate = 192000.0) {
    return {rate, kQlac, 0u, 0u, 4096u, 0u, 2u, 0u, 0u};
}

AppleAsbd Destination(double rate = 44100.0) {
    return {rate, kLpcm, 1u, 8u, 1u, 8u, 2u, 32u, 0u};
}

std::array<std::uint8_t, 24> Cookie(std::uint32_t rate = 192000u) {
    std::array<std::uint8_t, 24> bytes{
        0u, 0u, 0x10u, 0u, 0u, 24u, 40u, 10u, 14u, 2u, 0u, 255u};
    bytes[20] = static_cast<std::uint8_t>(rate >> 24u);
    bytes[21] = static_cast<std::uint8_t>(rate >> 16u);
    bytes[22] = static_cast<std::uint8_t>(rate >> 8u);
    bytes[23] = static_cast<std::uint8_t>(rate);
    return bytes;
}

ProbeSnapshot Capture(ConverterEvidenceTable& table, std::uint64_t now) {
    ProbeSnapshot snapshot{};
    Check(table.Capture(now, snapshot), "uncontended snapshot captured");
    return snapshot;
}

void Observe(ConverterEvidenceTable& table, void* key, double rate,
             std::uint64_t now = 100u) {
    const auto destination = Destination();
    table.ObserveSource(key, Source(rate), &destination, now);
    table.Filled(key, now);
}

void SourceAndActivity() {
    ConverterEvidenceTable table;
    const auto destination = Destination();
    table.ObserveSource(Key(), Source(), &destination, 100u);
    auto snapshot = Capture(table, 101u);
    Check(snapshot.activeConverters == 0u && snapshot.uniqueSourceRateHz == 0u,
          "source metadata without successful fill is not active playback");
    Check(!table.NeedsMetadata(Key(), 101u), "fresh source metadata does not need refresh");
    table.Filled(Key(), 101u);
    snapshot = Capture(table, 102u);
    Check(snapshot.activeConverters == 1u && snapshot.reportCount == 1u &&
          snapshot.uniqueSourceRateHz == 192000u && !snapshot.conflicting,
          "successful source fill creates a single source-stream candidate");
    Check(snapshot.reports[0].sourceRateHz == 192000u &&
          snapshot.reports[0].decodedRateHz == 44100u &&
          snapshot.reports[0].sourceCodec == kQlac &&
          snapshot.reports[0].successfulFills == 1u,
          "source192kHz and decoded44100Hz remain distinct");

    ConverterEvidenceTable outputOnly;
    outputOnly.ObserveSource(Key(), destination, &destination, 100u);
    outputOnly.Filled(Key(), 100u);
    snapshot = Capture(outputOnly, 101u);
    Check(snapshot.activeConverters == 1u && snapshot.uniqueSourceRateHz == 0u &&
          snapshot.reports[0].sourceRateHz == 0u && snapshot.reports[0].decodedRateHz == 44100u,
          "output PCM cannot become native source rate");

    ConverterEvidenceTable cookieOnly;
    const auto cookie = Cookie();
    cookieOnly.ObserveCookie(Key(), cookie.data(), static_cast<std::uint32_t>(cookie.size()), 100u);
    cookieOnly.Filled(Key(), 100u);
    snapshot = Capture(cookieOnly, 101u);
    Check(snapshot.activeConverters == 1u && snapshot.uniqueSourceRateHz == 0u &&
          snapshot.reports[0].sourceRateHz == 0u && snapshot.reports[0].cookieRateHz == 192000u,
          "cookie-only observation needs a matching source ASBD before promotion");

    // A converter with no source/cookie metadata can still be observed filling,
    // but its activity does not manufacture a source rate.
    ConverterEvidenceTable fillOnly;
    fillOnly.Filled(Key(), 100u);
    snapshot = Capture(fillOnly, 101u);
    Check(snapshot.activeConverters == 1u && snapshot.uniqueSourceRateHz == 0u,
          "fill-only converter has unknown source rate");
    Check(fillOnly.NeedsMetadata(Key(), 101u), "fill-only converter requests source metadata");
}

void FreshnessAndClock() {
    ConverterEvidenceTable table;
    Observe(table, Key(), 192000.0);
    auto snapshot = Capture(table, 100u + kEvidenceTtlMs);
    Check(snapshot.activeConverters == 1u && snapshot.uniqueSourceRateHz == 192000u,
          "activity is retained through the documented TTL boundary");
    Check(table.NeedsMetadata(Key(), 100u + kEvidenceTtlMs),
          "source refresh is requested at the TTL boundary");
    snapshot = Capture(table, 101u + kEvidenceTtlMs);
    Check(snapshot.activeConverters == 0u && snapshot.uniqueSourceRateHz == 0u,
          "expired fill is excluded without a fabricated rate");

    table.Filled(Key(), 102u + kEvidenceTtlMs);
    snapshot = Capture(table, 103u + kEvidenceTtlMs);
    Check(snapshot.uniqueSourceRateHz == 0u,
          "fresh fills cannot keep expired source metadata authoritative");
    Check(table.NeedsMetadata(Key(), 103u + kEvidenceTtlMs),
          "failed or missing metadata refresh remains retryable");
    const auto destination = Destination();
    table.ObserveSource(Key(), Source(), &destination, 104u + kEvidenceTtlMs);
    snapshot = Capture(table, 105u + kEvidenceTtlMs);
    Check(snapshot.uniqueSourceRateHz == 192000u, "matching fresh metadata restores current source candidate");

    snapshot = Capture(table, 99u);
    Check(snapshot.uniqueSourceRateHz == 0u && snapshot.activeConverters == 0u,
          "clock regression cannot produce fresh activity");
    Check(table.NeedsMetadata(Key(), 99u), "metadata clock regression requests a new observation");
}

void AmbiguityAndCookieValidation() {
    ConverterEvidenceTable table;
    Observe(table, Key(1u), 192000.0);
    Observe(table, Key(2u), 96000.0);
    auto snapshot = Capture(table, 101u);
    Check(snapshot.activeConverters == 2u && snapshot.reportCount == 2u &&
          snapshot.conflicting && snapshot.uniqueSourceRateHz == 0u,
          "simultaneous different source rates are explicitly ambiguous");

    ConverterEvidenceTable sameRates;
    Observe(sameRates, Key(1u), 96000.0);
    Observe(sameRates, Key(2u), 96000.0);
    snapshot = Capture(sameRates, 101u);
    Check(snapshot.activeConverters == 2u && !snapshot.conflicting && snapshot.uniqueSourceRateHz == 96000u,
          "same-rate active converters agree on stream rate without claiming track identity");

    ConverterEvidenceTable cookieMismatch;
    Observe(cookieMismatch, Key(), 192000.0);
    auto cookie = Cookie(96000u);
    cookieMismatch.ObserveCookie(Key(), cookie.data(), static_cast<std::uint32_t>(cookie.size()), 101u);
    snapshot = Capture(cookieMismatch, 102u);
    Check(snapshot.conflicting && snapshot.uniqueSourceRateHz == 0u &&
          snapshot.reports[0].status == static_cast<std::uint32_t>(ParseStatus::Conflict),
          "cookie and source disagreement veto unique source rate");

    ConverterEvidenceTable malformed;
    Observe(malformed, Key(), 192000.0);
    cookie = Cookie();
    cookie[5] = 8u;
    malformed.ObserveCookie(Key(), cookie.data(), static_cast<std::uint32_t>(cookie.size()), 101u);
    snapshot = Capture(malformed, 102u);
    Check(snapshot.conflicting && snapshot.uniqueSourceRateHz == 0u &&
          snapshot.reports[0].status == static_cast<std::uint32_t>(ParseStatus::Invalid),
          "malformed cookie suppresses source success");

    ConverterEvidenceTable shortened;
    Observe(shortened, Key(), 192000.0);
    cookie = Cookie();
    shortened.ObserveCookie(Key(), cookie.data(), static_cast<std::uint32_t>(cookie.size()), 100u);
    shortened.ObserveCookie(Key(), cookie.data(), 23u, 101u);
    snapshot = Capture(shortened, 102u);
    Check(snapshot.conflicting && snapshot.uniqueSourceRateHz == 0u,
          "short cookie replacement cannot silently retain earlier valid cookie");

    ConverterEvidenceTable conflictingConfigs;
    Observe(conflictingConfigs, Key(), 192000.0);
    std::array<std::uint8_t, 48> twoCookies{};
    const auto first = Cookie();
    const auto second = Cookie(96000u);
    for (std::size_t index = 0; index < first.size(); ++index) {
        twoCookies[index] = first[index];
        twoCookies[index + 24u] = second[index];
    }
    conflictingConfigs.ObserveCookie(Key(), twoCookies.data(), static_cast<std::uint32_t>(twoCookies.size()), 101u);
    snapshot = Capture(conflictingConfigs, 102u);
    Check(snapshot.conflicting && snapshot.uniqueSourceRateHz == 0u,
          "table preserves parser conflict across multiple config offsets");
}

void LifetimesAndPointerReuse() {
    ConverterEvidenceTable table;
    Observe(table, Key(), 192000.0);
    auto snapshot = Capture(table, 101u);
    const auto originalGeneration = snapshot.reports[0].generation;
    Check(originalGeneration != 0u, "converter generation is explicit");
    table.Reset(Key());
    snapshot = Capture(table, 102u);
    Check(snapshot.activeConverters == 0u && snapshot.uniqueSourceRateHz == 0u,
          "reset clears activity and source metadata");
    Check(table.NeedsMetadata(Key(), 102u), "reset requests new source metadata");
    Observe(table, Key(), 96000.0, 103u);
    snapshot = Capture(table, 104u);
    Check(snapshot.reports[0].generation > originalGeneration &&
          snapshot.reports[0].successfulFills == 1u && snapshot.uniqueSourceRateHz == 96000u,
          "reset starts a new generation without stale fills");
    const auto resetGeneration = snapshot.reports[0].generation;
    table.Retire(Key());
    snapshot = Capture(table, 105u);
    Check(snapshot.activeConverters == 0u, "disposed converter removed from reports");
    Observe(table, Key(), 48000.0, 106u);
    snapshot = Capture(table, 107u);
    Check(snapshot.reports[0].generation > resetGeneration &&
          snapshot.reports[0].successfulFills == 1u && snapshot.uniqueSourceRateHz == 48000u,
          "retired pointer reused for a fresh converter cannot inherit evidence");

    const auto destination = Destination();
    const auto priorGeneration = snapshot.reports[0].generation;
    table.ObserveSource(Key(), Source(192000.0), &destination, 108u);
    snapshot = Capture(table, 109u);
    Check(snapshot.activeConverters == 0u && snapshot.uniqueSourceRateHz == 0u,
          "same pointer source change invalidates all prior activity");
    table.Filled(Key(), 110u);
    snapshot = Capture(table, 111u);
    Check(snapshot.reports[0].generation > priorGeneration &&
          snapshot.reports[0].successfulFills == 1u && snapshot.uniqueSourceRateHz == 192000u,
          "changed source requires a new successful fill and generation");

    table.ObserveSource(Key(), Source(192000.0), nullptr, 112u);
    snapshot = Capture(table, 113u);
    Check(snapshot.reports[0].decodedRateHz == 0u,
          "absent new destination cannot leave a stale decoded rate");
    auto invalidDestination = Destination(std::numeric_limits<double>::quiet_NaN());
    table.ObserveSource(Key(), Source(192000.0), &invalidDestination, 114u);
    snapshot = Capture(table, 115u);
    Check(snapshot.reports[0].decodedRateHz == 0u,
          "malformed new destination cannot leave a stale decoded rate");
}

void Bounds() {
    ConverterEvidenceTable reports;
    for (std::uintptr_t index = 1u; index <= kMaximumReports + 1u; ++index) {
        Observe(reports, Key(index), 96000.0);
    }
    auto snapshot = Capture(reports, 101u);
    Check(snapshot.activeConverters == kMaximumReports + 1u &&
          snapshot.reportCount == kMaximumReports && snapshot.conflicting &&
          snapshot.uniqueSourceRateHz == 0u,
          "report overflow remains bounded and explicitly ambiguous");

    ConverterEvidenceTable capacity;
    const auto destination = Destination();
    for (std::uintptr_t index = 1u; index <= 128u; ++index) {
        capacity.ObserveSource(Key(index), Source(), &destination, 100u);
    }
    capacity.ObserveSource(Key(129u), Source(), &destination, 100u);
    snapshot = Capture(capacity, 101u);
    Check(snapshot.capacityDrops == 1u && snapshot.activeConverters == 0u,
          "129th converter is dropped without overwriting bounded capacity");
    capacity.Filled(Key(1u), 102u);
    snapshot = Capture(capacity, 103u);
    Check(snapshot.activeConverters == 1u && snapshot.conflicting && snapshot.uniqueSourceRateHz == 0u,
          "capacity loss hides possible active converters and vetoes aggregate authority");
    capacity.Retire(Key(2u));
    capacity.ObserveSource(Key(129u), Source(48000.0), &destination, 104u);
    capacity.Filled(Key(129u), 104u);
    snapshot = Capture(capacity, 105u);
    Check(snapshot.capacityDrops == 1u && snapshot.activeConverters == 2u &&
          snapshot.conflicting && snapshot.uniqueSourceRateHz == 0u,
          "retirement frees one bounded slot without erasing other source evidence");
}

void LostLifetimeAndCreation() {
    ConverterEvidenceTable table;
    Observe(table, Key(), 192000.0);
    const auto cookie = Cookie();
    table.ObserveCookie(Key(), cookie.data(), static_cast<std::uint32_t>(cookie.size()), 100u);
    auto snapshot = Capture(table, 101u);
    const auto generation = snapshot.reports[0].generation;

    // Simulates a reset/dispose callback unable to take the nonblocking gate.
    table.ReportObservationLoss();
    snapshot = Capture(table, 102u);
    Check(snapshot.droppedEvents == 1u && snapshot.activeConverters == 0u &&
          snapshot.uniqueSourceRateHz == 0u,
          "lost lifecycle observation discards every prior converter association");
    table.Filled(Key(), 103u);
    snapshot = Capture(table, 104u);
    Check(snapshot.uniqueSourceRateHz == 0u && snapshot.reports[0].cookieRateHz == 0u,
          "a Fill after lost disposal cannot inherit source or cookie evidence");
    Observe(table, Key(), 96000.0, 105u);
    snapshot = Capture(table, 106u);
    Check(snapshot.uniqueSourceRateHz == 96000u && snapshot.reports[0].generation > generation,
          "fresh metadata and Fill can establish a new post-loss association");

    const auto newGeneration = snapshot.reports[0].generation;
    table.Created(Key());
    const auto destination = Destination();
    table.ObserveSource(Key(), Source(96000.0), &destination, 107u);
    snapshot = Capture(table, 108u);
    Check(snapshot.activeConverters == 0u && snapshot.uniqueSourceRateHz == 0u,
          "successful creation with an identical pointer and format clears old activity");
    table.Filled(Key(), 109u);
    snapshot = Capture(table, 110u);
    Check(snapshot.uniqueSourceRateHz == 96000u &&
          snapshot.reports[0].generation > newGeneration && snapshot.reports[0].successfulFills == 1u,
          "reused pointer receives an unconditional fresh creation generation");
}

void MetadataAttemptBackoff() {
    ConverterEvidenceTable table;
    Check(table.TryBeginMetadataRefresh(Key(), 100u), "first missing metadata query is immediately allowed");
    for (std::uint64_t delta = 1; delta < ConverterEvidenceTable::kMetadataRetryMs; ++delta) {
        Check(!table.TryBeginMetadataRefresh(Key(), 100u + delta),
              "failed metadata query is not repeated at audio-buffer frequency");
        table.Filled(Key(), 100u + delta);
    }
    auto snapshot = Capture(table, 350u);
    Check(snapshot.uniqueSourceRateHz == 0u,
          "metadata retry throttling never turns missing evidence into a rate");
    Check(table.TryBeginMetadataRefresh(Key(), 100u + ConverterEvidenceTable::kMetadataRetryMs),
          "failed query becomes retryable at the exact backoff boundary");
    Check(table.TryBeginMetadataRefresh(Key(), 351u, true),
          "new cookie event can explicitly refresh descriptions despite audio-query backoff");
    Check(!table.TryBeginMetadataRefresh(Key(), 352u), "forced attempt still fences immediately following Fill retries");

    const auto destination = Destination();
    table.ObserveSource(Key(), Source(), &destination, 400u);
    Check(!table.TryBeginMetadataRefresh(Key(), 401u), "successful fresh source metadata does not need polling");
    Check(table.TryBeginMetadataRefresh(Key(), 400u + kEvidenceTtlMs),
          "expired source metadata is refreshed at its TTL boundary");
    Check(!table.TryBeginMetadataRefresh(Key(), 401u + kEvidenceTtlMs),
          "failed expiration refresh also observes bounded retry delay");

    const auto cookie = Cookie();
    table.ObserveCookie(Key(), cookie.data(), static_cast<std::uint32_t>(cookie.size()), 402u + kEvidenceTtlMs);
    table.Filled(Key(), 402u + kEvidenceTtlMs);
    snapshot = Capture(table, 403u + kEvidenceTtlMs);
    Check(snapshot.uniqueSourceRateHz == 0u && table.NeedsMetadata(Key(), 403u + kEvidenceTtlMs),
          "cookie observation cannot extend the freshness of an expired source descriptor");
    Check(table.TryBeginMetadataRefresh(Key(), 99u), "clock reversal requests fresh metadata instead of retaining a future attempt");
}

} // namespace

int main() {
    SourceAndActivity();
    FreshnessAndClock();
    AmbiguityAndCookieValidation();
    LifetimesAndPointerReuse();
    Bounds();
    LostLifetimeAndCreation();
    MetadataAttemptBackoff();
    std::cout << "ConverterEvidenceTable: " << checks << " checks passed\n";
    return EXIT_SUCCESS;
}
