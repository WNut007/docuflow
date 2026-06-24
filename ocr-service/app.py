"""
PP-Structure microservice (STEP ① of the Paddle auto-draw plan).

Image in -> table structure + text out, over HTTP, so the .NET app (IOcrEngine) can map it into the
existing OcrExtraction (TextBlocks + Tables/OcrTableCell). The .NET side renders pages to PNG at the
SAME 200-DPI backdrop frame the designer uses, posts them here, and normalizes the pixel bboxes we
return to 0..1 against that frame -> auto-drawn separators land on the backdrop by construction.

ENDPOINTS
  GET  /health            -> {"status":"ok"}
  POST /structure  (file) -> tables (region bbox + per-cell bbox + row/col) + page size, in PIXELS
  POST /ocr        (file) -> words (text + bbox + conf) + page size, in PIXELS

STEP ① NOTE: each response also carries "raw_debug" — the unmodified engine output — so we can SEE the
real PP-Structure shape (cell_bbox vs html grid, coord convention, span fields) and finalize the .NET
mapper against reality, not an assumed contract. raw_debug is dropped once the contract is locked.
"""
import io
import time
from typing import Any

import numpy as np
from fastapi import FastAPI, UploadFile, File, Form
from PIL import Image

app = FastAPI(title="docuflow-ppstructure", version="0.1")

# Lazily-built, long-lived engines (models load once per process; that's why this is a service, not a
# per-call CLI), cached PER LANGUAGE: one recognizer model per lang. "en" is fine for the Latin/German
# Michelin invoice (and is what the Dockerfile bakes); other langs (e.g. "th" for Thai) are built on
# first request — their models download at runtime since only "en" is baked. Default stays "en" so the
# existing English path is unchanged. Mirrors how the .NET side selects Tesseract languages per call.
_structure: dict[str, Any] = {}
_ocr: dict[str, Any] = {}


def _structure_engine(lang: str = "en"):
    if lang not in _structure:
        from paddleocr import PPStructure
        _structure[lang] = PPStructure(table=True, ocr=True, lang=lang, show_log=False)
    return _structure[lang]


def _ocr_engine(lang: str = "en"):
    if lang not in _ocr:
        from paddleocr import PaddleOCR
        _ocr[lang] = PaddleOCR(use_angle_cls=True, lang=lang, show_log=False)
    return _ocr[lang]


def _to_bgr(file_bytes: bytes) -> np.ndarray:
    img = Image.open(io.BytesIO(file_bytes)).convert("RGB")
    return np.array(img)[:, :, ::-1]  # RGB -> BGR for paddle/cv2


def _jsonable(x: Any) -> Any:
    """Recursively coerce numpy/bytes into JSON-safe values so we can dump WHATEVER the engine returns."""
    if isinstance(x, (np.integer,)):
        return int(x)
    if isinstance(x, (np.floating,)):
        return float(x)
    if isinstance(x, np.ndarray):
        # Elide bulky arrays to a shape marker. PP-Structure attaches each region's CROPPED IMAGE as a
        # (H,W,3) uint8 ndarray; dumping its pixels via tolist() balloons raw_debug to ~500 MB/page. We
        # only want the SHAPE of the contract, so keep small arrays (bboxes, scores) and stub the rest.
        if x.size > 256:
            return {"__ndarray__": True, "shape": list(x.shape), "dtype": str(x.dtype)}
        return x.tolist()
    if isinstance(x, (bytes, bytearray)):
        return x.decode("utf-8", "replace")
    if isinstance(x, dict):
        return {str(k): _jsonable(v) for k, v in x.items()}
    if isinstance(x, (list, tuple)):
        return [_jsonable(v) for v in x]
    return x


@app.get("/health")
def health():
    return {"status": "ok"}


@app.post("/structure")
async def structure(file: UploadFile = File(...), lang: str = Form("en")):
    data = await file.read()
    img = _to_bgr(data)
    h, w = img.shape[:2]
    t0 = time.perf_counter()
    result = _structure_engine(lang)(img)  # list of region dicts: type/bbox/res (+ res.html/res.cell_bbox for tables)
    elapsed_ms = round((time.perf_counter() - t0) * 1000, 1)

    # Best-effort normalized contract (refined once we see raw_debug). Region/cell bboxes are PIXELS
    # [x1,y1,x2,y2] in this image's frame; the .NET mapper divides by page_width/height for 0..1.
    tables = []
    for region in (result or []):
        if region.get("type") != "table":
            continue
        res = region.get("res", {}) or {}
        tables.append({
            "region_bbox": region.get("bbox"),
            "html": res.get("html"),
            "cell_bbox": res.get("cell_bbox"),  # list of per-cell pixel boxes (order matches the html grid)
        })

    return {
        "page_width": w,
        "page_height": h,
        "elapsed_ms": elapsed_ms,
        "table_count": len(tables),
        "tables": tables,
        "raw_debug": _jsonable(result),  # STEP ①: the real shape, removed once the mapper is locked
    }


@app.post("/ocr")
async def ocr(file: UploadFile = File(...), lang: str = Form("en")):
    data = await file.read()
    img = _to_bgr(data)
    h, w = img.shape[:2]
    t0 = time.perf_counter()
    result = _ocr_engine(lang).ocr(img, cls=True)
    elapsed_ms = round((time.perf_counter() - t0) * 1000, 1)

    words = []
    for line in (result[0] if result and result[0] else []):
        box, (text, conf) = line[0], line[1]
        xs = [p[0] for p in box]
        ys = [p[1] for p in box]
        words.append({
            "text": text,
            "bbox": [min(xs), min(ys), max(xs), max(ys)],  # pixel x1,y1,x2,y2
            "conf": float(conf),
        })

    return {"page_width": w, "page_height": h, "elapsed_ms": elapsed_ms, "word_count": len(words), "words": words}
