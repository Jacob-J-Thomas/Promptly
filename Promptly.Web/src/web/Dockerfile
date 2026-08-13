FROM node:22.22.2-alpine3.23 AS build

WORKDIR /app

COPY package.json package-lock.json ./
RUN npm ci

COPY . .
RUN npm run build

FROM nginxinc/nginx-unprivileged:1.29.4-alpine AS runtime

COPY nginx.conf /etc/nginx/conf.d/default.conf
COPY --from=build /app/dist /usr/share/nginx/html

EXPOSE 8080

HEALTHCHECK --interval=5s --timeout=3s --start-period=5s --retries=12 \
  CMD wget --quiet --output-document=- http://127.0.0.1:8080/healthz >/dev/null || exit 1
