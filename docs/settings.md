# Settings

Settings are changed on the agent's settings page (double-click the tray icon, or open
`http://127.0.0.1:17365`). They are stored in `%AppData%\HddLabelAgent\settings.json`, which can also
be edited by hand while the agent is stopped. A value outside its range is brought into range when
the file is loaded or saved; a file that cannot be read is copied to `settings.json.broken` and the
agent starts with defaults.

```json
{
  "port": 17365,
  "allowedOrigins": ["https://shop.example.com"],
  "activeProfileId": "default",
  "profiles": [
    {
      "id": "default",
      "name": "50 × 25 mm",
      "printer": {"name": "Xprinter XP-365B", "mode": "tspl", "dpi": 203, "density": 8, "speed": 4,
                  "direction": 1, "media": "gap", "gapMm": 2, "offsetXMm": 0, "offsetYMm": 0, "invertBitmap": false},
      "paper": {"widthMm": 50, "heightMm": 25},
      "layout": {"marginMm": 1.5, "fontFamily": "Tahoma", "smoothText": false,
                 "showMarketName": false, "marketName": "", "marketFontPt": 7,
                 "showName": true, "nameFontPt": 8, "nameMaxLines": 2, "nameBold": true,
                 "showPrice": true, "showCurrency": true, "priceFontPt": 9, "priceBold": true,
                 "showCodeText": true, "codeTextFontPt": 6.5,
                 "codeType": "code128", "barcodeHeightMm": 0, "moduleDots": 3,
                 "qrSide": "left", "qrContent": "{barcode}"}
    }
  ]
}
```

## Top level

| Setting | Default | Meaning |
| --- | --- | --- |
| `port` | `17365` | The loopback port the agent listens on (1024 to 65535). Not on the settings page; takes effect after a restart. The web page must use the same port. |
| `allowedOrigins` | `[]` | Sites that may print, as `scheme://host[:port]`. "Allowed sites" on the settings page. A site is added when the person answers "yes" to the agent's question. |
| `activeProfileId` | `"default"` | The profile used when a print request does not name one. Also switchable from the tray icon's menu. |
| `profiles` | one profile | See below. There is always at least one. |

## Profiles

A profile is one combination of printer, paper and label layout. Make one per paper size (and per
printer, if the computer has several) and switch between them when the roll is changed.

A new installation gets one profile for 50 × 25 mm labels, with the first installed printer whose
name looks like a label printer already chosen.

### `printer`

| Setting | On the page | Default | Meaning |
| --- | --- | --- | --- |
| `name` | Printer | | The printer's name in Windows. |
| `mode` | Connection | `tspl` | `tspl`: the label is sent directly in the printer's own language, bypassing the driver. `driver`: it is printed through the Windows driver, for printers that do not understand TSPL. |
| `dpi` | Resolution | `203` | The printer's resolution. The label is drawn at this resolution, so a wrong value prints the label at the wrong size. |
| `density` | Darkness | `8` | 0 to 15. Higher is darker. Direct mode only. |
| `speed` | Speed | `4` | Inches per second. Slower often prints cleaner. Direct mode only. |
| `direction` | Orientation | `1` | `1` or `0`; the other value turns the print by 180°. Direct mode only. |
| `media` | Roll type | `gap` | `gap` for separate labels on a backing strip, `blackmark` for paper with black marks, `continuous` for an uncut roll. Direct mode only. |
| `gapMm` | Gap between labels | `2` | The space between two labels (or the height of the black mark). Direct mode only. |
| `offsetXMm` | Shift right | `0` | Moves the whole print on the paper; negative moves it left. Not shown in the preview. |
| `offsetYMm` | Shift down | `0` | The same, downwards; negative moves it up. |
| `invertBitmap` | (file only) | `false` | For a printer that prints the label as a negative, white on black. |

In `driver` mode darkness, speed and paper sensing are whatever the printer's own driver settings say.

