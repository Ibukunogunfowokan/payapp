# The app so far

A record of everything done to connect PayApp (a .NET 8 app that takes payments through Paystack) to **Ductape**, written so questions can be asked about it later. Covers 2026-10-20 to 2026-10-21.

No secrets are in this file. Keys, passwords and URLs with credentials live in `.env`, in Ductape secrets, or in `~/.claude.json`.

---

## 1. What Ductape is (as understood)

Ductape is a platform for reusable backend building blocks. You describe things once in its workspace, then call them by name from code.

| Concept | Meaning |
|---|---|
| **Workspace** | Your account area ("Spiral gateway"). |
| **Product** | Top-level boundary for one app or service. Ours is "Payments and queues". |
| **Environment** | A stage of a product, with its own URLs and secrets. Ours: `snd` (sandbox) and `prd` (production). |
| **App** | A description of an external API: base URLs per environment plus **actions** (endpoints). Paystack is an app. |
| **Action** | One endpoint of an app (method, path, fields). |
| **Resource** | Managed infrastructure attached to a product: databases, message brokers, storage and so on. |
| **Secret** | An encrypted value, referenced as `$Secret{NAME}`. Listing shows keys only. |

### CLI vs SDK

| | CLI / MCP (`ductape ...`) | SDK (`Ductape.Sdk` NuGet package, inside PayApp) |
|---|---|---|
| Purpose | Setup and admin: create apps, register databases and brokers, secrets, link a project | Runtime: the app calls Paystack, queries the database, tests the broker |
| Auth | `ductape login` session (admin) | `DUCTAPE_ACCESS_KEY` from `.env` |
| Where it runs | Requests go to Ductape's cloud | In the PayApp process |
| Reaches `localhost`? | **No.** `ductape db connect` failed with `Failed to connect to PostgreSQL` because the request goes through `api.ductape.app/proxy/v1/db-proxy`. | **Yes.** The SDK contains Npgsql, RabbitMQ and other drivers and connects directly from your machine. |

Consequence: local Docker Postgres and RabbitMQ are registered with `localhost` URLs. The app can use them through the SDK, but CLI/MCP/Workbench database operations on them will not work.

---

## 2. Identifiers

- Workspace: `Spiral gateway`, id `6aa51d19b1b2cea1a273458b`, tag `spiral-gateway`
- Product: `spiral-gateway:payments-and-queues` (id `6ad896f74d0c937d194436d9`)
- Environments: `snd` (sandbox), `prd` (production). Every resource needs an entry for both.
- Paystack app tag: `payapp-paystack` (the tag `paystack` is already taken by someone else)
- Database tag: `payapp-db` (Postgres)
- Broker tag: `payapp-rabbitmq` (RabbitMQ)

---

## 3. What was registered in Ductape

### Paystack app (`payapp-paystack`)
- Environments `snd` and `prd`, both with base URL `https://api.paystack.co`. Paystack has no separate sandbox host; test vs live is decided by the key.
- Connected to the product with `products apps connect --env-map snd:snd --env-map prd:prd`.
- Three actions, defined from files in `ductape/actions/payapp-paystack/`. Ductape rewrote the tags with underscores:
  - `list_banks`: `GET bank` (read-only, used for connection tests)
  - `initialize_transaction`: `POST transaction/initialize`
  - `verify_transaction`: `GET transaction/verify/{reference}`
- Each action declares an `Authorization` header field.
- Auth connection (`ductape/apps/payapp-paystack/connection.json`), applied to both environments:
  ```json
  "credentials": { "headers:Authorization": "Bearer $Secret{PAYSTACK_SECRET_KEY}" }
  ```
  This only holds a **reference**. Ductape swaps in the real secret when an action runs.
- The secret `PAYSTACK_SECRET_KEY` (described as "paystack test secret key") was created by the user with `ductape secrets create`.
- Both `snd` and `prd` currently use the **test** key. Swap `prd` before going live.

