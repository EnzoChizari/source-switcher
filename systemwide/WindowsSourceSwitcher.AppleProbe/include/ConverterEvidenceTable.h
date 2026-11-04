#pragma once
#include "SourceFormatEvidence.h"
#include "ProbeProtocol.h"
#include <array>
#include <atomic>
#include <cmath>

namespace dx5::appleprobe {
// A diagnostic observer, not the upstream audio scheduler. Never inspect or copy
// PCM; never wait for a lock in an Apple callback. Contention loses an observation
// and is reported explicitly, rather than delaying playback.
class ConverterEvidenceTable final {
public:
    static constexpr std::uint64_t kMetadataRetryMs = 250;

    // A missed reset/disposal is more serious than a missed rate sample. Fence
    // every prior slot on any callback loss; a later source query and Fill must
    // establish fresh evidence before an aggregate rate can be reported again.
    void ReportObservationLoss() noexcept {
        lossEpoch_.fetch_add(1, std::memory_order_release);
        dropped_.fetch_add(1, std::memory_order_relaxed);
    }
    void Created(void* key) noexcept {
        Guard guard(*this);
        if (!guard || !key) return;
        auto* slot = FindOrAdd(key);
        if (!slot) return;
        *slot = {};
        slot->key = key;
        slot->generation = ++generation_;
        ++revision_;
    }
    void ObserveSource(void* key, const AppleAsbd& source, const AppleAsbd* destination,
                       std::uint64_t now) noexcept {
        Guard guard(*this);
        if (!guard || !key) return;
        auto* slot = FindOrAdd(key);
        if (!slot) return;
        const auto evidence = FromSourceAsbd(source);
        if (slot->hasSource && (slot->source.rateHz != evidence.rateHz ||
            slot->source.codec != evidence.codec || slot->source.channels != evidence.channels ||
            slot->source.bits != evidence.bits || slot->source.status != evidence.status)) {
            const auto pointer = slot->key;
            *slot = {};
            slot->key = pointer;
            slot->generation = ++generation_;
        }
        slot->source = evidence;
        slot->hasSource = true;
        slot->metadataTick = now;
        slot->decodedRate = 0;
        if (destination && std::isfinite(destination->sampleRate) &&
            destination->sampleRate >= 8000 && destination->sampleRate <= 768000 &&
            std::floor(destination->sampleRate) == destination->sampleRate)
            slot->decodedRate = static_cast<std::uint32_t>(destination->sampleRate);
        if (slot->hasCookie) slot->cookie = ParseAlacCookie(slot->cookieBytes.data(), slot->cookieSize, slot->source);
        ++revision_;
    }
    void ObserveCookie(void* key, const void* bytes, std::uint32_t length, std::uint64_t /*now*/) noexcept {
        Guard guard(*this);
        if (!guard || !key) return;
        auto* slot = FindOrAdd(key);
        if (!slot) return;
        // Only the possible config regions are relevant; cap copied metadata at
        // 48 bytes even when an Apple cookie also contains unrelated data.
        slot->cookieSize = bytes && length <= 4096 ? (length < slot->cookieBytes.size()
            ? length : static_cast<std::uint32_t>(slot->cookieBytes.size())) : 0;
        const auto* input = static_cast<const std::uint8_t*>(bytes);
        for (std::size_t index = 0; index < slot->cookieSize; ++index) slot->cookieBytes[index] = input[index];
        slot->hasCookie = true;
        slot->cookie = slot->cookieSize ? ParseAlacCookie(slot->cookieBytes.data(), slot->cookieSize, slot->source)
            : SourceEvidence{ParseStatus::Invalid};
        ++revision_;
    }
    bool NeedsMetadata(void* key, std::uint64_t now) noexcept {
        Guard guard(*this);
        if (!guard || !key) return false;
        auto* slot = Find(key);
        return !slot || !slot->hasSource || now < slot->metadataTick || now - slot->metadataTick >= kEvidenceTtlMs;
    }
    bool TryBeginMetadataRefresh(void* key, std::uint64_t now, bool force = false) noexcept {
        Guard guard(*this);
        if (!guard || !key) return false;
        auto* slot = FindOrAdd(key);
        if (!slot) return false;
        const bool due = !slot->hasSource || now < slot->metadataTick || now - slot->metadataTick >= kEvidenceTtlMs;
        if (!force && (!due || (slot->hasMetadataAttempt && now >= slot->metadataAttemptTick &&
            now - slot->metadataAttemptTick < kMetadataRetryMs))) return false;
        slot->hasMetadataAttempt = true;
        slot->metadataAttemptTick = now;
        return true;
    }
    void Filled(void* key, std::uint64_t now) noexcept {
        Guard guard(*this);
        if (!guard || !key) return;
        auto* slot = FindOrAdd(key);
        if (!slot) return;
        ++slot->fills;
        slot->lastFillTick = now;
        ++revision_;
    }
    void Reset(void* key) noexcept {
        Guard guard(*this);
        if (!guard) return;
        auto* slot = Find(key);
        if (!slot) return;
        const auto pointer = slot->key;
        *slot = {};
        slot->key = pointer;
        slot->generation = ++generation_;
        ++revision_;
    }
    void Retire(void* key) noexcept {
        Guard guard(*this);
        if (!guard) return;
        if (auto* slot = Find(key)) { *slot = {}; ++revision_; }
    }
    bool Capture(std::uint64_t now, ProbeSnapshot& snapshot) noexcept {
        Guard guard(*this);
        if (!guard) return false;
        snapshot = {};
        snapshot.observedTick = now;
        snapshot.revision = revision_;
        snapshot.droppedEvents = dropped_.load(std::memory_order_relaxed);
        snapshot.capacityDrops = capacityDrops_;
        for (const auto& slot : slots_) {
            if (!slot.key || slot.fills == 0 || now < slot.lastFillTick || now - slot.lastFillTick > kEvidenceTtlMs) continue;
            ++snapshot.activeConverters;
            const auto status = now < slot.metadataTick || now - slot.metadataTick > kEvidenceTtlMs
                ? ParseStatus::Unknown : CombinedStatus(slot);
            const auto rate = status == ParseStatus::Valid ? slot.source.rateHz : 0;
            if (status != ParseStatus::Valid) snapshot.conflicting = 1;
            if (rate && snapshot.uniqueSourceRateHz && rate != snapshot.uniqueSourceRateHz) snapshot.conflicting = 1;
            if (rate && !snapshot.uniqueSourceRateHz) snapshot.uniqueSourceRateHz = rate;
            if (snapshot.reportCount == kMaximumReports) { snapshot.conflicting = 1; continue; }
            auto& report = snapshot.reports[snapshot.reportCount++];
            report = {slot.generation, slot.fills, now - slot.lastFillTick, static_cast<std::uint32_t>(status),
                rate, slot.source.channels, slot.source.bits, slot.source.codec, slot.decodedRate,
                slot.cookie.rateHz, slot.cookie.bits};
        }
        // Capacity loss hides a potentially different active converter. Keep
        // that ambiguity latched for this bounded diagnostic probe. A callback
        // lost while Capture held the gate also invalidates this exact result.
        if (snapshot.capacityDrops || lossEpoch_.load(std::memory_order_acquire) != handledLossEpoch_) {
            snapshot.conflicting = 1;
            for (std::uint32_t index = 0; index < snapshot.reportCount; ++index) {
                snapshot.reports[index].status = static_cast<std::uint32_t>(ParseStatus::Unknown);
                snapshot.reports[index].sourceRateHz = 0;
            }
        }
        if (snapshot.conflicting) snapshot.uniqueSourceRateHz = 0;
        return true;
    }
private:
    struct Slot {
        void* key{};
        std::uint64_t generation{}, fills{}, lastFillTick{}, metadataTick{}, metadataAttemptTick{};
        SourceEvidence source{}, cookie{};
        std::array<std::uint8_t, 48> cookieBytes{};
        std::uint32_t cookieSize{}, decodedRate{};
        bool hasSource{}, hasCookie{}, hasMetadataAttempt{};
    };
    struct Guard {
        ConverterEvidenceTable& owner;
        bool held;
        explicit Guard(ConverterEvidenceTable& table) noexcept : owner(table), held(!table.gate_.test_and_set(std::memory_order_acquire)) {
            if (!held) table.ReportObservationLoss();
            else table.ReconcileLoss();
        }
        ~Guard() { if (held) owner.gate_.clear(std::memory_order_release); }
        explicit operator bool() const noexcept { return held; }
    };
    void ReconcileLoss() noexcept {
        const auto epoch = lossEpoch_.load(std::memory_order_acquire);
        if (epoch == handledLossEpoch_) return;
        for (auto& slot : slots_) slot = {};
        handledLossEpoch_ = epoch;
        ++revision_;
    }
    static ParseStatus CombinedStatus(const Slot& slot) noexcept {
        if (!slot.hasSource || slot.source.status != ParseStatus::Valid) return slot.source.status;
        if (slot.hasCookie && slot.cookie.status != ParseStatus::Valid) return slot.cookie.status;
        return ParseStatus::Valid;
    }
    Slot* Find(void* key) noexcept {
        for (auto& slot : slots_) if (slot.key == key) return &slot;
        return nullptr;
    }
    Slot* FindOrAdd(void* key) noexcept {
        if (auto* slot = Find(key)) return slot;
        for (auto& slot : slots_) if (!slot.key) { slot.key = key; slot.generation = ++generation_; return &slot; }
        ++capacityDrops_;
        return nullptr;
    }
    std::array<Slot, 128> slots_{};
    std::atomic_flag gate_ = ATOMIC_FLAG_INIT;
    std::atomic<std::uint64_t> dropped_{};
    std::atomic<std::uint64_t> lossEpoch_{};
    std::uint64_t handledLossEpoch_{}, generation_{}, revision_{}, capacityDrops_{};
};
}
