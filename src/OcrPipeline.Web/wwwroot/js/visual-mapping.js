// Point-and-click mapping (Prompt 4). Vanilla JS, no framework.
// LEFT: page image with clickable OCR boxes. RIGHT: fields + tabs. Click a field to arm it,
// then click a box (KEY_VALUE) or a table header (TABLE_CELL) to bind. Never shows regex.
(function () {
    "use strict";

    const data = JSON.parse(document.getElementById("vm-data").textContent);
    const token = document.querySelector('input[name="__RequestVerificationToken"]').value;

    const state = {
        templateId: data.templateId,
        documentId: data.documentId,
        pageCount: data.pageCount || 0,
        page: 1,
        fields: (data.fields || []).map(normalizeField),
        ocr: { pages: [], blocks: [], tables: [] },
        armed: null,      // { fieldIdx, subIdx | null }
        tab: "fields"
    };

    function normalizeField(f) {
        return {
            fieldId: f.fieldId, targetProperty: f.targetProperty, dataType: f.dataType,
            isRequired: f.isRequired, sourceType: f.sourceType, tableHeader: f.tableHeader,
            rowSelector: f.rowSelector, defaultValue: f.defaultValue,
            minConfidence: f.minConfidence, bindingLabel: f.bindingLabel,
            bindingKey: null, raw: null, norm: null,
            // dirty flags drive the partial save — untouched fields are never sent
            _bindingChanged: false, _subChanged: false, _metaChanged: false,
            subColumns: (f.subColumns || []).map(c => ({ ...c }))
        };
    }

    // ---- DOM helpers ----------------------------------------------------------
    const $ = id => document.getElementById(id);
    function el(tag, cls, txt) { const e = document.createElement(tag); if (cls) e.className = cls; if (txt != null) e.textContent = txt; return e; }
    function setStatus(t) { $("status").textContent = t || ""; }

    // ---- document / image -----------------------------------------------------
    async function loadDoc() {
        if (!state.documentId) { $("stage").classList.add("d-none"); $("noDoc").classList.remove("d-none"); return; }
        $("stage").classList.remove("d-none"); $("noDoc").classList.add("d-none");
        $("pageImg").src = `/api/documents/${state.documentId}/pages/${state.page}/image`;
        try {
            const res = await fetch(`/api/documents/${state.documentId}/ocr`, { headers: { Accept: "application/json" } });
            state.ocr = res.ok ? await res.json() : { pages: [], blocks: [], tables: [] };
        } catch { state.ocr = { pages: [], blocks: [], tables: [] }; }

        // Boxes are positioned in PERCENT, so they're resolution-independent and track the
        // rendered <img> automatically. We just need the stage to have its rendered size, which
        // only exists once the image has loaded — renderOverlay therefore runs now (in case the
        // image is already cached) and again on the image's 'load' event (see wiring below).
        renderOverlay();
        renderRight();
        updatePager();
    }

    function updatePager() {
        $("pageLabel").textContent = `${state.page} / ${state.pageCount || 1}`;
        $("prevPage").disabled = state.page <= 1;
        $("nextPage").disabled = state.page >= state.pageCount;
    }

    function renderOverlay() {
        const specs = [];
        // text blocks (KEY/VALUE/LINE) -> KEY_VALUE binding
        (state.ocr.blocks || []).filter(b => b.page === state.page && b.bbox).forEach(b => {
            specs.push({ bbox: b.bbox, className: "vm-box vm-box-" + (b.type || "LINE").toLowerCase(), title: b.text, onClick: () => bindBlock(b) });
        });
        // table cells with geometry -> bind a sub-field/field via the cell's column header
        (state.ocr.tables || []).filter(t => t.page === state.page).forEach(t => {
            (t.cells || []).filter(c => c.bbox).forEach(c => {
                specs.push({ bbox: c.bbox, className: "vm-box vm-cell" + (c.isHeader ? " vm-cell-header" : ""), title: c.text, onClick: () => bindCell(t, c) });
            });
        });
        OcrOverlay.render($("overlay"), specs);
    }

    // ---- binding --------------------------------------------------------------
    function keyFromText(t) { const i = (t || "").indexOf(":"); return (i > 0 ? t.slice(0, i) : (t || "")).trim(); }

    function bindBlock(block) {
        if (!state.armed) { setStatus("Pick a field first, then click a box."); return; }
        const f = state.fields[state.armed.fieldIdx];
        if (state.armed.subIdx != null) { setStatus("Sub-fields bind to a table column — use the Tables tab."); return; }
        f.sourceType = "KEY_VALUE";
        f.bindingKey = keyFromText(block.text);
        f.bindingLabel = f.bindingKey;
        f.raw = block.text; f.norm = block.normalizedValue;
        f._bindingChanged = true;
        setStatus(`Bound ${f.targetProperty || "field"} → "${f.bindingKey}"`);
        renderRight();
    }

    function headerForColumn(table, colIndex) {
        const h = (table.cells || []).find(c => c.isHeader && c.colIndex === colIndex);
        return h ? (h.text || "").trim() : null;
    }

    // Clicking a cell on the image binds via that cell's column header (same colIndex).
    function bindCell(table, cell) {
        if (!state.armed) { setStatus("Pick a field or sub-field first."); return; }
        const header = headerForColumn(table, cell.colIndex);
        if (!header) { setStatus("No header for this column — use the Tables tab."); return; }
        bindHeader(header);
    }

    function bindHeader(headerText) {
        if (!state.armed) { setStatus("Pick a field or sub-field first."); return; }
        const f = state.fields[state.armed.fieldIdx];
        if (state.armed.subIdx != null) {
            f.subColumns[state.armed.subIdx].tableHeader = headerText;
            f._subChanged = true;
        } else {
            f.sourceType = "TABLE_CELL";
            f.tableHeader = headerText;
            if (!f.rowSelector) f.rowSelector = "ALL";
            f.bindingLabel = headerText;
            f._bindingChanged = true;
        }
        setStatus(`Bound column "${headerText}"`);
        renderRight();
    }

    function arm(fieldIdx, subIdx) {
        state.armed = { fieldIdx, subIdx: (subIdx == null ? null : subIdx) };
        renderRight();
        setStatus("Now click a box on the document (or a Tables/Key-value entry).");
    }

    // ---- right panel rendering ------------------------------------------------
    function isArmed(fi, si) { return state.armed && state.armed.fieldIdx === fi && state.armed.subIdx === (si == null ? null : si); }
    function matchesFilter(text) { const q = $("filter").value.trim().toLowerCase(); return !q || (text || "").toLowerCase().includes(q); }

    function renderRight() {
        ["fields", "kv", "tables", "ocr"].forEach(t => $("pane-" + t).classList.toggle("d-none", t !== state.tab));
        if (state.tab === "fields") renderFields();
        else if (state.tab === "kv") renderKv();
        else if (state.tab === "tables") renderTables();
        else renderOcr();
    }

    function bindingBadge(f) {
        const wrap = el("div", "small mt-1");
        if (f.bindingLabel) {
            wrap.appendChild(el("span", "badge text-bg-success me-1", "bound: " + f.bindingLabel));
            if (f.raw != null) wrap.appendChild(el("span", "badge text-bg-light me-1", "raw: " + f.raw));
            if (f.norm != null) wrap.appendChild(el("span", "badge text-bg-info", "norm: " + f.norm));
        } else {
            wrap.appendChild(el("span", "badge text-bg-secondary", "unbound"));
        }
        return wrap;
    }

    function renderFields() {
        const pane = $("pane-fields"); pane.innerHTML = "";
        state.fields.forEach((f, fi) => {
            if (!matchesFilter(f.targetProperty)) return;
            const card = el("div", "vm-field card mb-2" + (isArmed(fi, null) ? " vm-armed" : ""));
            const body = el("div", "card-body p-2");

            const head = el("div", "d-flex gap-2 align-items-center");
            const name = el("input", "form-control form-control-sm");
            name.value = f.targetProperty; name.placeholder = "target property";
            name.addEventListener("input", () => { f.targetProperty = name.value; f._metaChanged = true; });
            const dt = el("select", "form-select form-select-sm", null); dt.style.maxWidth = "110px";
            ["STRING", "DECIMAL", "DATE", "INT", "BOOL"].forEach(o => { const op = el("option", null, o); op.selected = f.dataType === o; dt.appendChild(op); });
            dt.addEventListener("change", () => { f.dataType = dt.value; f._metaChanged = true; });
            const armBtn = el("button", "btn btn-sm " + (isArmed(fi, null) ? "btn-warning" : "btn-outline-primary"), "Select");
            armBtn.type = "button"; armBtn.addEventListener("click", () => arm(fi, null));
            head.append(name, dt, armBtn);

            const tools = el("div", "d-flex gap-2 align-items-center mt-1");
            tools.appendChild(el("span", "badge text-bg-light", f.sourceType));
            if (f.bindingLabel) {
                const un = el("button", "btn btn-sm btn-outline-danger py-0", ""); un.type = "button";
                un.appendChild(el("i", "bi bi-x"));
                un.addEventListener("click", () => { f.bindingLabel = null; f.bindingKey = null; f.tableHeader = null; f.raw = f.norm = null; f._bindingChanged = true; renderRight(); });
                tools.appendChild(un);
            }

            body.append(head, bindingBadge(f), tools);

            if ((f.subColumns && f.subColumns.length) || f.sourceType === "TABLE_CELL") {
                const sub = el("div", "vm-subs mt-2 ps-2 border-start");
                (f.subColumns || []).forEach((c, si) => {
                    const r = el("div", "d-flex gap-2 align-items-center mb-1" + (isArmed(fi, si) ? " vm-armed-sub" : ""));
                    r.appendChild(el("span", "small fw-semibold", c.targetSubProperty));
                    r.appendChild(el("span", "badge text-bg-light", c.dataType));
                    r.appendChild(el("span", "small text-body-secondary", c.tableHeader ? "← " + c.tableHeader : "(unbound)"));
                    const a = el("button", "btn btn-sm " + (isArmed(fi, si) ? "btn-warning" : "btn-outline-secondary") + " py-0 ms-auto", "Select"); a.type = "button";
                    a.addEventListener("click", () => arm(fi, si));
                    r.appendChild(a);
                    sub.appendChild(r);
                });
                body.appendChild(sub);
            }

            card.appendChild(body);
            pane.appendChild(card);
        });
    }

    function blockList(types) {
        return (state.ocr.blocks || []).filter(b => b.page === state.page && types.includes(b.type) && matchesFilter(b.text));
    }

    function renderKv() {
        const pane = $("pane-kv"); pane.innerHTML = "";
        const list = blockList(["KEY", "VALUE"]);
        if (!list.length) { pane.appendChild(el("div", "text-body-secondary small", "No key/value blocks.")); return; }
        list.forEach(b => {
            const it = el("button", "list-group-item list-group-item-action d-block w-100 text-start small"); it.type = "button";
            it.appendChild(el("span", "badge text-bg-light me-1", b.type));
            it.appendChild(document.createTextNode(b.text || ""));
            if (b.normalizedValue) it.appendChild(el("span", "badge text-bg-info ms-1", b.normalizedValue));
            it.addEventListener("click", () => bindBlock(b));
            pane.appendChild(it);
        });
    }

    function renderTables() {
        const pane = $("pane-tables"); pane.innerHTML = "";
        const tables = (state.ocr.tables || []).filter(t => t.page === state.page);
        if (!tables.length) { pane.appendChild(el("div", "text-body-secondary small", "No tables.")); return; }
        tables.forEach(t => {
            pane.appendChild(el("div", "small fw-semibold mt-2", `Table (${t.rowCount}×${t.columnCount})`));
            const headers = (t.cells || []).filter(c => c.isHeader && matchesFilter(c.text));
            const row = el("div", "d-flex flex-wrap gap-1");
            headers.forEach(h => {
                const chip = el("button", "btn btn-sm btn-outline-primary py-0", h.text || ""); chip.type = "button";
                chip.title = "Click to bind the selected field/sub-field to this column";
                chip.addEventListener("click", () => bindHeader((h.text || "").trim()));
                row.appendChild(chip);
            });
            pane.appendChild(row);
        });
    }

    function renderOcr() {
        const pane = $("pane-ocr"); pane.innerHTML = "";
        const list = blockList(["KEY", "VALUE", "LINE"]);
        if (!list.length) { pane.appendChild(el("div", "text-body-secondary small", "No OCR text.")); return; }
        list.forEach(b => {
            const it = el("button", "list-group-item list-group-item-action d-block w-100 text-start small"); it.type = "button";
            it.appendChild(el("span", "badge text-bg-light me-1", b.type));
            it.appendChild(document.createTextNode(b.text || ""));
            it.addEventListener("click", () => bindBlock(b));
            pane.appendChild(it);
        });
    }

    // ---- save -----------------------------------------------------------------
    async function save() {
        // Partial save: only send fields the user actually changed (bound/unbound/edited or new).
        const isNew = f => !(f.fieldId > 0);
        const payload = {
            templateId: state.templateId,
            fields: state.fields
                .filter(f => (f.targetProperty || "").trim().length > 0)
                .filter(f => isNew(f) || f._bindingChanged || f._subChanged || f._metaChanged)
                .map(f => ({
                    fieldId: f.fieldId, targetProperty: f.targetProperty, dataType: f.dataType,
                    isRequired: !!f.isRequired, sourceType: f.sourceType, bindingKey: f.bindingKey,
                    tableHeader: f.tableHeader, rowSelector: f.rowSelector, defaultValue: f.defaultValue,
                    minConfidence: f.minConfidence || 0.6,
                    bindingChanged: isNew(f) || !!f._bindingChanged,
                    subColumnsChanged: isNew(f) ? (f.subColumns || []).length > 0 : !!f._subChanged,
                    subColumns: (f.subColumns || []).map(c => ({
                        columnId: c.columnId || 0, targetSubProperty: c.targetSubProperty,
                        dataType: c.dataType, tableHeader: c.tableHeader, sortOrder: c.sortOrder || 0
                    }))
                }))
        };
        setStatus("Saving…");
        const res = await fetch("/Mapping/VisualSave", {
            method: "POST",
            headers: { "Content-Type": "application/json", "RequestVerificationToken": token },
            body: JSON.stringify(payload)
        });
        setStatus(res.ok ? "Saved." : "Save failed (" + res.status + ").");
    }

    // ---- wiring ---------------------------------------------------------------
    $("docSelect").addEventListener("change", e => { state.documentId = Number(e.target.value); state.page = 1; loadDoc(); });
    $("templateSelect").addEventListener("change", e => { location.href = `/Mapping/Visual?templateId=${e.target.value}`; });
    $("prevPage").addEventListener("click", () => { if (state.page > 1) { state.page--; loadDoc(); } });
    $("nextPage").addEventListener("click", () => { if (state.page < state.pageCount) { state.page++; loadDoc(); } });
    $("filter").addEventListener("input", renderRight);
    $("addField").addEventListener("click", () => {
        state.fields.push({ fieldId: 0, targetProperty: "", dataType: "STRING", isRequired: false, sourceType: "KEY_VALUE", minConfidence: 0.6, subColumns: [], bindingLabel: null, bindingKey: null, _bindingChanged: true, _subChanged: false, _metaChanged: false });
        state.tab = "fields"; renderRight();
    });
    $("saveBtn").addEventListener("click", save);
    // shared render-timing safety net: re-place boxes once the image has its rendered size
    OcrOverlay.onImageReady($("pageImg"), renderOverlay);
    document.getElementById("tabs").addEventListener("click", e => {
        const a = e.target.closest("[data-tab]"); if (!a) return;
        e.preventDefault();
        document.querySelectorAll("#tabs .nav-link").forEach(n => n.classList.remove("active"));
        a.classList.add("active");
        state.tab = a.dataset.tab; renderRight();
    });

    loadDoc();
})();
