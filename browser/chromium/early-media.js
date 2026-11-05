// Dynamic document_start registration is limited to explicitly granted sites.
// Keep bounded format metadata until the document closes; no background timer.
globalThis[Symbol.for("source-switcher.mse-observer.v1")]?.retain?.();
