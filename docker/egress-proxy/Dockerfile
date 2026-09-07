ARG GO_BUILDER_IMAGE=golang:1.26.8-alpine3.23@sha256:33ce311e5eecedee48ec1b84419c1306e9fbd71009f0d5c3f2a6904b579c1ecc
ARG RUNTIME_IMAGE=gcr.io/distroless/static-debian12:nonroot@sha256:1b7b9f0f0e0a1d2155f531db587cc48ec26aaf97ab64364225f5bf18a054e66a

FROM ${GO_BUILDER_IMAGE} AS build

ARG SMOKESCREEN_COMMIT=131fba29ce1e3c2b9d78f359a2fb633703d5013d
ARG SMOKESCREEN_ARCHIVE_SHA256=c79a7ee3c82516b027ca0c2fd771bd203bdac79b2354e40ae5ac41b7e4e57850
ARG GOVULNCHECK_VERSION=v1.6.0

WORKDIR /src
RUN wget \
      --output-document=/tmp/smokescreen.tar.gz \
      "https://codeload.github.com/stripe/smokescreen/tar.gz/${SMOKESCREEN_COMMIT}" \
    && echo "${SMOKESCREEN_ARCHIVE_SHA256}  /tmp/smokescreen.tar.gz" | sha256sum -c - \
    && tar --extract --gzip --file /tmp/smokescreen.tar.gz --strip-components=1 \
    && rm /tmp/smokescreen.tar.gz

COPY go.mod go.sum /src/
COPY healthcheck/main.go /healthcheck/main.go

ENV GOFLAGS=-mod=vendor
RUN go mod verify \
    && go mod vendor \
    && go test -buildvcs=false -tags=nointegration ./... \
    && CGO_ENABLED=0 go build -buildvcs=false -trimpath \
      -ldflags="-s -w -X github.com/carlmjohnson/versioninfo.Revision=${SMOKESCREEN_COMMIT}" \
      -o /out/smokescreen . \
    && CGO_ENABLED=0 go build -buildvcs=false -trimpath \
      -ldflags="-s -w" \
      -o /out/smokescreen-check-config ./cmd/check_config \
    && CGO_ENABLED=0 go build -buildvcs=false -trimpath \
      -ldflags="-s -w" \
      -o /out/smokescreen-healthcheck /healthcheck/main.go

# Scan the binaries that will actually ship, including their embedded Go
# standard library and resolved module versions. The scanner is version-pinned;
# CI still receives the current Go vulnerability database at build time.
RUN GOFLAGS= GOBIN=/tmp/govulncheck \
      go install "golang.org/x/vuln/cmd/govulncheck@${GOVULNCHECK_VERSION}" \
    && /tmp/govulncheck/govulncheck -mode=binary /out/smokescreen \
    && /tmp/govulncheck/govulncheck -mode=binary /out/smokescreen-check-config \
    && /tmp/govulncheck/govulncheck -mode=binary /out/smokescreen-healthcheck

COPY smokescreen.yaml egress-acl.yaml /etc/smokescreen/
RUN /out/smokescreen-check-config /etc/smokescreen/smokescreen.yaml

FROM ${RUNTIME_IMAGE} AS runtime

LABEL org.opencontainers.image.source="https://github.com/stripe/smokescreen" \
      org.opencontainers.image.revision="131fba29ce1e3c2b9d78f359a2fb633703d5013d" \
      org.opencontainers.image.title="Promptly outbound HTTP proxy"

COPY --from=build /out/smokescreen /smokescreen
COPY --from=build /out/smokescreen-check-config /smokescreen-check-config
COPY --from=build /out/smokescreen-healthcheck /smokescreen-healthcheck
COPY --from=build /etc/smokescreen /etc/smokescreen

USER 65532:65532
EXPOSE 4750

ENTRYPOINT ["/smokescreen"]
CMD ["--config-file=/etc/smokescreen/smokescreen.yaml"]
