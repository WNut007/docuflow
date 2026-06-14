// Accuracy review (Prompt 5). Vanilla JS. Reuses the working overlay pattern from
// visual-mapping.js: PERCENT-positioned boxes over the rendered <img>, re-rendered on image
// 'load' (no naturalWidth pixel math). Boxes are coloured by the server-computed confidence band.
(function () {
    "use strict";

    const data = JSON.parse(document.getElementById("review-data").textContent);
    const token = document.querySelector('input[name="__RequestVerificationToken"]').value;
    const state = { documentId: data.documentId, page: 1, ocr: { pages: [], blocks: [], tables: [] }, values: data.values || [] };

    const $ = id => document.getElementById(id);
    const setStatus = t => { $("status").textContent = t || ""; };

    const blockBox = id => { const b = (state.ocr.blocks || []).find(x => x.id === id && x.bbox); return b ? b.bbox : null; };
    const inputFor = rvid => document.querySelector('.review-input[data-rvid="' + rvid + '"]');

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
            if (!v.blockId) return;                 // no source box (regex/constant/table array)
            const bb = blockBox(v.blockId);
            if (!bb) return;
            const inp = inputFor(v.resultValueId);
            const label = inp ? inp.value : (v.normalizedValue || "");
            const band = v.bandClass || "secondary";
            specs.push({
                bbox: bb,
                className: "vm-box vm-band-" + band,
                title: v.targetProperty + ": " + label,
                build: d => {
                    const chip = document.createElement("span");
                    chip.className = "vm-chip text-bg-" + band;
                    chip.textContent = label;
                    d.appendChild(chip);
                },
                onClick: () => { if (inp) { inp.focus(); inp.scrollIntoView({ block: "center" }); } }
            });
        });
        OcrOverlay.render($("overlay"), specs);
    }

    async function save() {
        const corrections = [];
        document.querySelectorAll(".review-input").forEach(inp => {
            if (inp.value !== (inp.dataset.original || ""))
                corrections.push({ resultValueId: Number(inp.dataset.rvid), normalizedValue: inp.value });
        });
        setStatus("Saving…");
        const res = await fetch("/Documents/ReviewSave", {
            method: "POST",
            headers: { "Content-Type": "application/json", "RequestVerificationToken": token },
            body: JSON.stringify({ documentId: state.documentId, corrections })
        });
        if (!res.ok) { setStatus("Save failed (" + res.status + ")."); return; }
        const j = await res.json();
        document.querySelectorAll(".review-input").forEach(inp => { inp.dataset.original = inp.value; });
        setStatus(`Saved ${corrections.length} correction(s). Status: ${j.status}.`);
    }

    $("saveBtn").addEventListener("click", save);
    $("valueList").addEventListener("input", e => { if (e.target.classList.contains("review-input")) renderOverlay(); });
    OcrOverlay.onImageReady($("pageImg"), renderOverlay); // shared render-timing safety net
    load();
})();
