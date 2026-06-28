"""
BUILD-TIME warm-up + self-check for the Thai PP-OCRv5 /ocr engine.

Goal (Gate #4 — the bake must BUILD CLEAN, or fail LOUD): construct the EXACT engine app.py serves, so
the PP-OCRv5 mobile det/rec + Thai recognizer models download into an image layer (not at first request),
then prove the inference path actually runs and the model cache is complete — NOT a half-finished download.

Hardening:
  * retry-wrap construction        flaky model downloads get a few bounded retries before the build fails
  * smoke predict                  exercise det+rec at BUILD time so any runtime-only crash (e.g. the
                                   paddle-3.0.0 server-model `strides` PIR error) surfaces here, not in prod
  * verify model cache non-empty   a partial/aborted download leaves a near-empty dir; assert real size
Any unrecoverable problem -> non-zero exit -> `docker build` fails with the reason in the log.
"""
import os
import sys
import time

import numpy as np

# Must mirror app.py:_ocr_engine() EXACTLY so the warm-up downloads the SAME files the running engine loads.
DET_MODEL = "PP-OCRv5_mobile_det"
REC_MODEL = "th_PP-OCRv5_mobile_rec"
EXPECTED_MODELS = [DET_MODEL, REC_MODEL]
MIN_MODEL_BYTES = 100_000  # real det/rec model dirs are multi-MB; anything under this == partial download


def build_engine():
    from paddleocr import PaddleOCR
    return PaddleOCR(
        lang="th",
        text_detection_model_name=DET_MODEL,
        text_recognition_model_name=REC_MODEL,
        use_doc_orientation_classify=False,
        use_doc_unwarping=False,
        use_textline_orientation=False,  # mirror app.py: avoid false 180-deg flips on small crops
    )


def fail(msg: str):
    print(f"[warmup] FATAL: {msg}", file=sys.stderr, flush=True)
    sys.exit(1)


def construct_with_retries(attempts: int = 5):
    last = None
    for i in range(1, attempts + 1):
        try:
            eng = build_engine()
            print(f"[warmup] engine constructed on attempt {i}", flush=True)
            return eng
        except Exception as e:  # noqa: BLE001 - download/build can raise many types; retry then fail loud
            last = e
            print(f"[warmup] construct attempt {i}/{attempts} failed: {e}", flush=True)
            time.sleep(min(30, 5 * i))
    fail(f"engine construction failed after {attempts} attempts: {last}")


def smoke_predict(eng):
    # Render a tiny synthetic image with text so detection AND recognition both run (a blank image would
    # exercise det only). The default PIL font needs no external file. We only assert it does not raise.
    from PIL import Image, ImageDraw
    im = Image.new("RGB", (480, 120), "white")
    ImageDraw.Draw(im).text((20, 45), "123 ABC 456", fill="black")
    img = np.array(im)[:, :, ::-1]  # RGB -> BGR
    try:
        list(eng.predict(img))
    except Exception as e:  # noqa: BLE001
        fail(f"smoke predict raised (inference path is broken in this image): {e}")
    print("[warmup] smoke predict ran without error", flush=True)


def _dir_size(path: str) -> int:
    total = 0
    for root, _, files in os.walk(path):
        for f in files:
            try:
                total += os.path.getsize(os.path.join(root, f))
            except OSError:
                pass
    return total


def verify_model_cache():
    roots = [
        os.path.expanduser("~/.paddlex/official_models"),
        os.path.expanduser("~/.paddleocr"),
        os.path.expanduser("~/.paddlex"),
    ]
    found = {}
    for root in roots:
        if os.path.isdir(root):
            for name in os.listdir(root):
                full = os.path.join(root, name)
                if os.path.isdir(full):
                    found[name] = _dir_size(full)
    print(f"[warmup] cached model dirs: {found}", flush=True)

    for model in EXPECTED_MODELS:
        sizes = [size for name, size in found.items() if model in name]
        if not sizes:
            fail(f"expected model '{model}' is not in the cache (download did not happen)")
        if max(sizes) < MIN_MODEL_BYTES:
            fail(f"model '{model}' cache looks partial (largest match = {max(sizes)} bytes)")
    print("[warmup] model cache verified (all expected models present and non-trivially sized)", flush=True)


def main():
    eng = construct_with_retries()
    smoke_predict(eng)
    verify_model_cache()
    print("[warmup] OK — Thai PP-OCRv5 mobile det/rec warmed, smoke predict passed, cache complete.", flush=True)


if __name__ == "__main__":
    main()
