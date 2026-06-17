// Zone designer (zonal / template-based OCR). Vanilla JS, no framework.
// LEFT: page image. Pick a field on the RIGHT, then drag a rectangle over its value. A field can be a
// scalar zone OR a line_item TABLE: draw the table rect, then drag column separators and map each
// column to a sub-field (description/qty/unit_price/amount), marking one column as the row ANCHOR.
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
        armed: null,                 // index of the SELECTED field (drives highlight + table separators)
        drawIntent: false,           // true only after "Draw" is clicked; one mousedown-draw consumes it
        fields: (data.fields || []).map(normalizeField),
        removedFieldIds: []          // saved fields the user deleted (sent to the server to remove)
    };

    function numOrNull(v) { return (v === null || v === undefined || v === "") ? null : Number(v); }
    function normalizeField(f) {
        return {
            fieldId: f.fieldId, targetProperty: f.targetProperty || "", dataType: f.dataType || "STRING",
            isRequired: !!f.isRequired, minConfidence: f.minConfidence || 0.6,
            sourceType: f.sourceType || "KEY_VALUE",
            zonePage: f.zonePage || null,
            zoneX: numOrNull(f.zoneX), zoneY: numOrNull(f.zoneY), zoneW: numOrNull(f.zoneW), zoneH: numOrNull(f.zoneH),
            ocrHint: f.zoneOcrHint || "TEXT", psm: f.zonePsm || null,
            role: f.zonePageRole || "",            // "" = single page; FIRST/CONTINUATION/LAST = multi-page (Phase 3)
            columns: (f.columns || []).map(normalizeColumn),
            _changed: false
        };
    }
    function normalizeColumn(c) {
        return {
            columnId: c.columnId || 0, targetSubProperty: c.targetSubProperty || "", dataType: c.dataType || "STRING",
            sortOrder: c.sortOrder || 0, isAnchor: !!c.isAnchor,
            colXStart: numOrNull(c.colXStart), colXEnd: numOrNull(c.colXEnd),
            lineSelectMode: c.lineSelectMode || "ALL", lineSelectIndices: c.lineSelectIndices || "",
            lineJoinSeparator: c.lineJoinSeparator == null ? " " : c.lineJoinSeparator
        };
    }

    const $ = id => document.getElementById(id);
    function el(tag, cls, txt) { const e = document.createElement(tag); if (cls) e.className = cls; if (txt != null) e.textContent = txt; return e; }
    function setStatus(t) { $("status").textContent = t || ""; }
    const clamp01 = v => Math.max(0, Math.min(1, v));
    const isTable = f => f.sourceType === "TABLE_CELL";

    const ROLES = ["FIRST", "CONTINUATION", "LAST"];
    const ROLE_LABELS = { "": "Single page", FIRST: "First (header)", CONTINUATION: "Continuation", LAST: "Last (totals)" };
    const normRole = r => { r = (r || "").trim().toUpperCase(); return r === "CONT" ? "CONTINUATION" : (ROLES.includes(r) ? r : ""); };
    const roleRank = r => { const i = ROLES.indexOf(normRole(r)); return i < 0 ? ROLES.length : i; };
    const propKey = f => (f.targetProperty || "").trim().toLowerCase();
    // default line_item sub-columns when a new table is created (anchor on qty)
    function defaultTableColumns() {
        const cols = ["description", "qty", "unit_price", "amount"].map(n =>
            normalizeColumn({ targetSubProperty: n, dataType: n === "description" ? "STRING" : (n === "qty" ? "INT" : "DECIMAL") }));
        (cols.find(c => c.targetSubProperty === "qty") || cols[0]).isAnchor = true;
        return cols;
    }

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

    // ---- columns --------------------------------------------------------------
    // Columns store page-normalized x-boundaries. Equal-split across the table rect when (re)flowed;
    // dragging a separator overrides the shared boundary of the two adjacent columns.
    function reflowColumns(f) {
        if (f.zoneX == null || f.columns.length === 0) return;
        const n = f.columns.length, w = f.zoneW / n;
        f.columns.forEach((c, i) => { c.colXStart = f.zoneX + i * w; c.colXEnd = f.zoneX + (i + 1) * w; c.sortOrder = i; });
    }
    function addColumn(f) {
        f.columns.push(normalizeColumn({ targetSubProperty: "", dataType: "STRING" }));
        if (!f.columns.some(c => c.isAnchor)) f.columns[0].isAnchor = true;
        reflowColumns(f); f._changed = true; renderFields(); renderOverlay();
    }
    function removeColumn(f, idx) {
        f.columns.splice(idx, 1);
        if (f.columns.length && !f.columns.some(c => c.isAnchor)) f.columns[0].isAnchor = true;
        reflowColumns(f); f._changed = true; renderFields(); renderOverlay();
    }
    // Phase 3: copy the column definitions from the FIRST-role table region of the same property (or
    // any sibling table with columns) so CONTINUATION/LAST regions don't redraw the sub-fields. Their
    // x-boundaries are re-flowed to THIS region's rect (fine-tune separators afterwards).
    function copyColumnsFromFirst(f) {
        const same = o => o !== f && isTable(o)
            && (o.targetProperty || "").trim().toLowerCase() === (f.targetProperty || "").trim().toLowerCase();
        const source = state.fields.find(o => same(o) && o.role === "FIRST" && o.columns.length > 0)
            || state.fields.find(o => same(o) && o.columns.length > 0);
        if (!source) { setStatus("No FIRST table columns to copy yet."); return; }
        f.columns = source.columns.map(c => normalizeColumn({
            targetSubProperty: c.targetSubProperty, dataType: c.dataType, sortOrder: c.sortOrder,
            isAnchor: c.isAnchor, lineSelectMode: c.lineSelectMode,
            lineSelectIndices: c.lineSelectIndices, lineJoinSeparator: c.lineJoinSeparator
        }));
        if (f.zoneX != null) reflowColumns(f);
        f._changed = true; renderFields(); renderOverlay();
    }

    // ---- overlay (existing zones + table separators) --------------------------
    function renderOverlay() {
        const specs = [];
        state.fields.forEach((f, idx) => {
            if (f.zoneX == null || (f.zonePage || 1) !== state.page) return;
            const armed = idx === state.armed;
            specs.push({
                bbox: { left: f.zoneX, top: f.zoneY, width: f.zoneW, height: f.zoneH },
                className: "zone-box" + (armed ? " zone-armed" : "") + (isTable(f) ? " zone-table" : ""),
                title: f.targetProperty,
                build: d => {
                    d.dataset.fieldIdx = idx;
                    d.appendChild(el("span", "zone-label", (isTable(f) ? "▦ " : "") + (f.targetProperty || "(field)")));
                    if (isTable(f) && armed) buildSeparators(d, f, idx);
                    const h = el("span", "zone-handle"); h.dataset.handle = "1"; d.appendChild(h);
                }
            });
        });
        OcrOverlay.render($("overlay"), specs);
    }

    // internal column separators rendered inside the table box (percent of the box width)
    function buildSeparators(boxEl, f, fieldIdx) {
        if (f.zoneW <= 0) return;
        for (let i = 0; i < f.columns.length - 1; i++) {
            const pageX = f.columns[i].colXEnd;
            if (pageX == null) continue;
            const sep = el("span", "zone-sep");
            sep.style.left = clamp01((pageX - f.zoneX) / f.zoneW) * 100 + "%";
            sep.dataset.sep = String(i);
            sep.dataset.fieldIdx = String(fieldIdx);
            boxEl.appendChild(sep);
        }
    }

    // ---- draw / move / resize / separator drag --------------------------------
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
        if (e.target.dataset && e.target.dataset.sep !== undefined) {       // drag a column separator
            const idx = Number(e.target.dataset.fieldIdx);
            drag = { mode: "sep", idx, sep: Number(e.target.dataset.sep) }; arm(idx);
        } else if (e.target.dataset && e.target.dataset.handle && boxEl) {
            const idx = Number(boxEl.dataset.fieldIdx);
            drag = { mode: "resize", idx, sx: p.x, sy: p.y, orig: zoneOf(idx) }; arm(idx);
        } else if (boxEl) {
            const idx = Number(boxEl.dataset.fieldIdx);
            drag = { mode: "move", idx, sx: p.x, sy: p.y, orig: zoneOf(idx) }; arm(idx);
        } else if (state.drawIntent && state.armed != null) {              // draw only with an explicit Draw-intent
            const f = state.fields[state.armed];
            f.zoneX = p.x; f.zoneY = p.y; f.zoneW = 0; f.zoneH = 0; f.zonePage = state.page;
            drag = { mode: "draw", idx: state.armed, sx: p.x, sy: p.y };
        } else {
            setStatus(state.armed != null
                ? "Click “Draw” to (re)draw this field’s zone."     // selected but no draw-intent -> no-op
                : "Pick a field first (Draw), then drag its zone.");
            return;
        }
        e.preventDefault();
    });

    window.addEventListener("mousemove", e => {
        if (!drag) return;
        const p = normPoint(e);
        const f = state.fields[drag.idx];
        if (drag.mode === "draw") {
            f.zoneX = Math.min(drag.sx, p.x); f.zoneY = Math.min(drag.sy, p.y);
            f.zoneW = Math.abs(p.x - drag.sx); f.zoneH = Math.abs(p.y - drag.sy);
            setStatus(`Zone: ${(f.zoneW * 100).toFixed(1)}% × ${(f.zoneH * 100).toFixed(1)}%`); // live size cue
        } else if (drag.mode === "move") {
            f.zoneX = Math.min(clamp01(drag.orig.x + (p.x - drag.sx)), 1 - drag.orig.w);
            f.zoneY = Math.min(clamp01(drag.orig.y + (p.y - drag.sy)), 1 - drag.orig.h);
            f.zoneW = drag.orig.w; f.zoneH = drag.orig.h;
            if (isTable(f)) reflowColumns(f);
        } else if (drag.mode === "resize") {
            f.zoneW = Math.max(0.005, drag.orig.w + (p.x - drag.sx));
            f.zoneH = Math.max(0.005, drag.orig.h + (p.y - drag.sy));
            if (f.zoneX + f.zoneW > 1) f.zoneW = 1 - f.zoneX;
            if (f.zoneY + f.zoneH > 1) f.zoneH = 1 - f.zoneY;
            if (isTable(f)) reflowColumns(f);
        } else if (drag.mode === "sep") {
            const lo = f.columns[drag.sep].colXStart, hi = f.columns[drag.sep + 1].colXEnd;
            const x = Math.max(lo + 0.005, Math.min(hi - 0.005, p.x));     // clamp between neighbours
            f.columns[drag.sep].colXEnd = x; f.columns[drag.sep + 1].colXStart = x;
        }
        renderOverlay();
    });

    window.addEventListener("mouseup", () => {
        if (!drag) return;
        const idx = drag.idx;
        const f = state.fields[idx];
        if (drag.mode === "draw" && f.zoneW < 0.005 && f.zoneH < 0.005) {
            f.zoneX = f.zoneY = f.zoneW = f.zoneH = null;   // discard accidental click (tiny dot in BOTH axes)
        } else {
            f._changed = true;
            if (drag.mode === "draw" && isTable(f)) reflowColumns(f);
            // A redraw NEVER changes an existing region's role: role is the region's logical slot in
            // its table group, not the page on screen. Only a brand-new, still-role-less TABLE region
            // gets a suggested role — via nextFreeRole so it can't collide with siblings, and
            // independent of the viewed page (no re-stamping from state.page / f.zonePage).
            if (drag.mode === "draw" && state.pageCount > 1 && !f.role && isTable(f))
                f.role = nextFreeRole(groupOf(f));
            setStatus(`Zone set for ${f.targetProperty || "field"} — remember to Save.`);
        }
        // Any finished drag consumes the Draw-intent (scalar AND table), so a later mousedown on empty
        // canvas can't start a fresh zero-size draw and clobber the zone. The field stays SELECTED
        // (state.armed), so table separators remain visible/draggable; move/resize use the box/handle
        // and never needed the intent. Re-draw by clicking the field's "Draw" button again.
        state.drawIntent = false;
        drag = null; renderOverlay(); renderFields();
    });

    // ---- right panel ----------------------------------------------------------
    // Scalar fields render one card each. TABLE_CELL fields are GROUPED by TargetProperty into one
    // "table card": the name lives ONCE on the table; each page-region under it has only a role + zone
    // + columns (no name input) — so a multi-page table's regions share one property BY CONSTRUCTION.
    function renderFields() {
        const pane = $("pane-fields"); pane.innerHTML = "";
        const q = $("filter").value.trim().toLowerCase();

        state.fields.forEach((f, idx) => {
            if (isTable(f)) return;
            if (q && !(f.targetProperty || "").toLowerCase().includes(q)) return;
            pane.appendChild(renderScalarCard(f, idx));
        });

        // group table regions by property, preserving first-seen order
        const groups = new Map();
        state.fields.forEach((f, idx) => {
            if (!isTable(f)) return;
            const key = propKey(f);
            if (!groups.has(key)) groups.set(key, { name: f.targetProperty || "", items: [] });
            groups.get(key).items.push({ f, idx });
        });
        groups.forEach(g => {
            if (q && !g.name.toLowerCase().includes(q)) return;
            pane.appendChild(renderTableCard(g));
        });
    }

    function zoneRowFor(f, idx) {
        const zoneRow = el("div", "d-flex gap-2 align-items-center mt-1");
        if (f.zoneX != null) {
            zoneRow.appendChild(el("span", "badge text-bg-success", "zone p" + (f.zonePage || 1)));
            const clr = el("button", "btn btn-sm btn-outline-danger py-0 ms-auto"); clr.type = "button";
            clr.appendChild(el("i", "bi bi-x")); clr.title = "clear zone";
            clr.addEventListener("click", () => { f.zoneX = f.zoneY = f.zoneW = f.zoneH = null; f._changed = true; renderFields(); renderOverlay(); });
            zoneRow.appendChild(clr);
        } else {
            zoneRow.appendChild(el("span", "badge text-bg-secondary ms-auto", "no zone"));
        }
        return zoneRow;
    }

    function drawButton(idx) {
        const b = el("button", "btn btn-sm " + (idx === state.armed ? "btn-warning" : "btn-outline-primary"), "Draw");
        b.type = "button"; b.addEventListener("click", () => { state.drawIntent = true; arm(idx); });
        return b;
    }

    function renderScalarCard(f, idx) {
        const card = el("div", "card mb-2" + (idx === state.armed ? " zone-field-armed" : ""));
        const body = el("div", "card-body p-2");

        const head = el("div", "d-flex gap-2 align-items-center");
        const name = el("input", "form-control form-control-sm"); name.value = f.targetProperty; name.placeholder = "target property";
        name.addEventListener("input", () => { f.targetProperty = name.value; f._changed = true; });
        head.append(name, drawButton(idx));
        body.appendChild(head);

        // page-role for a scalar header/total zone (FIRST=read on p1, LAST=read on last page).
        const roleRow = el("div", "d-flex gap-2 align-items-center mt-1");
        roleRow.appendChild(el("span", "small text-body-secondary", "Page"));
        const roleSel = el("select", "form-select form-select-sm"); roleSel.style.maxWidth = "160px";
        [["", "Single page"], ["FIRST", "First (header)"], ["CONTINUATION", "Continuation"], ["LAST", "Last (totals)"]]
            .forEach(([v, t]) => { const op = el("option", null, t); op.value = v; op.selected = (f.role || "") === v; roleSel.appendChild(op); });
        roleSel.addEventListener("change", () => { f.role = roleSel.value; f._changed = true; renderOverlay(); });
        roleRow.appendChild(roleSel);
        body.appendChild(roleRow);

        const tools = el("div", "d-flex gap-2 align-items-center mt-1");
        tools.appendChild(el("span", "small text-body-secondary", "OCR"));
        const hint = el("select", "form-select form-select-sm"); hint.style.maxWidth = "130px";
        [["TEXT", "Text"], ["NUMERIC", "Numeric"], ["DATE", "Date"], ["INT", "Integer"]].forEach(([v, t]) => {
            const op = el("option", null, t); op.value = v; op.selected = (f.ocrHint || "TEXT") === v; hint.appendChild(op);
        });
        hint.addEventListener("change", () => { f.ocrHint = hint.value; f._changed = true; });
        const dt = el("select", "form-select form-select-sm"); dt.style.maxWidth = "110px";
        ["STRING", "DECIMAL", "DATE", "INT", "BOOL"].forEach(o => { const op = el("option", null, o); op.selected = f.dataType === o; dt.appendChild(op); });
        dt.addEventListener("change", () => { f.dataType = dt.value; f._changed = true; });
        tools.append(hint, dt);
        body.appendChild(tools);

        body.appendChild(zoneRowFor(f, idx));
        card.appendChild(body);
        return card;
    }

    function renderTableCard(g) {
        // duplicate-role detection (e.g. a legacy template with two CONTINUATION regions) -> flag on load
        const roleCounts = {};
        g.items.forEach(({ f }) => { const r = normRole(f.role); if (r) roleCounts[r] = (roleCounts[r] || 0) + 1; });
        const armedHere = g.items.some(({ idx }) => idx === state.armed);

        const card = el("div", "card mb-2 zone-table-card" + (armedHere ? " zone-field-armed" : ""));
        const body = el("div", "card-body p-2");

        // ONE name input for the whole table — editing renames every region in the group together.
        const head = el("div", "d-flex gap-2 align-items-center");
        head.appendChild(el("span", "fs-5", "▦"));
        const name = el("input", "form-control form-control-sm fw-semibold"); name.value = g.name; name.placeholder = "table name (e.g. line_item)";
        name.addEventListener("input", () => {
            g.name = name.value;
            g.items.forEach(({ f }) => { f.targetProperty = name.value; f._changed = true; });
        });
        head.appendChild(name);
        body.appendChild(head);
        body.appendChild(el("div", "small text-body-secondary mt-1",
            g.items.length > 1 ? "Multi-page table — one row per page-region; all regions share this name." : "Line-item table."));

        // sort regions by role position so the card reads FIRST → CONTINUATION → LAST
        const ordered = g.items.slice().sort((a, b) => roleRank(a.f.role) - roleRank(b.f.role) || a.idx - b.idx);
        ordered.forEach(({ f, idx }) => body.appendChild(renderRegion(g, f, idx, roleCounts)));

        const add = el("button", "btn btn-sm btn-outline-secondary mt-2", "+ Add page region"); add.type = "button";
        add.addEventListener("click", () => addPageRegion(g));
        body.appendChild(add);

        card.appendChild(body);
        return card;
    }

    function renderRegion(g, f, idx, roleCounts) {
        const dup = roleCounts[normRole(f.role)] > 1;
        const region = el("div", "border rounded p-2 mt-2" + (idx === state.armed ? " border-primary" : "") + (dup ? " border-danger" : ""));

        const top = el("div", "d-flex gap-2 align-items-center");
        top.appendChild(el("span", "small text-body-secondary", "Page"));
        // role select: disable roles already taken by SIBLING regions (can't pick two CONTINUATIONs)
        const used = new Set(g.items.filter(o => o.f !== f).map(o => normRole(o.f.role)).filter(Boolean));
        const roleSel = el("select", "form-select form-select-sm"); roleSel.style.maxWidth = "150px";
        [["", "Single page"], ...ROLES.map(r => [r, ROLE_LABELS[r]])].forEach(([v, t]) => {
            const op = el("option", null, t); op.value = v; op.selected = (normRole(f.role) || "") === v;
            if (v && used.has(v)) op.disabled = true;
            roleSel.appendChild(op);
        });
        roleSel.addEventListener("change", () => { f.role = roleSel.value; f._changed = true; renderFields(); renderOverlay(); });
        top.appendChild(roleSel);
        if (dup) top.appendChild(el("span", "badge text-bg-danger", "duplicate role"));
        top.appendChild(drawButton(idx));
        // delete this region
        const del = el("button", "btn btn-sm btn-outline-danger py-0 ms-auto"); del.type = "button";
        del.title = "remove this page-region"; del.appendChild(el("i", "bi bi-trash"));
        del.addEventListener("click", () => removeRegion(f, idx));
        top.appendChild(del);
        region.appendChild(top);

        region.appendChild(buildColumnEditor(f, idx));
        region.appendChild(zoneRowFor(f, idx));
        return region;
    }

    function nextFreeRole(g) {
        const used = new Set(g.items.map(o => normRole(o.f.role)).filter(Boolean));
        return ROLES.find(r => !used.has(r)) || "LAST";
    }

    // The sibling set for a table region (itself + other TABLE_CELL regions sharing its TargetProperty),
    // shaped like the render groups so nextFreeRole can consume it. Used to suggest a brand-new region's
    // role without colliding with the roles its siblings already hold — page-independent.
    function groupOf(f) {
        const key = (f.targetProperty || "").trim().toLowerCase();
        return { items: state.fields
            .filter(o => isTable(o) && (o.targetProperty || "").trim().toLowerCase() === key)
            .map(o => ({ f: o })) };
    }

    function addPageRegion(g) {
        const role = nextFreeRole(g);
        const nf = { fieldId: 0, targetProperty: g.name, dataType: "STRING", isRequired: false, minConfidence: 0.6,
            sourceType: "TABLE_CELL", zonePage: null, zoneX: null, zoneY: null, zoneW: null, zoneH: null,
            ocrHint: "TEXT", psm: null, role, columns: [], _changed: true };
        state.fields.push(nf);
        g.items.push({ f: nf, idx: state.fields.length - 1 });
        copyColumnsFromFirst(nf);                 // inherit the FIRST region's columns
        state.armed = state.fields.length - 1; state.drawIntent = true;
        renderFields(); renderOverlay();
        setStatus(`Added a ${ROLE_LABELS[role]} region to “${g.name || "table"}” — draw its zone, then Save.`);
    }

    function removeRegion(f, idx) {
        if (f.fieldId > 0 && !state.removedFieldIds.includes(f.fieldId)) state.removedFieldIds.push(f.fieldId);
        state.fields.splice(idx, 1);
        if (state.armed === idx) state.armed = null;
        else if (state.armed > idx) state.armed--;   // indices shifted by the splice
        renderFields(); renderOverlay();
        setStatus("Region removed — Save to apply.");
    }

    function buildColumnEditor(f, fieldIdx) {
        const wrap = el("div", "mt-2 border-top pt-2");
        wrap.appendChild(el("div", "small text-body-secondary mb-1", "Columns — ⚓ marks the anchor (one value per row); drag separators on the table to set widths"));
        f.columns.forEach((c, ci) => {
            const row = el("div", "d-flex gap-1 align-items-center mb-1");
            const cn = el("input", "form-control form-control-sm"); cn.value = c.targetSubProperty; cn.placeholder = "sub-field"; cn.style.maxWidth = "130px";
            cn.addEventListener("input", () => { c.targetSubProperty = cn.value; f._changed = true; });
            const ct = el("select", "form-select form-select-sm"); ct.style.maxWidth = "100px";
            ["STRING", "DECIMAL", "INT", "DATE"].forEach(o => { const op = el("option", null, o); op.selected = c.dataType === o; ct.appendChild(op); });
            ct.addEventListener("change", () => { c.dataType = ct.value; f._changed = true; });
            const anchor = el("div", "form-check form-check-inline m-0");
            const ar = el("input", "form-check-input"); ar.type = "radio"; ar.name = `anchor-${fieldIdx}`; ar.checked = c.isAnchor; ar.title = "anchor column (one value per row)";
            ar.addEventListener("change", () => { f.columns.forEach(x => x.isAnchor = false); c.isAnchor = true; f._changed = true; });
            anchor.append(ar, el("label", "form-check-label small", "⚓"));
            const rm = el("button", "btn btn-sm btn-outline-danger py-0"); rm.type = "button"; rm.appendChild(el("i", "bi bi-x"));
            rm.addEventListener("click", () => removeColumn(f, ci));
            row.append(cn, ct, anchor, rm);
            wrap.appendChild(row);
        });
        const add = el("button", "btn btn-sm btn-outline-secondary py-0", "+ column"); add.type = "button";
        add.addEventListener("click", () => addColumn(f));
        const copy = el("button", "btn btn-sm btn-outline-secondary py-0 ms-1", "copy columns from FIRST"); copy.type = "button";
        copy.addEventListener("click", () => copyColumnsFromFirst(f));
        wrap.append(add, copy);
        return wrap;
    }

    // ---- save -----------------------------------------------------------------
    let saving = false;
    async function save() {
        if (saving) return;                          // ignore double-clicks / concurrent submits
        const payload = {
            templateId: state.templateId,
            mappingMode: state.mappingMode,
            removedFieldIds: state.removedFieldIds.slice(),
            fields: state.fields
                .filter(f => (f.targetProperty || "").trim().length > 0 && f.zoneX != null)
                .map(f => ({
                    fieldId: f.fieldId, targetProperty: f.targetProperty, dataType: f.dataType,
                    isRequired: !!f.isRequired, minConfidence: f.minConfidence || 0.6,
                    sourceType: f.sourceType || "KEY_VALUE",
                    zonePage: f.zonePage || 1, zoneX: f.zoneX, zoneY: f.zoneY, zoneW: f.zoneW, zoneH: f.zoneH,
                    zoneOcrHint: f.ocrHint || "TEXT", zonePsm: f.psm || null, zonePageRole: f.role || null,
                    columns: isTable(f) ? f.columns
                        .filter(c => (c.targetSubProperty || "").trim().length > 0)
                        .map((c, i) => ({
                            columnId: c.columnId || 0, targetSubProperty: c.targetSubProperty, dataType: c.dataType,
                            sortOrder: i, colXStart: c.colXStart, colXEnd: c.colXEnd, isAnchor: !!c.isAnchor,
                            lineSelectMode: c.lineSelectMode || "ALL", lineSelectIndices: c.lineSelectIndices || "",
                            lineJoinSeparator: c.lineJoinSeparator == null ? " " : c.lineJoinSeparator
                        })) : []
                }))
        };
        if (state.mappingMode === "ZONAL" && payload.fields.length === 0) {
            setStatus("Draw at least one zone before saving in ZONAL mode."); return;
        }
        saving = true;
        $("saveBtn").disabled = true;
        setStatus("Saving…");
        try {
            const res = await fetch("/Mapping/ZonesSave", {
                method: "POST",
                headers: { "Content-Type": "application/json", "RequestVerificationToken": token },
                body: JSON.stringify(payload)
            });
            if (res.ok) {
                state.removedFieldIds = [];           // applied — clear the pending deletions
                // Reload so freshly-drawn fields (FieldId=0 client-side) come back with their
                // server-assigned FieldIds; otherwise a second save would re-INSERT them (dup fields).
                setStatus("Saved — reloading…");
                location.reload();
                return;
            }
            let msg = "Save failed (" + res.status + ").";
            try { const j = await res.json(); if (j && j.error) msg = j.error; } catch { /* non-JSON */ }
            setStatus(msg);
        } catch (e) {
            setStatus("Save failed: " + (e && e.message ? e.message : e));
        } finally {
            saving = false;
            $("saveBtn").disabled = false;
        }
    }

    // ---- wiring ---------------------------------------------------------------
    $("modeSelect").value = state.mappingMode;
    $("modeSelect").addEventListener("change", e => { state.mappingMode = e.target.value; });
    $("templateSelect").addEventListener("change", e => { location.href = `/Mapping/Zones?templateId=${e.target.value}`; });
    $("prevPage").addEventListener("click", () => { if (state.page > 1) { state.page--; loadDoc(); } });
    $("nextPage").addEventListener("click", () => { if (state.page < state.pageCount) { state.page++; loadDoc(); } });
    $("filter").addEventListener("input", renderFields);
    $("addField").addEventListener("click", () => {
        state.fields.push({ fieldId: 0, targetProperty: "", dataType: "STRING", isRequired: false, minConfidence: 0.6, sourceType: "KEY_VALUE", zonePage: null, zoneX: null, zoneY: null, zoneW: null, zoneH: null, ocrHint: "TEXT", psm: null, role: "", columns: [], _changed: true });
        state.armed = state.fields.length - 1; state.drawIntent = true; renderFields(); // new field -> ready to draw
    });
    const addTableBtn = $("addTable");
    if (addTableBtn) addTableBtn.addEventListener("click", () => {
        // a new table starts as ONE region; multi-page tables grow via "+ Add page region".
        state.fields.push({ fieldId: 0, targetProperty: "", dataType: "STRING", isRequired: false, minConfidence: 0.6, sourceType: "TABLE_CELL", zonePage: null, zoneX: null, zoneY: null, zoneW: null, zoneH: null, ocrHint: "TEXT", psm: null, role: state.pageCount > 1 ? "FIRST" : "", columns: defaultTableColumns(), _changed: true });
        state.armed = state.fields.length - 1; state.drawIntent = true; renderFields();
    });
    $("saveBtn").addEventListener("click", save);
    OcrOverlay.onImageReady($("pageImg"), renderOverlay);

    loadDoc();
})();