### Postgres (`payapp-db`)
- Type `postgresql`, `snd` and `prd` both pointing at the local Docker container (`localhost:5432`, database `payapp`).
- Ductape stored each URL as a secret and replaced it in the record: `DB_PAYMENTS_AND_QUEUES_PAYAPP_DB_SND_URL` and `..._PRD_URL`.
- The URL format is `postgresql://user:password@host:port/database`, with the password URL-encoded.

### RabbitMQ (`payapp-rabbitmq`)
- Type `rabbitmq`, `snd` and `prd` both pointing at the local Docker container (`amqp://user:password@localhost:5672`). The config is stored encrypted.
- The first registration attempt was blocked by Claude Code's permission classifier, and it succeeded when retried.
- No topics, producers or consumers are defined. PayApp's email queue still uses its own `RabbitMQ.Client` code, not Ductape.

### Other
- The paystack app that was created by hand earlier (`spiral-gateway:paystack-integration`, empty draft) was deleted by the user in Workbench. `ductape apps delete` fails with `Workspace ID is required` (looks like a CLI bug, reproduced through both the CLI and the MCP).

---

## 4. Files added or changed in this repo

| Path | What |
|---|---|
| `PayApp.Api/PayApp.Api.csproj` | `Ductape.Sdk` 0.2.3 package reference (net8.0 only) |
| `PayApp.Api/Integrations/DuctapeService.cs` | Singleton: logs in once with `DUCTAPE_ACCESS_KEY`, then has the check methods below |
| `PayApp.Api/Integrations/DuctapeStartupCheck.cs` | Background service that runs the checks once at startup. A failing check logs an error and never stops the app. |
| `PayApp.Api/Program.cs` | `using PayApp.Api.Integrations;` plus `AddSingleton<DuctapeService>()` and `AddHostedService<DuctapeStartupCheck>()` |
| `.gitignore` | Added `ductape/databases/` and `ductape/brokers/` (they contain local passwords) |
| `ductape/` | Registration files (below) and `config.json` linking this folder to the product |

`ductape/` layout:
- `config.json`: workspace, product and env (`snd`) link
- `apps/payapp-paystack.app.json`: the app definition
- `apps/payapp-paystack/connection.json`: the auth reference
- `actions/payapp-paystack/*.action.json`: the three actions
- `databases/payapp-db.databases.json`: **contains the local DB password (gitignored)**
- `brokers/payapp-rabbitmq.brokers.json`: **contains the local RabbitMQ password (gitignored)**

The two gitignored files are only local copies. The registrations already live in Ductape, so they can be deleted.

### Settings in `.env`
- `DUCTAPE_ACCESS_KEY` (required, from Workbench -> Tokens)
- Optional, with defaults: `DUCTAPE_PRODUCT` (`spiral-gateway:payments-and-queues`), `DUCTAPE_ENV` (`snd`), `DUCTAPE_DATABASE` (`payapp-db`), `DUCTAPE_BROKER` (`payapp-rabbitmq`)

### The checks (in `DuctapeService`)
| Method | What it does |
|---|---|
| `GetClientAsync` | `Ductape.Sdk.Ductape.CreateAsync(accessKey, product, env)` then `ReadyAsync()` |
| `CheckDatabaseAsync` | `Database.TestConnection({product, env, database})` |
| `QueryPaymentsAsync` | `Database.Count` and `Database.Query` (limit 1) on the `payments` table |
| `CheckPaystackAsync` | `Actions.RunAsync` for app `payapp-paystack`, action `list_banks` |
| `CheckRabbitMqAsync` | `Brokers.TestConnectionAsync({product, env, broker})` |

---

## 5. Test results (2026-10-21, env `snd`)

