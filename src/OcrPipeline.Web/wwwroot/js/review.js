// Accuracy review. Vanilla JS. Reuses OcrOverlay (shared percent-positioned boxes over the rendered
// <img>). Multi-page (Phase 3): the line_item rows are ONE continuous table; each row knows its source
// page (data-pg). Focusing a row switches the page image and highlights that page's table zone; a
// numeric anchor cell with no digit flags the row as junk (deletable). Edits round-trip to the single
// canonical line_item field (rows are concatenated server-side), carrying _pg so provenance survives.
(function () {
    "use strict";

    const data = JSON.parse(document.getElementById("review-data").textContent);
    const token = document.querySelector('input[name="__RequestVerificationToken"]').value;

    const pageTableZones = {};   // page -> normalized rect for the row->page highlight
    (data.pageTableZones || []).forEach(z => { pageTableZones[z.page] = { left: z.x, top: z.y, width: z.w, height: z.h }; });

    const state = {
        documentId: data.documentId, page: 1, pageCount: data.pageCount || 1,
        ocr: { pages: [], blocks: [], tables: [] },
        values: data.values || [],
        pageTableZones,
        focusedFieldId: null,    // a scalar field is focused
        focusedRowPage: null     // a line_item row is focused -> highlight this page's table zone
    };
    const dirtyTables = new Set();   // result-value ids whose rows changed structurally (a row deleted)

    const $ = id => document.getElementById(id);
    const setStatus = t => { $("status").textContent = t || ""; };

    const blockBox = id => { const b = (state.ocr.blocks || []).find(x => x.id === id && x.bbox); return b ? b.bbox : null; };
    const zoneBox = v => (v.zoneX == null || v.zoneW == null) ? null
        : { left: v.zoneX, top: v.zoneY, width: v.zoneW, height: v.zoneH };
    const firstInputFor = fieldId => document.querySelector('[data-fieldid="' + fieldId + '"]');

    // ---- junk-row flag (mirrors ReviewTableHelpers.AnchorValueValid) ----------
    const thaiToArabic = s => s.replace(/[๐-๙]/g, d => "๐๑๒๓๔๕๖๗๘๙".indexOf(d));
    function anchorValid(dt, val) {
        dt = (dt || "STRING").toUpperCase();
        if (dt !== "INT" && dt !== "DECIMAL") return true;
        if (!val || !val.trim()) return false;
        return /[0-9]/.test(thaiToArabic(val));
    }
    function flagRow(tr) {
        const a = tr.querySelector('.li-cell[data-anchor="1"]');
        tr.classList.toggle("li-junk", !!(a && !anchorValid(a.dataset.dt, a.value)));
    }
    function refreshJunk() { document.querySelectorAll(".li-row").forEach(flagRow); }

    // ---- page image + overlay -------------------------------------------------
    async function load() {
        $("pageImg").src = `/api/documents/${state.documentId}/pages/${state.page}/image`;
        try {
            const r = await fetch(`/api/documents/${state.documentId}/ocr`, { headers: { Accept: "application/json" } });
            state.ocr = r.ok ? await r.json() : { pages: [], blocks: [], tables: [] };
        } catch { state.ocr = { pages: [], blocks: [], tables: [] }; }
        renderOverlay();
        refreshJunk();
    }

    let navCtl = null;                          // DocViewport page-nav controller (wired at init)
    function syncNav() { if (navCtl) navCtl.sync(); }

    function showPage(pg) {
        pg = Math.max(1, Math.min(pg, state.pageCount || 1));
        if (pg === state.page) { renderOverlay(); syncNav(); return; }
        state.page = pg;
        $("pageImg").src = `/api/documents/${state.documentId}/pages/${pg}/image`;
        const lbl = $("pageLabel"); if (lbl) lbl.textContent = `Page ${pg} / ${state.pageCount}`;
        renderOverlay();   // percent boxes; onImageReady re-renders once the new image has size
        syncNav();         // zoom is untouched here, so paging keeps the current zoom level
    }

    function renderOverlay() {
        const specs = [];
        state.values.forEach(v => {
            if ((v.zonePage || 1) !== state.page) return;   // only draw zones on the visible page
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
                onClick: () => { const i = firstInputFor(v.fieldId); if (i) i.focus(); }
            });
        });
        // line_item row highlight: the table zone owning the focused row's page
        if (state.focusedRowPage === state.page && state.pageTableZones[state.page]) {
            specs.push({ bbox: state.pageTableZones[state.page], className: "vm-zone vm-zone-focus",
                         title: "line items (page " + state.page + ")" });
        }
        OcrOverlay.render($("overlay"), specs);
        const f = $("overlay").querySelector(".vm-zone-focus");
        if (f) f.scrollIntoView({ block: "center", behavior: "smooth" });
    }

    // ---- interactions ---------------------------------------------------------
    function onFocus(e) {
        const el = e.target;
        if (!el.classList) return;
        if (el.classList.contains("review-input")) {
            const fid = Number(el.dataset.fieldid);
            const v = state.values.find(x => x.fieldId === fid);
            state.focusedFieldId = fid; state.focusedRowPage = null;
            showPage(v ? (v.zonePage || 1) : state.page);
        } else if (el.classList.contains("li-cell")) {
            state.focusedRowPage = Number(el.dataset.pg) || 1;
            state.focusedFieldId = null;
            showPage(state.focusedRowPage);
        }
    }

    function onInput(e) {
        if (e.target.classList && e.target.classList.contains("li-cell")) {
            const tr = e.target.closest(".li-row");
            if (tr) flagRow(tr);
        }
    }

    function onClick(e) {
        const del = e.target.closest(".li-del");
        if (del) {
            const tr = del.closest(".li-row");
            if (tr) { dirtyTables.add(Number(tr.dataset.rvid)); tr.remove(); }
            return;
        }
        const jump = e.target.closest(".li-jump");
        if (jump) {
            const wrap = jump.closest(".li-wrap");
            const row = wrap.querySelector('.li-row[data-pg="' + jump.dataset.page + '"]');
            wrap.querySelectorAll(".li-jump").forEach(x => x.classList.remove("li-jump-active"));
            jump.classList.add("li-jump-active");
            if (row) { row.scrollIntoView({ block: "nearest", behavior: "smooth" }); row.querySelector(".li-cell")?.focus(); }
        }
    }

    // ---- save -----------------------------------------------------------------
    // Walk .li-row in DOM order (deletes compact naturally; no index gaps), one group per rvid.
    function collectTableCorrections() {
        const groups = new Map();
        document.querySelectorAll(".li-row").forEach(tr => {
            const rvid = Number(tr.dataset.rvid);
            let g = groups.get(rvid);
            if (!g) { g = { resultValueId: rvid, fieldId: Number(tr.dataset.fieldid), rows: [], changed: dirtyTables.has(rvid) }; groups.set(rvid, g); }
            const row = {};
            tr.querySelectorAll(".li-cell").forEach(inp => {
                row[inp.dataset.sub] = inp.value;
                if (inp.value !== (inp.dataset.original || "")) g.changed = true;
            });
            if (tr.dataset.pg) row._pg = tr.dataset.pg;   // preserve page provenance through the round-trip
            g.rows.push(row);
        });
        return [...groups.values()].filter(g => g.changed)
            .map(g => ({ resultValueId: g.resultValueId, fieldId: g.fieldId, rows: g.rows }));
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
        dirtyTables.clear();
        setStatus(`Saved ${corrections.length + tableCorrections.length} change(s). Status: ${j.status}.`);
    }

    // Width-driven zoom + page nav are shared with the zone designer (see doc-viewport.js).
    DocViewport.attachZoom({ stage: $("stage"), zoomOut: $("zoomOut"), zoomPct: $("zoomPct"),
                             zoomIn: $("zoomIn"), zoomFit: $("zoomFit") });
    navCtl = DocViewport.attachNav({
        prevPage: $("prevPage"), nextPage: $("nextPage"), pageInput: $("pageInput"),
        getPage: () => state.page, getPageCount: () => state.pageCount, goToPage: showPage
    });

    $("saveBtn").addEventListener("click", save);
    const list = $("valueList");
    list.addEventListener("focusin", onFocus);
    list.addEventListener("input", onInput);
    list.addEventListener("click", onClick);
    OcrOverlay.onImageReady($("pageImg"), renderOverlay); // shared render-timing safety net
    load();
})();
