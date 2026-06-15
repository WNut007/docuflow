// Accuracy review (Prompt 5; line-item table + focus->zone highlight added later). Vanilla JS.
// Reuses OcrOverlay (shared percent-positioned boxes over the rendered <img>). Each mapped value is
// drawn from EITHER its drawn zone rect (zonal) OR its source text block (OCR-first), coloured by the
// server-computed confidence band. Focusing a field input highlights that field's zone and scrolls to it.
(function () {
    "use strict";

    const data = JSON.parse(document.getElementById("review-data").textContent);
    const token = document.querySelector('input[name="__RequestVerificationToken"]').value;
    const state = {
        documentId: data.documentId, page: 1,
        ocr: { pages: [], blocks: [], tables: [] },
        values: data.values || [],
        focusedFieldId: null
    };

    const $ = id => document.getElementById(id);
    const setStatus = t => { $("status").textContent = t || ""; };

    const blockBox = id => { const b = (state.ocr.blocks || []).find(x => x.id === id && x.bbox); return b ? b.bbox : null; };
    const zoneBox = v => (v.zoneX == null || v.zoneW == null) ? null
        : { left: v.zoneX, top: v.zoneY, width: v.zoneW, height: v.zoneH };
    const firstInputFor = fieldId => document.querySelector('[data-fieldid="' + fieldId + '"]');

    async function load() {
        $("pageImg").src = `/api/documents/${state.documentId}/pages/${state.page}/image`;
        try {
            const r = await fetch(`/api/documents/${state.documentId}/ocr`, { headers: { Accept: "application/json" } });
            state.ocr = r.ok ? await r.json() : { pages: [], blocks: [], tables: [] };
        } catch { state.ocr = { pages: [], blocks: [], tables: [] }; }
        renderOverlay();
    }

    function renderOverlay() {
        const specs = [];
        state.values.forEach(v => {
            // Prefer the drawn zone (zonal); fall back to the source text block (OCR-first).
            const zb = zoneBox(v);
            const bb = zb || (v.blockId ? blockBox(v.blockId) : null);
            if (!bb) return;
            const band = v.bandClass || "secondary";
            const focused = v.fieldId != null && v.fieldId === state.focusedFieldId;
            const kind = zb ? "vm-zone" : "vm-box";
            specs.push({
                bbox: bb,
                className: kind + " vm-band-" + band + (focused ? " vm-zone-focus" : ""),
                title: v.targetProperty,
                build: d => {
                    if (!focused) return;
                    const chip = document.createElement("span");
                    chip.className = "vm-chip text-bg-" + band;
                    chip.textContent = v.targetProperty;
                    d.appendChild(chip);
                },
                onClick: () => { const i = firstInputFor(v.fieldId); if (i) { i.focus(); } }
            });
        });
        OcrOverlay.render($("overlay"), specs);
        // Scroll the focused zone into view (it was just (re)created in the overlay).
        const f = $("overlay").querySelector(".vm-zone-focus");
        if (f) f.scrollIntoView({ block: "center", behavior: "smooth" });
    }

    function onFocus(e) {
        const el = e.target;
        if (!el.classList || !(el.classList.contains("review-input") || el.classList.contains("li-cell"))) return;
        const fid = Number(el.dataset.fieldid);
        if (Number.isNaN(fid) || fid === state.focusedFieldId) return;
        state.focusedFieldId = fid;
        renderOverlay();
    }

    // Collect edited line-item tables grouped by their single ResultValueId: rows[rowIndex][sub] = value.
    function collectTableCorrections() {
        const groups = new Map(); // rvid -> { resultValueId, fieldId, rows:[], changed:false }
        document.querySelectorAll(".li-cell").forEach(inp => {
            const rvid = Number(inp.dataset.rvid);
            let g = groups.get(rvid);
            if (!g) { g = { resultValueId: rvid, fieldId: Number(inp.dataset.fieldid), rows: [], changed: false }; groups.set(rvid, g); }
            const r = Number(inp.dataset.row);
            (g.rows[r] = g.rows[r] || {})[inp.dataset.sub] = inp.value;
            if (inp.value !== (inp.dataset.original || "")) g.changed = true;
        });
        return [...groups.values()].filter(g => g.changed)
            .map(g => ({ resultValueId: g.resultValueId, fieldId: g.fieldId, rows: g.rows.map(r => r || {}) }));
    }

    async function save() {
        const corrections = [];
        document.querySelectorAll(".review-input").forEach(inp => {
            if (inp.value !== (inp.dataset.original || ""))
                corrections.push({ resultValueId: Number(inp.dataset.rvid), normalizedValue: inp.value });
        });
        const tableCorrections = collectTableCorrections();

        setStatus("Saving…");
        const res = await fetch("/Documents/ReviewSave", {
            method: "POST",
            headers: { "Content-Type": "application/json", "RequestVerificationToken": token },
            body: JSON.stringify({ documentId: state.documentId, corrections, tableCorrections })
        });
        if (!res.ok) { setStatus("Save failed (" + res.status + ")."); return; }
        const j = await res.json();
        document.querySelectorAll(".review-input, .li-cell").forEach(inp => { inp.dataset.original = inp.value; });
        const n = corrections.length + tableCorrections.length;
        setStatus(`Saved ${n} change(s). Status: ${j.status}.`);
    }

    $("saveBtn").addEventListener("click", save);
    $("valueList").addEventListener("focusin", onFocus);
    OcrOverlay.onImageReady($("pageImg"), renderOverlay); // shared render-timing safety net
    load();
})();
