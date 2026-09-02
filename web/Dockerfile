# The front end is plain static files, served as-is (no bundler). The only build-time step is
# stamping a cache-busting build id into index.html so a deploy invalidates every module cache.
FROM nginx:alpine
COPY web/src/ /usr/share/nginx/html/
COPY web/nginx.conf /etc/nginx/conf.d/default.conf
RUN sed -i "s/GITSRV_BUILD_ID/$(date -u +%Y%m%d%H%M%S)/" /usr/share/nginx/html/index.html
