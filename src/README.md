# Depot System · Backend

Backend of the container depot management system (TCIS internship), built from the business document "SNP - HỆ THỐNG QUẢN LÝ DEPOT".
Based on the TechSpherex Clean Architecture template (.NET 10, Aspire 13). The user interface lives in [client/](../client/README.md).

| Part | Folder | README |
|:--|:--|:--|
| Backend (API, business rules, database) | `src/` | this file |
| User interface (Angular 22) | `client/` | [client/README.md](../client/README.md) |
| Original template (architecture, built-in features) | repository root | [README.md](../README.md) |

## 1. Requirements

| Software | Version | Notes |
|:--|:--|:--|
| .NET SDK | 10 | |
| Docker Desktop | recent | Must be running: Aspire starts Postgres, Redis and pgAdmin in Docker |
| dotnet-ef | 10.0.12 or later | Only needed to create new migrations |

## 2. First-time setup (once per machine)

Run in `src/` (PowerShell):

```powershell
dotnet tool install --global dotnet-ef --version 10.0.12
dotnet dev-certs https --trust
dotnet user-secrets set "Parameters:postgres-password" '<postgres password>' --project AppHost
```

- `AppHost.csproj` already has a `UserSecretsId`, so `user-secrets init` is not needed.
- Set the password **before the first run**. The Postgres volume keeps the password it was created with. Changing the user secret later without changing it in the database makes Postgres answer `password authentication failed`.
- The password lives only in the machine's user secrets. Never put it in `appsettings` or commit it.
- Open a new PowerShell after installing `dotnet-ef`. If a newer version is already installed, keep it.

## 3. Run

| Task | Command (in `src/`) | Notes |
|:--|:--|:--|
| Run everything | `dotnet run --project AppHost` | Opens the Aspire dashboard (link printed in the console) |
| API | (started by AppHost) | `https://localhost:7200`, API reference at `https://localhost:7200/scalar` |
| Database | (automatic) | On startup the API applies migrations and creates the admin account |
| Sign in | | `admin@TechSpherex.dev` / `Admin@123` |

Then start the user interface as described in [client/README.md](../client/README.md).

## 4. Test

| Task | Command (in `src/`) | Expected result |
|:--|:--|:--|
| Build | `dotnet build` | No errors |
| Unit and architecture tests | `dotnet test` | 298 passed (9 architecture + 289 unit tests) |
| Code coverage | `powershell -ExecutionPolicy Bypass -File ../scripts/coverage.ps1` | Runs all tests once, report in `../TestResults/CoverageReport/index.html` (scope: `../tests/coverage.runsettings`) |

Unit tests use EF InMemory and a fixed clock (`FixedTimeProvider`), so no real database is needed. The test projects are in `../tests/`.

## 5. Folder layout

| Folder | Contents |
|:--|:--|
| `Domain/` | Entities and business rules, for example `Container`, `ContainerVisit`, `Block`, `LineOperator` (partner), `OperatingAssignment`, `DeliveryOrder`, `RepairCase`, `RepairEstimate` and `ExternalRepairTransfer` |
| `Application/Features/` | One folder per business area, one command or query plus handler per action (hand-written CQRS, FluentValidation) |
| `Infrastructure/Persistence/` | `AppDbContext`, EF configuration, `Migrations/`, `AppDbSeeder` (sample data), `Demo/` (demo dataset) |
| `Api/Endpoints/` | Minimal API groups: containers, container-visits (gate-in, gate-out, moves), blocks, line-operators, operating-assignments, delivery-orders, repair-cases, statistics, demo |
| `AppHost/` | Aspire: Postgres (volume `depot-postgres-data`), Redis, pgAdmin, the API and the demo data commands |
| `ServiceDefaults/` | Shared OpenTelemetry, health checks and service discovery |

## 6. Conventions

- Handlers return `Result` / `Error` and do not throw for business errors. Error codes look like `Area.Name`, for example `ContainerVisit.AlreadyInYard`. The client translates them for display.
- Business dates use Vietnam time (`DepotTime`). Time comes from `TimeProvider`, so tests can fix the clock.
- Code comments are in English.
- To add a migration (in `src/`):

  ```powershell
  dotnet ef migrations add <Name> --project Infrastructure --startup-project Api --output-dir Persistence/Migrations
  ```

## 7. Data and demo dataset

**How data is kept**

- Postgres is the only data store. The named volume `depot-postgres-data` and the `Persistent` container lifetime keep the data when Aspire stops, the machine restarts or the container is recreated.
- Redis is only a cache. Clearing it loses nothing.
- Data is lost only when the volume is deleted on purpose (`docker volume rm depot-postgres-data`) or when the **Reset dữ liệu demo** command runs.
- Each machine has its own data. Machines are not synchronised.

**Demo dataset (140 containers with history)**

| Task | How |
|:--|:--|
| Create / complete | Aspire dashboard → **api** resource → `...` → **Tạo / bổ sung dữ liệu demo** (create / complete demo data), or `POST /api/demo/seed` with an Admin token |
| Reset | **Reset dữ liệu demo** (reset demo data, asks for confirmation), or `POST /api/demo/reset?confirm=reset-demo` with an Admin token |

- The first run stores an **anchor date** and creates 5 operators × 7 types × (2 × 20ft + 2 × 40ft) containers, with several months of history that goes through the same business rules as the screens.
- Running it again only **adds what is missing**, with the same anchor date: no duplicates and no date shifts.
- Reset deletes all depot data (accounts are kept) and creates the demo again with a new anchor date.
- The demo routes exist only in Development when started from AppHost.
- Scenario of each container: [docs/demo-data.md](../docs/demo-data.md).

## 8. Database diagram and SQL

| File | Contents | How to use |
|:--|:--|:--|
| [docs/erd/depot-erd.dbml](../docs/erd/depot-erd.dbml) | ERD of the 16 depot tables, grouped into catalog, yard operations and inspection & repair | Paste into [dbdiagram.io](https://dbdiagram.io), or publish a shareable page with `dbdocs build docs/erd/depot-erd.dbml` (from the repository root, after `npm install -g dbdocs` and `dbdocs login`) |
| [docs/sql/statistics.sql](../docs/sql/statistics.sql) | 4 PostgreSQL queries with the same rules as the statistics screens: in yard per line operator (short / long stay), daily gate-in / gate-out, in-yard ranking by dwell days, history of one container | pgAdmin (Aspire dashboard → **pgadmin** → database `TechSpherex-db` → Query Tool), run one query at a time |

- The ERD is generated from the EF Core model snapshot. After a new migration, update the DBML so the two stay in sync.
- The SQL is read-only: it only uses `SELECT`.
