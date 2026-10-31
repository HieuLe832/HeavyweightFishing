# Media (5.0) — design

Date: 2026-10-31

## Goal

Users upload files, attach them to messages, and use an uploaded image as their own or a group's avatar.
The API validates what it stores and decides who may read each file. No schema change: `media` (D7),
`message_attachments`, `users.avatar_media_id` and `conversations.avatar_media_id` already exist, and 4.1
"attachment ownership and readiness" is already drawn.

**Out of scope:** deleting bytes from the object store when a message is deleted (a cleanup job), thumbnails,
video and audio, resumable or presigned uploads, virus scanning, original file names (the ERD has no column),
moderation of media (`rejected` is reserved for 6.0).

## Decisions

**Upload goes through the API.** `POST /media` is a multipart request the API reads, checks and writes to the
object store itself. A presigned direct upload would let the client claim any type and size; checking the bytes
needs them in the API anyway. Files are capped at 10 MiB, so holding one in memory is acceptable.

**The type comes from the bytes, never from the client.** `MediaSniffer` reads the signature and header and returns the
MIME type and, for images, the size. Allowed: PNG, JPEG, GIF, WebP, PDF, and plain UTF-8 text. There is no SVG or HTML type: markup is just
text, so it is stored as `text/plain` and served as a download with `nosniff`, so it is never rendered or run. The file is not
decoded or re-encoded, so a header can lie about the pixels; that is why downloads are served as below.

**Downloads go through the API too.** `GET /media/{id}` checks access, then streams the bytes with
`X-Content-Type-Options: nosniff`, `Content-Security-Policy: default-src 'none'; sandbox`, `Cache-Control: private`, and
`Content-Disposition: inline` for images only (everything else is `attachment`). Access is decided per request, so a
removed member or a deleted message ends access at once, which a long-lived presigned URL could not do.

**A missing file and a forbidden file look the same** (`media_not_found`), so ids cannot be probed.

**Object store behind an interface.** `IObjectStore` has an in-memory implementation (tests, bare dev) and an S3 one
(`ObjectStore:Endpoint` set; RustFS in Compose). Keys are `media/{yyyy}/{MM}/{guid}`. Without an endpoint the API warns
at start that files are kept in memory.

## Components

```
Chat.Api/Files/
  MediaOptions.cs      MaxBytes, upload rate limit
  IObjectStore.cs      Put / Get / Delete; InMemoryObjectStore; S3ObjectStore (AWSSDK.S3)
  MediaSniffer.cs      type and image size from the bytes
  MediaService.cs      upload, open (authorise + stream), avatars
  MediaEndpoints.cs    POST /media, GET /media/{id}, PUT/DELETE avatar routes
```

### Upload

`POST /media`, `multipart/form-data` with one part named `file`, authenticated. 20 uploads per minute per user
(`rate_limited`). Steps: size ≤ `MaxBytes` (`file_too_large`, 413) → sniff (`unsupported_media_type`, 415) → SHA-256 →
write to the store → insert the `media` row as `ready` with the detected type, size, sha256 and image size. If the insert
fails the object is deleted. Response `201 { id, mimeType, sizeBytes, width?, height? }`.

### Who may read a file

A `ready` file is readable by, in order: its uploader; an active member of a conversation that has a non-deleted
message with it attached (blocks do not matter, as for history); anyone who may see a user whose avatar it is
(a user who blocked the viewer looks missing); an active member of a conversation whose avatar it is.

### Attachments

New hub method `SendMedia(conversationId, clientMsgId, caption?, mediaIds, replyToId?)` returns the message, like
`SendMessage`, and goes through the same checks (session, member, blocks, rate limit, dedupe, `seq` lock, events, push).
`mediaIds`: 1–10 distinct ids, each `ready` and uploaded by the sender, else `invalid_attachment`
(400; the same answer for a missing file and someone else's, so ids cannot be probed). The caption is optional,
trimmed, at most 4000 characters. The message has `type = media`; the caption is its `body`; `message_attachments`
rows keep the order in `position`. The same file may be attached again later by its uploader.

`MessageDto` gains `attachments: [{ mediaId, mimeType, sizeBytes, width?, height? }]`, in order, empty for text and
system messages and for deleted messages (a tombstone shows nothing). History, events and acks all carry it. Editing a media
message edits the caption. Reports (6.1) will copy the attachment list into their evidence.

### Avatars

`PUT /users/me/avatar` `{ mediaId }` and `DELETE /users/me/avatar`; `PUT /conversations/{id}/avatar` `{ mediaId }` and
`DELETE` for owners and admins of a group (`not_a_group` for a direct chat). The file must be `ready`, an image, and the
caller's own upload. User profiles and conversation summaries gain `avatarMediaId`. Group avatar changes write no system
notice. Replacing an avatar leaves the old file in the store.

## Errors

New: `file_too_large` (413), `unsupported_media_type` (415), `media_not_found` (404), `invalid_attachment` (400),
`validation_failed` for a missing part or a bad count of ids; an avatar that is not an image is `unsupported_media_type`, and one
that is missing or not the caller's is `media_not_found`.

## Testing

- sniffer: each allowed type with sizes; refusals (executable, empty, binary, text with NULs, broken image headers);
  SVG and HTML come out as `text/plain`, never as an image or page;
- upload: row and stored bytes, sha256, size limit, rate limit, 401, missing part;
- download: headers for image / pdf / text; access matrix (uploader, attached member, left member, deleted message,
  stranger, avatar of a visible user, avatar of a user who blocked the viewer, group avatar members only);
- `SendMedia`: ack and events carry attachments in order; caption; dedupe; invalid ids (missing, other's, pending,
  rejected, duplicates, zero, eleven); blocked and non-member; history shows them; delete empties them and ends access;
  edit changes the caption; push preview says `Sent an attachment`;
- avatars: set / replace / clear, non-image, other's file, group manager rules, `avatarMediaId` in profiles;
- the S3 store against a real S3-compatible container.

## Docs to update with the code

README "API: media" and the messaging section (new method, `attachments`); DFD / ERD check; pitfalls; CLAUDE.md.