| Check | Result |
|---|---|
| SDK login and ready | 5.8 to 6.9 s (one-time, runs in the background) |
| Postgres `TestConnection` | `connected=True`, about 23 ms, latency 20 ms |
| `payments` table | 3 rows counted in 51 ms; sample query returned keys `data, count, fields, rows, total` |
| Paystack `list_banks` | Returned "Banks retrieved" in about 1.7 s, so the app, the secret, the header and the action work together |
| RabbitMQ `TestConnection` | `{"connected":true,"latency":8}` in 9 ms |

Not tested: `initialize_transaction` and `verify_transaction` (initialize would create a real transaction), and publishing or consuming any message through Ductape.

---

## 6. How to run and re-check

1. Start the Docker containers: `docker compose up -d`
2. Make sure `DUCTAPE_ACCESS_KEY` is in `.env`.
3. `cd PayApp.Api && dotnet run`
4. Watch the log for lines starting `Ductape:`. A failure looks like `Ductape: <check> check failed: ...` and names the stage.

Useful CLI commands (run from the repo root; most accept `--help`):
```
ductape whoami
ductape products apps list --product spiral-gateway:payments-and-queues --json
ductape products apps actions list --product spiral-gateway:payments-and-queues --app payapp-paystack --json
ductape resources databases list --json
ductape resources events list --json        # message brokers
ductape secrets list --json                 # keys only, never values
```

Webhooks: ngrok is used for Paystack's callback URL (Paystack needs a public HTTPS address). That is unrelated to Ductape.

---

## 7. Tooling set up on this machine

- `ductape` and `ductape-mcp` are installed (`/opt/homebrew/bin`), and the CLI is logged in.
- The Ductape MCP server is registered with Claude Code at **local scope** in `~/.claude.json` (only for this project, not in the repo) with the publishable key and workspace id. Remove it with `claude mcp remove ductape`, inspect it with `claude mcp get ductape`.
- The publishable key was pasted in a chat transcript, so rotate it in Workbench -> Tokens if that matters.
- Admin work goes through `ductape_cli` (needs the login). `ductape_execute` is runtime only.

---

## 8. Gotchas and lessons

- Tag `paystack` is taken globally. Use `payapp-paystack`.
- Ductape rewrites action tags (`list-banks` became `list_banks`). Use the returned tags.
- The database create call rejected `description` fields with `"description" is not allowed`. They were removed.
- The `ductape apps delete` command is broken (see section 3).
- The `ductape resources ... create` commands print `undefined` on success. Verify with a `list`.
- NuGet README is empty and the GitHub repo had no usable docs. The package ships an XML doc file (`lib/net8.0/Ductape.Sdk.xml`) which was used to find the real C# API.
- Ductape's cloud cannot reach `localhost`. Only the SDK in your process can.
- `Database.Connect/TestConnection` take `{ product, env, database }`; `Connection(...)` itself opens nothing.
- **Mistake made:** while cleaning up a test run I used `pkill -f "PayApp.Api"`, which stopped the user's own running API instance. Later test copies were run on port 5199 and stopped by process id only.

---

## 9. Open items / ideas

- Replace `PaystackClient` with Ductape actions (`initialize_transaction`, `verify_transaction`) and test them with a safe test-mode transaction.
- Model Paystack webhooks as app webhook events (verify `x-paystack-signature` with HMAC-SHA512 before processing, deduplicate by reference).
- Define RabbitMQ topics and move the email queue onto Ductape, or leave it as is.
- Move `PAYSTACK_SECRET_KEY`, DB password and similar values from `.env` into Ductape secrets and read them with `secrets.fetch`.
- Swap the `prd` environment's database, broker and Paystack key for real production values (currently they mirror the local and test ones).
- Remove or hide the startup checks once no longer needed: they make a real (read-only) Paystack call on every start.
- Delete the two gitignored files that contain local passwords.
- Consider Ductape's local stack (`ductape profiles use local`, `install`, `start`, `status`) if the CLI/MCP ever need to reach local containers. Untested.
- Report the `apps delete` bug to Ductape support.