### `paper`

| Setting | On the page | Default | Meaning |
| --- | --- | --- | --- |
| `widthMm` | Width | `50` | Width of one label, 10 to 120 mm. |
| `heightMm` | Height | `25` | Height of one label, 8 to 300 mm. |

The page's **Size** list fills both for common label sizes.

### `layout`

Text sizes are in points. The label is laid out top to bottom: shop name, item name, the bars, the
number under them, the price. With a QR code, the QR sits on one side and the same lines are stacked
beside it.

| Setting | On the page | Default | Meaning |
| --- | --- | --- | --- |
| `marginMm` | Margin | `1.5` | Blank edge kept on all four sides. |
| `fontFamily` | Font | `Tahoma` | Any font installed in Windows. For Arabic, pick a font that has Arabic letters. |
| `smoothText` | Smoother letters | `false` | Off: letters are drawn directly in black and white, crisp at small sizes. On: they are drawn smooth and then reduced to black and white, which suits larger text and some fonts better. Compare both on a real label. |
| `showName` | Show item name | `true` | |
| `nameFontPt` | Name size | `8` | |
| `nameMaxLines` | Name lines | `2` | The most lines a long name may take (1 to 6). |
| `nameBold` | Bold name | `true` | |
| `showPrice` | Show price | `true` | |
| `showCurrency` | Show currency | `true` | Off: only the number is printed. |
| `priceFontPt` | Price size | `9` | |
| `priceBold` | Bold price | `true` | |
| `showMarketName` | Show shop name | `false` | |
| `marketName` | Shop name | | The same on every label of the profile. |
| `marketFontPt` | Shop name size | `7` | |
| `codeType` | Code type | `code128` | `code128`; `auto` (EAN-13 when the barcode is a valid 13-digit EAN, otherwise Code 128); `qr`; `none` for a text-only label. `ean13` is accepted in the file and behaves like `auto` with a warning when the number is not a valid EAN-13. |
| `moduleDots` | Widest bar | `3` | The width of the narrowest bar in printer dots, at most. See "How wide the bars get". |
| `barcodeHeightMm` | Bar height | `0` | `0`: the bars take all the height the text leaves. Otherwise they are at most this tall. |
| `showCodeText` | Show the number under the code | `true` | |
| `codeTextFontPt` | Number size | `6.5` | |
| `qrSide` | QR position | `left` | `left` or `right`. |
| `qrContent` | QR content | `{barcode}` | What the QR holds. `{barcode}`, `{name}` and `{price}` are replaced by the item's values, so `https://shop.example.com/i/{barcode}` makes a link. A print request can override it per label with `qr`. |

## How text is fitted

- A line that is too long is first made smaller, down to 70 % of its size, and only then cut off
  with "…".
- The item name may wrap up to *Name lines*. If that would leave the bars shorter than 4 mm, the
  name gets one line fewer.
- Text that contains Arabic or Hebrew letters is laid out right to left, the rest left to right.
  Every line is centred.

## How wide the bars get

A scanner reads a barcode most reliably when its bars are wide and there is blank space on both
sides. The agent starts from *Widest bar* and goes narrower, one dot at a time, until the barcode
with ten bar-widths of blank space on each side fits the label's width.

At 203 dpi one dot is 0.125 mm. On a 50 mm label:

| Barcode | Bar width used |
| --- | --- |
| Short codes: up to 7 characters, or up to 12 digits | 3 dots (0.38 mm) |
| 13 digits as Code 128 | 2 dots (0.25 mm) |
| 13 digits as EAN-13 | 3 dots (0.38 mm) |
| Much longer codes | 1 dot, with a warning; use a QR code instead |

A valid EAN-13 number is shorter as EAN-13 than as Code 128, so `auto` gives 13-digit product codes
wider bars. A scanner reads the same 13 digits either way.

A QR code is always made as large as the label's height allows.
