# Paddle OCR microservice — CPU, OCR-ONLY (Thai PP-OCRv5). On the OFFICIAL paddle 3.0.0 image, which bakes
# a tested glibc/MKL/protobuf toolchain around a working paddle 3.0.0 (same anti-segfault strategy as the
# prior 2.6.2 base — the plain pip wheel on a slim base segfaults as soon as paddle runs an op). Models are
# baked in at build time (below), so the image is self-contained and reproducible offline.
FROM paddlepaddle/paddle:3.0.0

WORKDIR /app
COPY requirements.txt .

# paddleocr 3.x (+ the paddlex[ocr] extra, kept from the proven pin — it installs CODE, not models; with
# the PPStructureV3 warm-up gone no table models download, so the image stays lean). No opencv/numpy
# normalize step is needed on the 3.x stack (unlike 2.6.2) — the resolver lands a working opencv + numpy-2.
RUN pip install --no-cache-dir --timeout 180 --retries 8 -r requirements.txt

# Warm up ONLY the /ocr engine (Thai PP-OCRv5 mobile det/rec) at BUILD time so its models download into an
# image layer (not at first request). warmup.py mirrors app.py:_ocr_engine() exactly, retries flaky
# downloads, runs a smoke predict (so any runtime-only crash surfaces in the BUILD), and verifies the model
# cache is complete — failing the build LOUD on a partial download. No PPStructureV3 here: this OCR-only
# branch never constructs the server-model table pipeline, so the paddle-3.0.0 `strides` crash can't fire.
# Kept BEFORE `COPY app.py` so editing service code doesn't re-download the models on rebuild.
ENV PADDLE_PDX_DISABLE_MODEL_SOURCE_CHECK=True
COPY warmup.py .
RUN python warmup.py

COPY app.py .

EXPOSE 8080
CMD ["uvicorn", "app:app", "--host", "0.0.0.0", "--port", "8080"]
