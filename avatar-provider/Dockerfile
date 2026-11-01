# The Avatar Provider: a static character pack the Chat API reads (catalog.json plus one file per avatar).
# The API runs without it; see README.md for the contract.
FROM nginx:alpine

COPY nginx.conf /etc/nginx/conf.d/default.conf
COPY avatars/*.webp /usr/share/nginx/html/

# The catalog is generated from the file names, so adding or removing a file needs no catalog edit.
RUN cd /usr/share/nginx/html \
 && { printf '{"avatars":['; sep=''; \
      for f in $(ls *.webp | sort); do printf '%s{"key":"%s","file":"%s"}' "$sep" "${f%.webp}" "$f"; sep=','; done; \
      printf ']}\n'; } > catalog.json

HEALTHCHECK --interval=10s --timeout=3s --retries=3 CMD wget -qO- http://127.0.0.1/catalog.json >/dev/null || exit 1
