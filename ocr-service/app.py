"""
Paddle OCR microservice — PP-OCRv5 (PaddleOCR 3.x: PaddleOCR.predict), THAI + English.

Image in -> text out, over HTTP, so the .NET app (PaddleRegionOcrEngine / IOcrEngine) can map it into
the existing OcrExtraction (TextBlocks). The .NET side renders each drawn zone to PNG at the SAME 200-DPI
backdrop frame the designer uses, posts it here, and normalizes the pixel bboxes we return to 0..1 against
that frame.

OCR-ONLY by design: this branch reaches Paddle through /ocr with MANUAL column drawing in the zonal
designer. The PP-Structure (/structure) auto-detect path is intentionally NOT shipped here — it stays
parked on paddle-v5-upgrade — so the v5 table-structure incompatibility never triggers and the build has
no PPStructureV3 server-model surface to crash on.

ENDPOINTS
  GET  /health         -> {"status":"ok"}
  POST /ocr     (file) -> words (text + bbox + conf) + page size, in PIXELS

WIRE CONTRACT (the engine-agnostic seam PaddleRegionOcrEngine consumes) IS UNCHANGED:
  /ocr -> words[] = {text, bbox=[x1,y1,x2,y2] pixels, conf}
so PaddleRegionOcrEngine needs ZERO .NET changes across the v5 + Thai upgrade.
"""
import io
import time
from typing import Any

import numpy as np
from fastapi import FastAPI, UploadFile, File
from PIL import Image

app = FastAPI(title="docuflow-paddle-ocr", version="0.3")

# Lazily-built, long-lived engine (models load once per process; that's why this is a service, not a
# per-call CLI). Built for THAI + English invoices.
_ocr = None


def _ocr_engine():
    global _ocr
    if _ocr is None:
        from paddleocr import PaddleOCR
        # Thai PP-OCRv5 MOBILE det/rec: the th_PP-OCRv5_mobile_rec recognizer reads Thai + Latin digits
        # accurately (proven in the Phase-1 accuracy spike on doc_rt.pdf), and the MOBILE det/rec models
        # sidestep a paddle-3.0.0 inference crash — the *server* PP-OCRv5 det/rec predictors raise
        # `(InvalidArgument) Type of attribute: strides is not right` (a PIR/new-IR incompatibility) when
        # created standalone. Detection is language-agnostic; recognition is the Thai-specific model.
        _ocr = PaddleOCR(
            lang="th",
            text_detection_model_name="PP-OCRv5_mobile_det",
            text_recognition_model_name="th_PP-OCRv5_mobile_rec",
            use_doc_orientation_classify=False,
            use_doc_unwarping=False,
            use_textline_orientation=True,
        )
    return _ocr


def _to_bgr(file_bytes: bytes) -> np.ndarray:
    img = Image.open(io.BytesIO(file_bytes)).convert("RGB")
    return np.array(img)[:, :, ::-1]  # RGB -> BGR for paddle/cv2


def _unwrap(res: Any) -> dict:
    """paddleocr 3.x result objects expose .json as {"res": {...}}; return the inner dict."""
    d = res.json if hasattr(res, "json") else res
    if isinstance(d, dict) and isinstance(d.get("res"), dict):
        return d["res"]
    return d if isinstance(d, dict) else {}


@app.get("/health")
def health():
    return {"status": "ok"}


@app.post("/ocr")
async def ocr(file: UploadFile = File(...)):
    data = await file.read()
    img = _to_bgr(data)
    h, w = img.shape[:2]
    t0 = time.perf_counter()
    output = list(_ocr_engine().predict(img))  # PaddleOCR.predict (v5; .ocr() is legacy)
    elapsed_ms = round((time.perf_counter() - t0) * 1000, 1)

    # v5: result carries parallel rec_texts / rec_scores / rec_boxes (axis-aligned PIXEL [x1,y1,x2,y2]).
    words = []
    for res in output:
        doc = _unwrap(res)
        texts = doc.get("rec_texts") or []
        scores = doc.get("rec_scores") or []
        boxes = doc.get("rec_boxes")
        if boxes is None or len(boxes) == 0:
            boxes = doc.get("rec_polys") or []
        for i, text in enumerate(texts):
            box = boxes[i] if i < len(boxes) else None
            if box is None:
                continue
            pts = np.asarray(box, dtype=float).reshape(-1)
            if pts.size == 4:                       # rec_boxes: [x1,y1,x2,y2]
                x1, y1, x2, y2 = pts.tolist()
            elif pts.size >= 8:                     # rec_polys: 4 points -> bound
                xs, ys = pts[0::2], pts[1::2]
                x1, y1, x2, y2 = float(xs.min()), float(ys.min()), float(xs.max()), float(ys.max())
            else:
                continue
            words.append({
                "text": text,
                "bbox": [x1, y1, x2, y2],
                "conf": float(scores[i]) if i < len(scores) else 0.0,
            })

    return {"page_width": w, "page_height": h, "elapsed_ms": elapsed_ms, "word_count": len(words), "words": words}
