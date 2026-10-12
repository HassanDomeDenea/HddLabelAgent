# HTTP API

The agent listens on `http://127.0.0.1:17365` (the port is the `port` value in `settings.json`).
`http://localhost:17365` works as well.

- Bodies and answers are JSON. A request with a body must say `Content-Type: application/json`.
- A request body may be at most 1 MB.
- Property names are camelCase and are not case-sensitive when read.

## Who may call it

| Caller | Result |
| --- | --- |
| The agent's own settings page | Allowed. |
| A program on the computer that is not a browser (no `Origin` header) | Allowed. |
| A web page whose site is in `allowedOrigins` | Allowed. |
| A web page from any other site | The agent asks the person at the computer. The request waits for the answer. "Yes" adds the site to `allowedOrigins`; "no" gives `403` with `code: "origin_refused"`, and further requests from that site are refused without asking until the agent restarts. |

`GET /api/ping` is the exception: any site may call it and nobody is asked.

Preflight (`OPTIONS`) requests are always answered, including the
`Access-Control-Allow-Private-Network` header; the real request is the one that is checked.

## Errors

A request that fails answers with a status of 400 or above and this body:

```json
{"ok": false, "error": "The printer \"Xprinter XP-365B\" is offline. Check that it is switched on and its cable is connected.", "code": null}
```

`error` is written for the person printing and can be shown as it is. Messages are in English.
Two failures have an empty body instead: `400`, and the `403` for a foreign `Host`.

| Status | When |
| --- | --- |
| 400 | The body is missing or is not valid JSON. |
| 403 | The site is not allowed (`code` is `"origin_refused"` when the person said no), or the request's `Host` is not the loopback address. |
| 415 | The body is not JSON. |
| 422 | The request was understood but cannot be done: no printer chosen, printer offline or not installed, barcode too long or not printable, nothing to print, too many labels. |
| 500 | Something unexpected; details are in `agent.log`. |

## Label data

One label is described by an *item*. Every field is optional; a line whose data is missing is
left off the label.

| Field | Meaning |
| --- | --- |
| `barcode` | The text to encode. Code 128 takes English letters, digits and symbols only. |
| `name` | The item's name. Any language. |
| `price` | The number alone, already formatted, for example `"2,500"`. |
| `priceWithCurrency` | The number with its currency the way it should be read, for example `"2,500 دينار"` or `"$12.5"`. |
| `currency` | Used only when `priceWithCurrency` is missing: the label then shows `price`, a space and `currency`. |
| `qr` | What the QR code holds for this label, instead of the profile's `qrContent`. |
| `copies` | How many of this label. Default 1. |

Which price is printed depends on the profile: with *Show currency* on it is `priceWithCurrency`
(or `price` + `currency`), with it off it is `price`.

## Which settings are used

`/api/preview`, `/api/print` and `/api/feed` choose a profile like this:

1. `profile` in the request: a whole profile object, used as it is and not saved. Anything left out
   takes its default value. The settings page uses this to try unsaved changes.
2. Otherwise `profileId`: a saved profile. An id that does not exist gives `422`.
3. Otherwise the active profile.

## Endpoints

### `GET /api/ping`

Says that the agent is running. Open to every site.

```json
{"app": "HddLabelAgent", "version": "1.0.0"}
```

### `GET /api/status`

What a print would use right now.

```json
{
  "app": "HddLabelAgent",
  "version": "1.0.0",
  "port": 17365,
  "activeProfile": {
    "id": "default", "name": "50 × 25 mm", "widthMm": 50, "heightMm": 25,
    "printer": "Xprinter XP-365B", "mode": "tspl", "codeType": "code128"
  },
  "printer": {
    "name": "Xprinter XP-365B", "found": true, "offline": false, "jobs": 0,
    "driver": "Xprinter XP-365B", "port": "USB002", "isDefault": false
  },
  "profiles": [
    {"id": "default", "name": "50 × 25 mm", "widthMm": 50, "heightMm": 25,
     "printer": "Xprinter XP-365B", "mode": "tspl", "codeType": "code128"}
  ]
}
```

`printer` is `null` when the active profile has no printer chosen. `found` is `false` when the
chosen printer is not installed any more.

### `GET /api/printers`

The printers installed in Windows, each in the same shape as `printer` above.

### `GET /api/fonts`

The names of the installed fonts, as an array of strings.

### `GET /api/settings`

The whole of `settings.json`. See [settings.md](settings.md).

### `PUT /api/settings`

Replaces the settings with the body. Values out of range are brought into range rather than refused.

```json
{"ok": true, "settings": {"…": "the settings as saved"}, "restartNeeded": false}
```

`restartNeeded` is `true` when the port was changed: the agent keeps listening on the old port until
it is restarted.

### `POST /api/preview`

Draws one label and returns it as a picture. Nothing is printed.

Request (every part is optional, but the body must be at least `{}`; without `item` a built-in
sample is drawn):

```json
{"item": {"barcode": "6281234567895", "name": "Paracetamol 500 mg", "price": "2,500", "currency": "IQD"}}
```

Answer:

```json
{
  "ok": true,
  "png": "data:image/png;base64,iVBORw0KGgo…",
  "width": 400,
  "height": 200,
  "dpi": 203,
  "codeType": "code128",
  "moduleDots": 2,
  "warnings": []
}
```

- `png` is exactly what the printer will get, one pixel per printer dot. The profile's
  *Shift right / Shift down* is not shown; it only moves the picture on the paper.
- `codeType` is the code that was actually drawn: `code128`, `ean13`, `qr` or `none`.
- `moduleDots` is the width of the narrowest bar (or of one QR cell) in printer dots; `0` when the
  label has no code.
- `warnings` lists things that will print but may disappoint, such as bars that had to be made
  1 dot wide.

### `POST /api/print`

Prints labels.

```json
{
  "items": [
    {"barcode": "6281234567895", "name": "Paracetamol 500 mg", "price": "2,500", "priceWithCurrency": "2,500 IQD", "copies": 2},
    {"barcode": "-0123", "name": "Gauze roll", "price": "500", "priceWithCurrency": "500 IQD"}
  ]
}
```

A single label may be sent as `"item": {…}` instead of `items`.

```json
{"ok": true, "printer": "Xprinter XP-365B", "labels": 3, "warnings": []}
```

- At most 500 labels in one request, counting copies.
- All the labels of a request go to the printer as one job. Requests are printed one after the
  other, never interleaved.
- `ok: true` means Windows accepted the job for the printer. It does not prove that paper came out:
  a printer that is out of labels still accepts jobs.

### `POST /api/feed`

Feeds one blank label. Direct (TSPL) mode only. Send `{}` for the active profile, or name one with
`profileId` or `profile`.

```json
{"ok": true}
```

## Trying it from a terminal

```powershell
curl.exe http://127.0.0.1:17365/api/status

curl.exe -X POST http://127.0.0.1:17365/api/print -H "Content-Type: application/json" --data-binary "@label.json"
```

Send Arabic text as UTF-8; putting the JSON in a file, as above, avoids the console's own encoding.
