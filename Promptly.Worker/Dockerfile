FROM python:3.11.15-slim-bookworm@sha256:d29f48a31a8b408ed19272ca1e7b10ebae13b240a27e862d3d4217c528e2e0c3 AS dependencies

ENV UV_COMPILE_BYTECODE=1 \
    UV_LINK_MODE=copy

WORKDIR /app

RUN pip install --no-cache-dir uv==0.12.3

COPY Promptly.Worker/pyproject.toml Promptly.Worker/uv.lock ./
RUN uv sync --frozen --no-dev --no-install-project

FROM python:3.11.15-slim-bookworm@sha256:d29f48a31a8b408ed19272ca1e7b10ebae13b240a27e862d3d4217c528e2e0c3 AS runtime

ENV PATH="/app/.venv/bin:$PATH" \
    PYTHONDONTWRITEBYTECODE=1 \
    PYTHONUNBUFFERED=1

RUN groupadd --system --gid 10001 promptly \
    && useradd --system --uid 10001 --gid promptly --home-dir /app promptly

WORKDIR /app

COPY --from=dependencies --chown=promptly:promptly /app/.venv /app/.venv
COPY --chown=promptly:promptly Promptly.Worker/*.py ./
COPY --chown=promptly:promptly Promptly.Worker/routers ./routers

USER promptly

EXPOSE 8000

HEALTHCHECK --interval=10s --timeout=3s --start-period=10s --retries=5 \
    CMD ["python", "-c", "import urllib.request; urllib.request.urlopen('http://127.0.0.1:8000/health/live', timeout=2)"]

CMD ["uvicorn", "main:app", "--host", "0.0.0.0", "--port", "8000", "--no-access-log"]
