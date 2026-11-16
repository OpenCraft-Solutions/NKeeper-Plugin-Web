# Real-browser test

`web.mjs` tests the web plugin in a real Chromium against an **installed** NKeeper: it signs in, creates a host whose HTTPS service is the NKeeper server itself, opens its web page, trusts its self-signed certificate in NKeeper's dialog, sees the page in the frame on its own session origin, closes it and deletes the host.

It needs:

- an NKeeper install with this plugin installed, and a web domain: `setup.sh --web-domain web.localhost`. Chromium resolves any `*.localhost` name to loopback, so the session origins need no DNS. For a real domain without DNS yet, set `RESOLVE_WEB_DOMAIN=web.example.test=192.0.2.10`, which sends `*.web.example.test` to that address in this browser only;
- Node (see `.nvmrc`) and Playwright's Chromium.

```sh
cd tests/e2e
npm install
npx playwright install chromium
BASE=https://localhost ADMIN_PASSWORD=… TARGET_ADDRESS=127.0.0.1 node web.mjs
```

| Variable | Default | |
| --- | --- | --- |
| `BASE` | `https://localhost` | the NKeeper to test |
| `ADMIN_USER` | `admin` | an administrator |
| `ADMIN_PASSWORD` | | required |
| `TARGET_ADDRESS` | `127.0.0.1` | the address the host's HTTPS service points at |
| `SCREENSHOTS` | this folder | where it writes screenshots (ignored by git) |

Pass the password in the environment as above rather than on a command line that is logged. The test deletes the host it creates.
