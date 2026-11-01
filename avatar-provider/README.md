# Avatar Provider

An optional character avatar pack for the Chat API, run as its own container. The API stores only the key a user
picked and fetches the image from here when asked; while this container is down avatars show as initials, and they
come back when it does. Nothing the users chose is lost.

## Contract

Any server that keeps this contract can replace this one:

- `GET /catalog.json` → `{ "avatars": [{ "key": "07-curly-blond", "file": "07-curly-blond.webp" }] }`
- `GET /{file}` → the image bytes (PNG, JPEG, GIF or WebP, at most 2 MiB).

Keys match `^[a-z0-9-]{1,64}$`; files are plain names. The API skips entries that do not.

## This implementation

`nginx:alpine` serving `avatars/*.webp`. `catalog.json` is generated from the file names when the image is built: a
file `07-curly-blond.webp` becomes key `07-curly-blond`. Add or remove files and rebuild.

```bash
docker compose up -d --build avatar-provider   # http://127.0.0.1:18090/catalog.json
```

The API finds it through `AvatarProvider:BaseUrl` (Development: `http://localhost:18090`).

The art belongs to its creators (see `avatars/SOURCES.md`); this pack is for local development only.
