# Depot System · Client

User interface of the container depot management system (TCIS internship), built from the business document "SNP - HỆ THỐNG QUẢN LÝ DEPOT".
Written in Angular 22 (standalone components, signals, reactive forms). The API lives in [src/](../src/README.md).

| Part | Folder | README |
|:--|:--|:--|
| Backend (API, business rules, database) | `src/` | [src/README.md](../src/README.md) |
| User interface (Angular 22) | `client/` | this file |
| Original template (architecture, built-in features) | repository root | [README.md](../README.md) |

## 1. Requirements

| Software | Version | Notes |
|:--|:--|:--|
| Node.js | 22.22 or later (24 LTS recommended) | Angular CLI 22 does not run on older Node versions |
| npm | bundled with Node | |
| Running backend | | See [src/README.md](../src/README.md); the client calls the API through a proxy |

## 2. First-time setup (once per machine)

Run in `client/` (PowerShell):

```powershell
npm ci
```

`npm ci` installs the exact versions in `package-lock.json`. Use `npm install` only to add or upgrade packages.

## 3. Run

| Task | Command (in `client/`) | Notes |
|:--|:--|:--|
| Development server | `npm start` | `http://localhost:4200`, reloads on every code change |
| API calls | (automatic) | `proxy.conf.json` forwards every `/api` request to `https://localhost:7200` |
| Sign in | | `admin@TechSpherex.dev` / `Admin@123` |

If a screen says "Không kết nối được API" (cannot reach the API), check that Aspire (the backend) is running.

## 4. Test

| Task | Command (in `client/`) | Expected result |
|:--|:--|:--|
| Build | `npx ng build` | No errors, output in `dist/` |
| Unit tests | `npx ng test --watch=false` | 72 passed |

Tests run with Vitest and do not need the backend.

## 5. Folder layout

| Folder | Contents |
|:--|:--|
| `src/app/core/api/` | `api-error.ts`: turns API errors into Vietnamese messages by error code |
| `src/app/core/auth/` | Sign-in, tokens, route guards |
| `src/app/core/depot/` | Shared types and helpers (`depot-models.ts`), yard placement rules (`yard-rules.ts`), API services |
| `src/app/features/` | One folder per screen: `gate-in`, `gate-out`, `yard`, `repairs`, `delivery-orders`, `statistics`, `catalog`, `auth` |
| `src/app/shared/` | Shared components: `search-select` (search and scroll dropdown), `pager`, `yard-map`, `paged-details`, `toast` |
| `src/app/layout/shell/` | Page frame: side menu and content area |

Screens (`app.routes.ts`):

| Group | Paths |
|:--|:--|
| Operations | `/gate-in`, `/gate-out`, `/yard`, `/repairs`, `/delivery-orders` |
| Statistics | `/statistics/inventory`, `/statistics/throughput`, `/statistics/history` |
| Catalog | `/catalog/containers`, `/catalog/line-operators`, `/catalog/customers`, `/catalog/blocks` |

## 6. Conventions

- Labels and messages on screen are in Vietnamese (the users' language). Code comments are in English.
- Every new API error code needs a Vietnamese message in `MESSAGES` in `core/api/api-error.ts`.
- Every paged list uses `LIST_PAGE_SIZE` (15 rows) from `shared/pager/pager.ts`. Search dropdowns load 20 rows at a time for scrolling.
- Client-side checks (yard position, container number, ISO code...) follow the same rules as the API, only to show errors early. The API always has the final say.
- Some screens take URL parameters to open with data already picked, for example `/gate-in?container=`, `/yard?container=`, `/repairs?caseId=` and `/catalog/containers?relations=`.

## 7. Connection to the backend

- The client stores no data of its own. Everything comes from the API, so the screens show the Postgres data of the machine running the backend.
- For data to try things with, create the demo dataset as described in section 7 of [src/README.md](../src/README.md).
- If the API port changes, update `target` in `proxy.conf.json`.
