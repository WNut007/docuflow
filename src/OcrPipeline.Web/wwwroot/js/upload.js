// Upload page: a drag-and-drop convenience layer over a normal multipart form. The real
// <input type=file required> + the select stay the source of truth (the server re-validates),
// so this only improves UX: drop/browse a file, show its name+size, gate the submit until a file
// and a template are picked, and spin the button during the inline pipeline run.
(function () {
    "use strict";

    const $ = id => document.getElementById(id);
    const form = $("uploadForm");
    if (!form) return;

    const dropZone = $("dropZone"), fileInput = $("fileInput");
    const dzPrompt = $("dzPrompt"), dzFile = $("dzFile"), fileName = $("fileName"), fileSize = $("fileSize");
    const clearFile = $("clearFile"), templateSelect = $("templateSelect");
    const submitBtn = $("submitBtn"), spinner = $("submitSpinner"), icon = $("submitIcon"), label = $("submitLabel");

    const fmtBytes = n => {
        if (!n) return "";
        const u = ["B", "KB", "MB", "GB"]; let i = 0; let v = n;
        while (v >= 1024 && i < u.length - 1) { v /= 1024; i++; }
        return `(${v.toFixed(v < 10 && i > 0 ? 1 : 0)} ${u[i]})`;
    };

    // ---- selected-file display -------------------------------------------------
    function showSelectedFile() {
        const f = fileInput.files && fileInput.files[0];
        if (f) {
            fileName.textContent = f.name;
            fileSize.textContent = fmtBytes(f.size);
            dzPrompt.classList.add("d-none");
            dzFile.classList.remove("d-none");
        } else {
            dzPrompt.classList.remove("d-none");
            dzFile.classList.add("d-none");
        }
        validate();
    }
    function clearSelectedFile() {
        fileInput.value = "";
        showSelectedFile();
    }

    // ---- validity gate: enable submit only with a file AND a template ----------
    function validate() {
        const ok = fileInput.files.length > 0 && !!templateSelect.value;
        submitBtn.disabled = !ok;
    }

    // ---- drag & drop -----------------------------------------------------------
    ["dragenter", "dragover"].forEach(ev => dropZone.addEventListener(ev, e => {
        e.preventDefault(); dropZone.classList.add("upload-dragover");
    }));
    ["dragleave", "dragend"].forEach(ev => dropZone.addEventListener(ev, e => {
        if (ev === "dragleave" && dropZone.contains(e.relatedTarget)) return;  // ignore moves between children
        dropZone.classList.remove("upload-dragover");
    }));
    dropZone.addEventListener("drop", e => {
        e.preventDefault();
        dropZone.classList.remove("upload-dragover");
        if (e.dataTransfer && e.dataTransfer.files.length) {
            fileInput.files = e.dataTransfer.files;   // assign FileList -> native input (keeps it the source of truth)
            showSelectedFile();
        }
    });

    // click / keyboard opens the native picker (but not when removing the file)
    dropZone.addEventListener("click", e => { if (!e.target.closest("#clearFile")) fileInput.click(); });
    dropZone.addEventListener("keydown", e => {
        if (e.key === "Enter" || e.key === " ") { e.preventDefault(); fileInput.click(); }
    });
    clearFile.addEventListener("click", e => { e.stopPropagation(); clearSelectedFile(); });

    fileInput.addEventListener("change", showSelectedFile);
    templateSelect.addEventListener("change", validate);

    // ---- submit: spinner + disable + double-submit guard -----------------------
    let submitting = false;
    form.addEventListener("submit", e => {
        if (submitting || submitBtn.disabled) { e.preventDefault(); return; }  // guard the double-click / invalid case
        submitting = true;
        spinner.classList.remove("d-none");
        icon.classList.add("d-none");
        label.textContent = "Processing… (OCR can take a while)";
        // disable AFTER this handler returns so the submit still fires with the button enabled
        setTimeout(() => { submitBtn.disabled = true; }, 0);
    });

    // back/forward bfcache restore must not leave the button stuck spinning/disabled
    window.addEventListener("pageshow", e => {
        if (!e.persisted) return;
        submitting = false;
        spinner.classList.add("d-none");
        icon.classList.remove("d-none");
        label.textContent = "Upload & process";
        validate();
    });

    showSelectedFile();   // initial state (covers a browser-restored file selection)
})();
