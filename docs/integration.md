# Printing from your web app

The agent runs on the computer that has the label printer. Your page, opened in a browser on that
same computer, calls it on the loopback address. Nothing goes through your server, so the labels come
out of the printer next to whoever pressed the button, whichever computer that is.

## A complete client

```js
const AGENT = 'http://127.0.0.1:17365';

async function call(method, path, body, timeoutMs) {
    const controller = new AbortController();
    const timer = setTimeout(() => controller.abort(), timeoutMs);
    try {
        const response = await fetch(AGENT + path, {
            method,
            headers: body ? {'Content-Type': 'application/json'} : {},
            body: body ? JSON.stringify(body) : undefined,
            signal: controller.signal,
        });
        const data = await response.json().catch(() => ({}));
        return {reached: true, ok: response.ok, data};
    } catch (e) {
        return {reached: false};
    } finally {
        clearTimeout(timer);
    }
}

/** Is the agent running on this computer? Never asks the user anything. */
export async function agentRunning() {
    return (await call('GET', '/api/ping', null, 1500)).reached;
}

/**
 * Prints labels. Resolves to {agent: false} when there is no agent here,
 * otherwise to {agent: true, ok, error, warnings, printer}.
 */
export async function printLabels(items) {
    if (!await agentRunning()) return {agent: false};
    const result = await call('POST', '/api/print', {items}, 120000);
    if (!result.reached) return {agent: true, ok: false, error: 'The label printer program stopped answering.'};
    return {
        agent: true,
        ok: result.ok,
        error: result.data.error,
        warnings: result.data.warnings || [],
        printer: result.data.printer,
    };
}
```

Using it:

```js
const result = await printLabels([{
    barcode: product.barcode,
    name: product.name,
    price: '2,500',
    priceWithCurrency: '2,500 IQD',
    copies: 1,
}]);

if (!result.agent) {
    // No agent on this computer: say so, or fall back to whatever you printed with before.
} else if (!result.ok) {
    showError(result.error);          // written for the person printing, show it as it is
} else if (result.warnings.length) {
    showWarning(result.warnings.join('\n'));
}
```

## Things to know

**Send data, not layout.** The item carries the barcode, the name and the price. Whether the price is
shown, how big the name is, which code is printed and on what paper is decided by the agent's
settings on that computer. See [api.md](api.md#label-data) for the item's fields.

**Format the price yourself.** The agent prints `price` and `priceWithCurrency` as they arrive. Send
both: the first is used when the profile hides the currency, the second when it shows it.

**The first print asks for permission.** When a site calls the agent for the first time, the agent
shows a question on the computer's screen and holds the request until it is answered. That is why the
print call above waits up to two minutes while the ping waits only a moment. After "yes" the site is
remembered. After "no" the call fails with status 403 and `code: "origin_refused"`, and so do later
ones until the agent is restarted or the site is added on the settings page.

**Check for the agent with `/api/ping`.** It is open to every site and never asks the user anything,
so it is safe to call when a page loads, for example to decide whether to show the print button.
`/api/status` tells more (which printer, which paper, whether the printer is offline) but counts as
a real request: from a site that is not allowed yet it brings up the permission question.

**Use `fetch` with a JSON body.** The agent refuses writes that are not `application/json`. If your
app's HTTP library adds its own headers to every request (a CSRF token, for example), the browser
will block the call, because the agent only lets the `Content-Type` header through. Plain `fetch`
avoids that.

**Link to the settings page.** `http://127.0.0.1:17365/` is the agent's settings page; a link to it
from your app's own settings saves people from looking for the tray icon.

## Sites served over HTTPS

The agent speaks plain HTTP on the loopback address. Browsers make an exception to their
mixed-content rule for that address, so an HTTPS page can normally call `http://127.0.0.1`; this is
the usual way local helper programs are reached. Two things to be aware of:

- Use `127.0.0.1`, not `localhost`: the exception is not applied to the name in every browser.
- Some browsers ask the user before a public site may talk to a program on the same computer
  (Chrome calls it local network access). The user has to allow it once.

Calling the agent from an HTTPS site has not been tested yet. If it fails in some browser, please
open an issue with the browser's console message.

## A preview in your own page

`POST /api/preview` returns the label as a PNG data URL, drawn with the agent's current settings:

```js
const response = await fetch('http://127.0.0.1:17365/api/preview', {
    method: 'POST',
    headers: {'Content-Type': 'application/json'},
    body: JSON.stringify({item: {barcode: '6281234567895', name: 'Paracetamol 500 mg', price: '2,500', currency: 'IQD'}}),
});
const label = await response.json();
document.querySelector('img#label').src = label.png;
```

## Printing from something that is not a browser

A script or a desktop program on the same computer can call the same endpoints. It sends no `Origin`
header, so it is not asked for permission.

```powershell
curl.exe -X POST http://127.0.0.1:17365/api/print -H "Content-Type: application/json" --data-binary "@label.json"
```
