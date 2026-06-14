// Shared OCR overlay placement — the ONE place that positions clickable boxes over a rendered
// page image. Both visual-mapping.js and review.js use this, so the render-timing / geometry fix
// (percent positioning that tracks the rendered <img>, re-rendered on image load) lives here only.
//
// A "spec" describes one box: { bbox:{left,top,width,height} (0..1), className, title?, build?(el), onClick? }
window.OcrOverlay = (function () {
    "use strict";

    const clamp = v => Math.max(0, Math.min(1, Number(v) || 0));

    function place(parent, spec) {
        const b = spec.bbox;
        if (!b) return null;
        const d = document.createElement("div");
        d.className = spec.className || "vm-box";
        d.style.left = (clamp(b.left) * 100) + "%";
        d.style.top = (clamp(b.top) * 100) + "%";
        d.style.width = (clamp(b.width) * 100) + "%";
        d.style.height = (clamp(b.height) * 100) + "%";
        if (spec.title) d.title = spec.title;
        if (typeof spec.build === "function") spec.build(d);
        if (typeof spec.onClick === "function") d.addEventListener("click", spec.onClick);
        parent.appendChild(d);
        return d;
    }

    // Clears the overlay and places every spec (percent-positioned, resolution-independent).
    function render(overlayEl, specs) {
        if (!overlayEl) return;
        overlayEl.innerHTML = "";
        (specs || []).forEach(s => place(overlayEl, s));
    }

    // Re-run the render once the image has a rendered size (the fix for boxes collapsing when the
    // overlay is built before the image loads). Page-specific code just supplies its render fn.
    function onImageReady(img, renderFn) {
        if (img) img.addEventListener("load", renderFn);
    }

    return { place, render, onImageReady, clamp };
})();
