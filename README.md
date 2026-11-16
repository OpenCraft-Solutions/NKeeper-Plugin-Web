# NKeeper web plugin

The official **web** plugin for [NKeeper](https://github.com/OpenCraft-Solutions/NKeeper) 2.0: web interfaces through NKeeper, in a tab. Its ID is `web`.

NKeeper's architecture document describes it in §7.19 (with §7.16 to §7.18 for what it builds on). In short:

- **Service types** `http` (port 80, TCP health probe) and `https` (port 443, TLS health probe).
- **Session type** `web`, "Web page", on both, through a jump host too.
- **Host action** `web.open`, "Web page", which needs no credential.
- **Opening a page** (`POST /api/p/web/hosts/{hostId}/sessions`): NKeeper opens the connection, so certificate trust is NKeeper's, and registers the session. The plugin answers with a one-time launch URL on the session's own origin, `nkw-<session ID>.<web domain>`, valid for 2 minutes. The site is served there, so it can never read NKeeper's cookies or call its API. This needs the install's web domain (`setup.sh --web-domain`); without one, the plugin says so.
- **The proxy** rewrites `Host`, `Origin`, `Referer`, redirects and cookies so the site stays on its session origin, and ends a session idle for 30 minutes or whose access ends.
- **The frontend** is the `web` session view: it opens the session, asks the shell to trust a new certificate, and shows the site in a frame.

The plugin references only NKeeper's published packages, never its source: [NKeeper's architecture.md §9](https://github.com/OpenCraft-Solutions/NKeeper/blob/main/docs/architecture.md#9-plugin-repositories-and-releases) describes them, and [plugin-authoring.md](https://github.com/OpenCraft-Solutions/NKeeper/blob/main/docs/plugin-authoring.md) how a plugin is written.

## Layout

```text
backend/       the plugin: NKeeper.Plugins.Web.csproj, nkeeper-plugin.json (the manifest), the C# sources
frontend/      the web session view: a Vite and React project, @nkeeper/plugin-web
tests/         WebTests.cs, run against NKeeper's test server; e2e/ holds the real-browser test
.github/       CI, and the release workflow
```

## Getting the packages

NKeeper publishes these to GitHub Packages, under `OpenCraft-Solutions`:

| Package | Used by | For |
| --- | --- | --- |
| NuGet `NKeeper.Abstractions` | `backend/` | the backend SDK |
| NuGet `NKeeper.Plugin.Build` | `backend/` | building, packing and signing the plugin |
| NuGet `NKeeper.Testing` | `tests/` | the test server and its fixtures |
| npm `@opencraft-solutions/nkeeper-sdk` | `frontend/` | the frontend SDK, imported as `@nkeeper/sdk` |

GitHub Packages needs a token to restore, even for public packages. Create a personal access token that can read packages, and put it in the environment where you build:

```sh
export GITHUB_TOKEN=…        # read by nuget.config
export NODE_AUTH_TOKEN=$GITHUB_TOKEN   # read by frontend/.npmrc
```

The token is never written to the repository. CI uses the workflow's own token.

## Build

You need the .NET SDK in `global.json` and the Node version in `.nvmrc`.

```sh
dotnet build NKeeper-Plugin-Web.slnx
```

This restores, builds the frontend (`npm ci` and `npm run build` in `frontend/`), then packs the plugin into `artifacts/plugins/web-<version>.nkp`, signed with a development key that the build creates in `.dev/keys/` on first use. NKeeper loads a development-signed package only when it runs in `Development` with `Plugins:Directory` set to `artifacts/plugins` and `Plugins:DevelopmentTrustDirectory` to `.dev/keys`.

The frontend alone, from `frontend/`:

```sh
npm ci
npm run lint
npm run typecheck
npm run build
```

### Without the packages on GitHub

To work with packages built locally (`dotnet pack` and `npm pack` in NKeeper's checkout, as its docs say), put them in a folder and restore from it:

```sh
dotnet restore NKeeper-Plugin-Web.slnx -p:RestoreSources="$HOME/local-feed;https://api.nuget.org/v3/index.json"
```

and point the frontend at the package file with `npm install --no-save <path to the .tgz>`. Don't commit either.

## Test

```sh
dotnet test NKeeper-Plugin-Web.slnx
```

`tests/WebTests.cs` starts NKeeper's test server (from `NKeeper.Testing`) with this plugin's built package loaded, and checks that pages open, are proxied on their own origin, and end when access ends. It needs **Docker**, for the PostgreSQL container the test server uses, and builds the plugin first.

The browser test, `tests/e2e/web.mjs`, drives a real Chromium against an **installed** NKeeper that has a web domain, so it doesn't run in CI yet. See [tests/e2e/README.md](tests/e2e/README.md).

## Release

1. Raise `version` in `backend/nkeeper-plugin.json` (and keep `frontend/package.json`'s in step), and merge it to `main`.
2. An owner tags the commit: `git tag v<version> && git push origin v<version>`.

The `Release` workflow then:

1. checks that the tag is `v` plus the manifest's version, and that the commit is on `main`;
2. restores, lints, type-checks, builds and tests, with nothing secret in reach;
3. in the protected `release` environment, which needs an owner's approval for each run, packs `web-<version>.nkp` signed with the official key and publishes a GitHub release with the package and its SHA-256.

The official key's private half, `NKEEPER_RELEASE_KEY` (a PEM), exists only as a secret of the `release` environment. The workflow writes it to memory-backed storage for the one step that signs, and deletes it when that step ends. It is never printed. NKeeper's installer pins a release by its SHA-256 in `deploy/plugins.lock`, which is updated in NKeeper's repository.

## License

[MIT](LICENSE).
