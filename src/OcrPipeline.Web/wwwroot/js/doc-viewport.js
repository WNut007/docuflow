// Shared document-viewport controls for the page-image panes (Review + Zone designer).
// Two independent helpers:
//   attachZoom — width-driven zoom (NOT transform:scale). Sets ONLY the stage width (% of the
//     scrollable viewport); the inset:0 overlay and its percent-positioned boxes track the stage
//     automatically, so the host never re-renders the overlay on zoom and any getBoundingClientRect-
//     based hit-testing (e.g. the designer's draw handler) stays correct at every level.
//   attachNav — prev/next + an editable page-number input. The HOST owns the current page and the
//     actual page load (goToPage); this only wires the controls and reflects state via sync().
// All control elements are optional (single-page docs omit the nav; a sample-less designer omits the
// whole toolbar) — every element is null-guarded so the same call site works in every case.
window.DocViewport = (function () {
    "use strict";

    const ZOOM_MIN = 0.5, ZOOM_MAX = 3, ZOOM_STEP = 0.25;   // fit-to-width == 100% (baseline)

    // opts: { stage, zoomOut, zoomPct, zoomIn, zoomFit }. Returns { set, get }.
    function attachZoom(opts) {
        const { stage, zoomOut, zoomPct, zoomIn, zoomFit } = opts;
        if (!stage) return { set() {}, get: () => 1 };
        let zoom = 1;
        function apply() {
            stage.style.width = Math.round(zoom * 100) + "%";
            if (zoomPct) zoomPct.textContent = Math.round(zoom * 100) + "%";
            if (zoomOut) zoomOut.disabled = zoom <= ZOOM_MIN + 1e-9;
            if (zoomIn) zoomIn.disabled = zoom >= ZOOM_MAX - 1e-9;
        }
        function set(z) { zoom = Math.min(ZOOM_MAX, Math.max(ZOOM_MIN, z)); apply(); }
        if (zoomIn) zoomIn.addEventListener("click", () => set(zoom + ZOOM_STEP));
        if (zoomOut) zoomOut.addEventListener("click", () => set(zoom - ZOOM_STEP));
        if (zoomFit) zoomFit.addEventListener("click", () => set(1));
        apply();
        return { set, get: () => zoom };
    }

    // opts: { prevPage, nextPage, pageInput, getPage, getPageCount, goToPage }. Returns { sync }.
    // goToPage(n) is the host's clamped page-load; the host calls sync() after the page changes.
    function attachNav(opts) {
        const { prevPage, nextPage, pageInput, getPage, getPageCount, goToPage } = opts;
        function sync() {
            const page = getPage(), count = getPageCount() || 1;
            if (prevPage) prevPage.disabled = page <= 1;
            if (nextPage) nextPage.disabled = page >= count;
            if (pageInput) pageInput.value = page;          // also reflects clamping (typed "9" -> last)
        }
        function jump() {
            if (!pageInput) return;
            const n = parseInt(pageInput.value, 10);
            goToPage(Number.isFinite(n) ? n : 1);            // empty/NaN -> page 1; host clamps the range
            pageInput.value = getPage();                     // snap the field to the clamped result
        }
        if (prevPage) prevPage.addEventListener("click", () => goToPage(getPage() - 1));
        if (nextPage) nextPage.addEventListener("click", () => goToPage(getPage() + 1));
        if (pageInput) {
            pageInput.addEventListener("keydown", e => { if (e.key === "Enter") { e.preventDefault(); jump(); pageInput.blur(); } });
            pageInput.addEventListener("blur", jump);
        }
        sync();
        return { sync };
    }

    return { attachZoom, attachNav, ZOOM_MIN, ZOOM_MAX, ZOOM_STEP };
})();
