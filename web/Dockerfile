# Phase 0: the front end is plain static files, served as-is. No build step yet — when concat +
# minify is introduced (plan, Phase 0 "no build step beyond concat + minify"), a node build stage
# goes here and the COPY below points at its output instead.
FROM nginx:alpine
COPY web/src/ /usr/share/nginx/html/
COPY web/nginx.conf /etc/nginx/conf.d/default.conf
