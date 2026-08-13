ARG RUNTIME_IMAGE=nginxinc/nginx-unprivileged:1.29.4-alpine@sha256:a6c4f61f456b85b8fdf7ec7ab28cc3e299440e6fb4a9dea520e5fd8fd440025e
FROM ${RUNTIME_IMAGE}

COPY nginx.conf /etc/nginx/conf.d/default.conf

USER 101:101
EXPOSE 5000 8080

HEALTHCHECK --interval=5s --timeout=3s --start-period=5s --retries=12 \
  CMD wget --quiet --output-document=- http://127.0.0.1:8080/healthz >/dev/null \
    && wget --quiet --output-document=- http://127.0.0.1:5000/health >/dev/null \
    || exit 1
