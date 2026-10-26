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
| Node.js | 22.22.3 or later in 22.x, or 24.15 or later (24 LTS recommended) | Angular CLI 22 refuses older versions, including 24.0 - 24.14 |
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
| Unit tests | `npx ng test --watch=false` | 81 passed |

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

## 6. Screens

Routes are in `app.routes.ts`. Every screen except sign-in needs a signed-in user.

| Path | Screen | What it does |
|:--|:--|:--|
| `/gate-in` | Nhập bãi | Pick a container (chip: can come in, already in the yard, no line operator, back from outside repair), check its operating relation, choose a slot on the yard map and record its condition. A damaged container opens an inspection case. |
| `/gate-out` | Xuất bãi | Tab **Cấp rỗng**: pick a delivery order, then one of its containers. The dropdown lists only the containers of the order (line operator, type and size still needed); a blocked one links to the screen that resolves it. Tab **Chuyển sửa ngoài**: send a container to an outside repair facility. |
| `/yard` | Vị trí & đảo chuyển | Yard map per block (Bay / Row / Tier), move a container to another slot or block, move history |
| `/repairs` | Giám định & sửa chữa | Repair cases by step: inspection, estimate versions with the payer of each line, approval, repair, acceptance, outside repair |
| `/delivery-orders` | Lệnh giao | Orders of a line operator with lines by type + size + quantity, expiry date, cancel |
| `/statistics/inventory` | Tồn bãi | Containers in the yard per line operator, short stay (0-9 days) and long stay (10 days or more) |
| `/statistics/throughput` | Sản lượng nhập/xuất | Gate-in and gate-out per day and line operator, gate-out split into empty release and outside repair |
| `/statistics/history` | Lịch sử container | One row per visit; the filters live in the URL so links and reload keep them |
| `/catalog/containers` | Container | Containers (number and ISO code checks) and their operating relations |
| `/catalog/line-operators` | Đối tác | Partners: owner, line operator or both |
| `/catalog/customers` | Khách hàng | Customers |
| `/catalog/blocks` | Block | Blocks with size limits and allowed types, virtual blocks |

**Links between screens** (URL parameters that open a screen with data already picked):

| URL | Opens |
|:--|:--|
| `/gate-in?container=MSCU1234566` | Gate-in with the container picked |
| `/gate-out?orderId=…` / `?tab=repair` | Gate-out with the order picked / on the outside repair tab |
| `/yard?container=…` | Yard with the container picked (for example the one on top of a container to release) |
| `/repairs?caseId=…&backOrderId=…` | The repair case, with a link back to the delivery order |
| `/catalog/containers?relations=…&returnTo=gate-in` | Operating relations of a container, with a link back to gate-in |
| `/statistics/history?…` | History with its filters |

## 7. Conventions

- Labels and messages on screen are in Vietnamese (the users' language). Code comments are in English.
- Every new API error code needs a Vietnamese message in `MESSAGES` in `core/api/api-error.ts`.
- Every paged list uses `LIST_PAGE_SIZE` (15 rows) from `shared/pager/pager.ts`. Search dropdowns load 20 rows at a time for scrolling.
- Client-side checks (yard position, container number, ISO code...) follow the same rules as the API, only to show errors early. The API always has the final say.
- Screens that link to each other use URL parameters (section 6), so reload and the back button keep the selection.

## 8. Connection to the backend

- The client stores no data of its own. Everything comes from the API, so the screens show the Postgres data of the machine running the backend.
- For data to try things with, create the demo dataset as described in section 7 of [src/README.md](../src/README.md).
- If the API port changes, update `target` in `proxy.conf.json`.
