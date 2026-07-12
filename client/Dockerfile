FROM node:22-alpine AS build
WORKDIR /app

COPY client/package*.json ./
RUN npm ci

COPY client/ ./
ARG VITE_BASE_PATH=/
ENV VITE_BASE_PATH=$VITE_BASE_PATH
RUN npm run build

FROM httpd:2.4
COPY apache/httpd.conf /usr/local/apache2/conf/httpd.conf
COPY --from=build /app/dist/ /usr/local/apache2/htdocs/
