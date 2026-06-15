// Zone designer (zonal / template-based OCR). Vanilla JS, no framework.
// LEFT: page image. Pick a field on the RIGHT, then drag a rectangle over its value. Existing zones
// render via the shared OcrOverlay (percent-positioned); this file adds draw / move / resize.
(function () {
    "use strict";

    const data = JSON.parse(document.getElementById("zone-data").textContent);
    const token = document.querySelector('input[name="__RequestVerificationToken"]').value;

    const state = {
        templateId: data.templateId,
        mappingMode: data.mappingMode || "OCR_FIRST",
        documentId: data.documentId,
        pageCount: data.pageCount || 0,
        page: 1,
        armed: null,                 // index of the field being drawn
        fields: (data.fields || []).map(normalizeField)
    };

    function numOrNull(v) { return (v === null || v === undefined || v === "") ? null : Number(v); }
    function normalizeField(f) {
        return {
            fieldId: f.fieldId, targetProperty: f.targetProperty || "", dataType: f.dataType || "STRING",
            isRequired: !!f.isRequired, minConfidence: f.minConfidence || 0.6,
            zonePage: f.zonePage || null,
            zoneX: numOrNull(f.zoneX), zoneY: numOrNull(f.zoneY), zoneW: numOrNull(f.zoneW), zoneH: numOrNull(f.zoneH),
            ocrHint: f.zoneOcrHint || "TEXT", psm: f.zonePsm || null,
            _changed: false
        };
    }

    const $ = id => document.getElementById(id);
    function el(tag, cls, txt) { const e = document.createElement(tag); if (cls) e.className = cls; if (txt != null) e.textContent = txt; return e; }
    function setStatus(t) { $("status").textContent = t || ""; }
    const clamp01 = v => Math.max(0, Math.min(1, v));

    // ---- document / image -----------------------------------------------------
    function loadDoc() {
        if (!state.documentId) { $("stage").classList.add("d-none"); $("noDoc").classList.remove("d-none"); return; }
        $("stage").classList.remove("d-none"); $("noDoc").classList.add("d-none");
        $("pageImg").src = `/api/documents/${state.documentId}/pages/${state.page}/image`;
        renderOverlay(); renderFields(); updatePager();
    }
    function updatePager() {
        $("pageLabel").textContent = `${state.page} / ${state.pageCount || 1}`;
        $("prevPage").disabled = state.page <= 1;
        $("nextPage").disabled = state.page >= state.pageCount;
    }

    // ---- overlay (existing zones) ---------------------------------------------
    function renderOverlay() {
        const specs = [];
        state.fields.forEach((f, idx) => {
            if (f.zoneX == null || (f.zonePage || 1) !== state.page) return;
            specs.push({
                bbox: { left: f.zoneX, top: f.zoneY, width: f.zoneW, height: f.zoneH },
                className: "zone-box" + (idx === state.armed ? " zone-armed" : ""),
                title: f.targetProperty,
                build: d => {
                    d.dataset.fieldIdx = idx;
                    d.appendChild(el("span", "zone-label", f.targetProperty || "(field)"));
                    const h = el("span", "zone-handle"); h.dataset.handle = "1"; d.appendChild(h);
                }
            });
        });
        OcrOverlay.render($("overlay"), specs);
    }

    // ---- draw / move / resize -------------------------------------------------
    const overlay = $("overlay");
    let drag = null;

    function normPoint(e) {
        const r = overlay.getBoundingClientRect();
        return { x: clamp01((e.clientX - r.left) / r.width), y: clamp01((e.clientY - r.top) / r.height) };
    }
    function zoneOf(idx) { const f = state.fields[idx]; return { x: f.zoneX, y: f.zoneY, w: f.zoneW, h: f.zoneH }; }
    function arm(idx) { state.armed = idx; renderFields(); renderOverlay(); }

    overlay.addEventListener("mousedown", e => {
        const p = normPoint(e);
        const boxEl = e.target.closest("[data-field-idx]");
        if (e.target.dataset && e.target.dataset.handle && boxEl) {
            const idx = Number(boxEl.dataset.fieldIdx);
            drag = { mode: "resize", idx, sx: p.x, sy: p.y, orig: zoneOf(idx) }; arm(idx);
        } else if (boxEl) {
            const idx = Number(boxEl.dataset.fieldIdx);
            drag = { mode: "move", idx, sx: p.x, sy: p.y, orig: zoneOf(idx) }; arm(idx);
        } else if (state.armed != null) {
            const f = state.fields[state.armed];
            f.zoneX = p.x; f.zoneY = p.y; f.zoneW = 0; f.zoneH = 0; f.zonePage = state.page;
            drag = { mode: "draw", idx: state.armed, sx: p.x, sy: p.y };
        } else { setStatus("Pick a field first (Draw), then drag its zone."); return; }
        e.preventDefault();
    });

    window.addEventListener("mousemove", e => {
        if (!drag) return;
        const p = normPoint(e);
        const f = state.fields[drag.idx];
        if (drag.mode === "draw") {
            f.zoneX = Math.min(drag.sx, p.x); f.zoneY = Math.min(drag.sy, p.y);
            f.zoneW = Math.abs(p.x - drag.sx); f.zoneH = Math.abs(p.y - drag.sy);
        } else if (drag.mode === "move") {
            f.zoneX = Math.min(clamp01(drag.orig.x + (p.x - drag.sx)), 1 - drag.orig.w);
            f.zoneY = Math.min(clamp01(drag.orig.y + (p.y - drag.sy)), 1 - drag.orig.h);
            f.zoneW = drag.orig.w; f.zoneH = drag.orig.h;
        } else if (drag.mode === "resize") {
            f.zoneW = Math.max(0.005, drag.orig.w + (p.x - drag.sx));
            f.zoneH = Math.max(0.005, drag.orig.h + (p.y - drag.sy));
            if (f.zoneX + f.zoneW > 1) f.zoneW = 1 - f.zoneX;
            if (f.zoneY + f.zoneH > 1) f.zoneH = 1 - f.zoneY;
        }
        renderOverlay();
    });

    window.addEventListener("mouseup", () => {
        if (!drag) return;
        const f = state.fields[drag.idx];
        if (drag.mode === "draw" && (f.zoneW < 0.01 || f.zoneH < 0.01)) {
            f.zoneX = f.zoneY = f.zoneW = f.zoneH = null;   // discard accidental click
        } else { f._changed = true; setStatus(`Zone set for ${f.targetProperty || "field"} — remember to Save.`); }
        drag = null; renderOverlay(); renderFields();
    });

    // ---- right panel ----------------------------------------------------------
    function renderFields() {
        const pane = $("pane-fields"); pane.innerHTML = "";
        const q = $("filter").value.trim().toLowerCase();
        state.fields.forEach((f, idx) => {
            if (q && !(f.targetProperty || "").toLowerCase().includes(q)) return;
            const card = el("div", "card mb-2" + (idx === state.armed ? " zone-field-armed" : ""));
            const body = el("div", "card-body p-2");

            const head = el("div", "d-flex gap-2 align-items-center");
            const name = el("input", "form-control form-control-sm"); name.value = f.targetProperty; name.placeholder = "target property";
            name.addEventListener("input", () => { f.targetProperty = name.value; f._changed = true; });
            const dt = el("select", "form-select form-select-sm"); dt.style.maxWidth = "100px";
            ["STRING", "DECIMAL", "DATE", "INT", "BOOL"].forEach(o => { const op = el("option", null, o); op.selected = f.dataType === o; dt.appendChild(op); });
            dt.addEventListener("change", () => { f.dataType = dt.value; f._changed = true; });
            const armBtn = el("button", "btn btn-sm " + (idx === state.armed ? "btn-warning" : "btn-outline-primary"), "Draw");
            armBtn.type = "button"; armBtn.addEventListener("click", () => arm(idx));
            head.append(name, dt, armBtn);

            const tools = el("div", "d-flex gap-2 align-items-center mt-1");
            tools.appendChild(el("span", "small text-body-secondary", "OCR"));
            const hint = el("select", "form-select form-select-sm"); hint.style.maxWidth = "130px";
            [["TEXT", "Text"], ["NUMERIC", "Numeric"], ["DATE", "Date"], ["INT", "Integer"]].forEach(([v, t]) => {
                const op = el("option", null, t); op.value = v; op.selected = (f.ocrHint || "TEXT") === v; hint.appendChild(op);
            });
            hint.addEventListener("change", () => { f.ocrHint = hint.value; f._changed = true; });
            tools.appendChild(hint);

            if (f.zoneX != null) {
                tools.appendChild(el("span", "badge text-bg-success", "zone p" + (f.zonePage || 1)));
                const clr = el("button", "btn btn-sm btn-outline-danger py-0 ms-auto"); clr.type = "button";
                clr.appendChild(el("i", "bi bi-x"));
                clr.addEventListener("click", () => { f.zoneX = f.zoneY = f.zoneW = f.zoneH = null; f._changed = true; renderFields(); renderOverlay(); });
                tools.appendChild(clr);
            } else {
                tools.appendChild(el("span", "badge text-bg-secondary ms-auto", "no zone"));
            }

            body.append(head, tools); card.appendChild(body); pane.appendChild(card);
        });
    }

    // ---- save -----------------------------------------------------------------
    async function save() {
        const payload = {
            templateId: state.templateId,
            mappingMode: state.mappingMode,
            fields: state.fields
                .filter(f => (f.targetProperty || "").trim().length > 0 && f.zoneX != null)
                .map(f => ({
                    fieldId: f.fieldId, targetProperty: f.targetProperty, dataType: f.dataType,
                    isRequired: !!f.isRequired, minConfidence: f.minConfidence || 0.6,
                    zonePage: f.zonePage || 1, zoneX: f.zoneX, zoneY: f.zoneY, zoneW: f.zoneW, zoneH: f.zoneH,
                    zoneOcrHint: f.ocrHint || "TEXT", zonePsm: f.psm || null
                }))
        };
        if (state.mappingMode === "ZONAL" && payload.fields.length === 0) {
            setStatus("Draw at least one zone before saving in ZONAL mode."); return;
        }
        setStatus("Saving…");
        const res = await fetch("/Mapping/ZonesSave", {
            method: "POST",
            headers: { "Content-Type": "application/json", "RequestVerificationToken": token },
            body: JSON.stringify(payload)
        });
        setStatus(res.ok ? `Saved (${state.mappingMode}).` : "Save failed (" + res.status + ").");
    }

    // ---- wiring ---------------------------------------------------------------
    $("modeSelect").value = state.mappingMode;
    $("modeSelect").addEventListener("change", e => { state.mappingMode = e.target.value; });
    $("docSelect").addEventListener("change", e => { state.documentId = Number(e.target.value); state.page = 1; loadDoc(); });
    $("templateSelect").addEventListener("change", e => { location.href = `/Mapping/Zones?templateId=${e.target.value}`; });
    $("prevPage").addEventListener("click", () => { if (state.page > 1) { state.page--; loadDoc(); } });
    $("nextPage").addEventListener("click", () => { if (state.page < state.pageCount) { state.page++; loadDoc(); } });
    $("filter").addEventListener("input", renderFields);
    $("addField").addEventListener("click", () => {
        state.fields.push({ fieldId: 0, targetProperty: "", dataType: "STRING", isRequired: false, minConfidence: 0.6, zonePage: null, zoneX: null, zoneY: null, zoneW: null, zoneH: null, ocrHint: "TEXT", psm: null, _changed: true });
        state.armed = state.fields.length - 1; renderFields();
    });
    $("saveBtn").addEventListener("click", save);
    OcrOverlay.onImageReady($("pageImg"), renderOverlay);

    loadDoc();
})();
