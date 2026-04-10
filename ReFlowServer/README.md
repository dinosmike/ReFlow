# ReFlow server endpoints (PHP)

This folder contains minimal PHP endpoints for ReFlow speed testing.

## Endpoints

- `GET /reflow/ping.php`
  - Returns `204 No Content`.
- `GET /reflow/download.php?seconds=15` (or `?bytes=...`)
  - Streams bytes with `Cache-Control: no-store`.
- `POST /reflow/upload.php`
  - Reads request body and returns `200 OK`.

## Deploy

Copy the `reflow/` folder into your website (for example: `https://example.com/reflow/`).

In the ReFlow app settings, set the **Server URL** to that folder URL, e.g.:

- `https://example.com/reflow/`

