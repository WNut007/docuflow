# PP-Structure microservice — CPU. Built on the OFFICIAL PaddlePaddle image, which bakes a tested
# glibc/MKL/protobuf toolchain around a working paddle 2.6.2. We tried `python:3.10-slim` + the
# `paddlepaddle==2.6.2` pip wheel first; it segfaults (`free(): invalid pointer`) as soon as paddle
# runs an op (run_check / model init), because the slim base's libs don't match what the wheel
# expects. The official base is paddle's own reference environment, so compute is solid. Models are
# still baked in at build time (below), so the image is self-contained and reproducible offline.
FROM paddlepaddle/paddle:2.6.2

WORKDIR /app
COPY requirements.txt .

# Install service deps, then NORMALIZE opencv to a single package. paddleocr pulls opencv-python (and
# pip sometimes drags in opencv-python-headless too); having more than one opencv overlaps the cv2/
# directory and leaves a half-clobbered module (`cv2 has no attribute INTER_NEAREST`). So purge every
# opencv and reinstall ONLY opencv-contrib-python (full-featured superset), then re-pin numpy < 2
# (opencv-python-headless would otherwise have dragged numpy 2.x in, which paddle 2.6.2 can't use).
RUN pip install --no-cache-dir -r requirements.txt \
 && pip uninstall -y opencv-python opencv-python-headless opencv-contrib-python \
 && pip install --no-cache-dir opencv-contrib-python==4.6.0.66 numpy==1.26.4

# Warm up once at BUILD time so PP-Structure + detection/recognition/table models download into an
# image layer (not at first request). Mirrors app.py's engine construction so the same models are baked.
# Kept BEFORE `COPY app.py` so editing the service code doesn't re-download the models on rebuild.
RUN python -c "from paddleocr import PPStructure, PaddleOCR; \
PPStructure(table=True, ocr=True, lang='en', show_log=False); \
PaddleOCR(use_angle_cls=True, lang='en', show_log=False)"

COPY app.py .

EXPOSE 8080
CMD ["uvicorn", "app:app", "--host", "0.0.0.0", "--port", "8080"]
