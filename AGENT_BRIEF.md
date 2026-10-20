# Agent Brief: PayApp

Read this whole file before doing anything in this project. It is the shared context between me (the developer) and any coding agent helping me.

## Who you are working with

I'm a .NET developer who used MySQL before and is new to Postgres, message queues, cloud hosting and Docker. **This is a learning project.** Build things with me step by step, and explain the *why* briefly as you go (one or two sentences per new concept, not essays). Don't skip ahead to later phases, and don't silently add tools or libraries we haven't agreed on. If a step needs me to do something outside the code (create an account, copy a key, click a setting), stop and tell me exactly what to do.

## What we're building

A small web app where users:

1. Sign up, confirm their email and log in.
2. Click a **"Pay ₦100"** button that pays into my Paystack account (**test mode only**).
3. Receive emails: verification, payment receipts and promotional emails.

Promotional emails go through a **message queue** so I learn how queues work.

The plan is to build everything by hand first, then move parts to **Ductape** (a backend-components platform, beta) to learn what it replaces.

## Architecture

```
Internet (browser, Paystack webhooks)
        │
┌───────▼──────────── GCP e2-micro VM (free tier), Docker Compose ─────┐
│  PayApp.Api     ASP.NET Core: auth, pay button, Paystack webhook      │──► Paystack (test mode)
│  PayApp.Worker  .NET Worker Service: consumes email-queue             │◄── RabbitMQ (Docker locally, CloudAMQP in cloud)
│  Postgres       users, payments, email records                        │
└───────────────────────────────────────────────────────────────────────┘
        Worker ──► Mailpit (local fake inbox) / Brevo or Resend (cloud)
```

- The API and the Worker are **separate processes** in one solution, sharing a class library. They never call each other directly; the API publishes to RabbitMQ and the Worker consumes.
- This is **not microservices**: one codebase, one database. Do not split it further.
- Local development and the VM use the same `docker-compose.yml`.

## Tech stack (agreed)

| Concern | Choice |
|---|---|
| Runtime | .NET 8 |
| API | ASP.NET Core |
| Background work | .NET Worker Service with a `BackgroundService`, registered via `AddHostedService` |
| Database | Postgres via EF Core with the Npgsql provider, migrations for all schema changes |
| Auth | ASP.NET Core Identity (never hand-roll password hashing) |
| Queue | RabbitMQ using `RabbitMQ.Client`, manual acks, dead-letter queue |
| Email (local) | Mailpit over SMTP, inbox at `localhost:8025` |
| Email (cloud) | Brevo or Resend (not chosen yet) |
| Payments | Paystack, test mode, test cards |
| Hosting | One GCP e2-micro VM, Docker Compose, Caddy for HTTPS |
| Frontend | **Not decided yet**: Razor Pages vs a separate React app. Ask me before building UI. |

## Solution layout

```
PayApp.sln
├── PayApp.Api/      dotnet new webapi    HTTP endpoints
├── PayApp.Worker/   dotnet new worker    queue consumer, no HTTP
└── PayApp.Core/     dotnet new classlib  entities, DbContext, IEmailSender, shared logic
```

## Database (first draft)

Identity owns `AspNetUsers`; we extend the user class with `FullName`, `MarketingOptIn` (bool, default false) and `CreatedAt`.

| Table | Key columns |
|---|---|
| `payments` | `id uuid`, `user_id`, `reference` (unique), `amount_kobo bigint`, `currency` (default NGN), `status` (pending / success / failed / abandoned), `paystack_txn_id`, `paid_at`, `created_at` |
| `payment_webhook_events` | `id`, `reference`, `event_type`, `payload jsonb`, `received_at` |
| `email_campaigns` | `id`, `subject`, `body_html`, `status` (draft / sending / sent), `created_at`, `sent_at` |
| `email_messages` | `id`, `campaign_id` (nullable), `user_id`, `to_address`, `kind` (verification / receipt / promo), `status` (queued / sent / failed), `attempts`, `last_error`, `created_at`, `sent_at` |

v1 additions: `email_messages` also has `subject` and `body_html`, and `kind` also allows `password_reset` and `payment_failed`. `email_campaigns` is not created yet. See `My_thought_process.md`.

Postgres conventions: `uuid` primary keys, `timestamptz` for all times, `jsonb` for raw payloads, money as **whole kobo in `bigint`** (₦100 = `10000`), never floats.

## Rules that always apply

- **No secrets in code or committed files.** Use `dotnet user-secrets` locally and a `.env` file on the VM only. Never write a real key into `appsettings.json`. Check `.gitignore` before the first commit.
- **Paystack is test mode only.** The pay button is a *collection* (Initialize Transaction → checkout → webhook), not a Transfer.
- **The webhook is the source of truth for payments**, not the browser redirect. Verify the webhook signature, log the raw event to `payment_webhook_events`, and make processing **idempotent** (same event twice = no double update).
- **The API never sends email directly.** It writes an `email_messages` row, publishes the id to `email-queue`, and the Worker sends it. Retry failures; after 3 attempts send to a dead-letter queue.
- **Promo emails go only to users with `MarketingOptIn = true`**, and every promo includes an unsubscribe link.
- **Cost safety on GCP:** free-tier regions only (`us-central1`, `us-east1`, `us-west1`), **no Cloud SQL**, Postgres stays private behind the firewall (only ports 80/443 open).
- Prefer clear, boring code over clever abstractions. This codebase is for learning.

## Roadmap and current status

Update the status column as phases finish.

| Phase | Goal | Status |
|---|---|---|
| 0. Tooling | .NET 8 SDK, Docker Desktop, Git/GitHub, editor, Postman/Bruno, ngrok | Done |
| 1. Accounts | Paystack test keys, pick email provider, buy a domain | In progress: Paystack + ngrok done; email provider and domain open |
| 2. Structure + DB | Solution, docker-compose (postgres, rabbitmq, mailpit), EF Core migrations | In progress: migrations done; infra runs as standalone containers, no docker-compose.yml yet |
| 3. Auth | Identity: register, confirm email, login, logout, opt-in checkbox | Done (API only, plus forgot/reset password) |
| 4. Payments | Pay ₦100 → Paystack checkout → webhook → `payments` row = success | Done (webhook tested with a signed simulated event; real test-card checkout still to try) |
| 5. Email + queue | Worker service, queue-based receipts and verification, promo campaign, dead-letter queue | In progress: verification, reset, receipt, payment-failed, retries and DLQ done; promo campaign open |
| 6. Deploy | Budget alerts first, then the e2-micro VM, CloudAMQP, email provider + DNS, Caddy HTTPS, nightly DB backup | Not started |
| 7. Ductape | Move Paystack, email, queue, DB, then Features over to Ductape one at a time and compare | Not started |

## Ductape context (Phase 7 only)

- Ductape is a beta platform: you register apps (APIs), resources (DB, queue, storage, notifications) and **Features** (multi-step workflows with rollback) in its Workbench, then call them by name from code. Credentials live in Ductape per environment (`dev`, `prd`).
- It **connects to** our infrastructure; it does not host our database.
- .NET SDK: NuGet package `Ductape.Sdk` (targets .NET 8, pre-1.0, described as a parity port of the TypeScript SDK). Its docs show mostly TypeScript, so **verify C# method names against the actual package** rather than guessing.
- Its CLI and MCP server need Node.js: `npm install -g @ductape/cli @ductape/mcp`.
- Docs: https://docs.ductape.app (Features are marked preview, so APIs may change).
- Give Ductape a restricted database user, never the admin account.

## How to help me well

- Work on one phase at a time, and finish with a quick way for me to test it.
- When you introduce a concept for the first time (ack, idempotency, migration, reverse proxy...), give a one-line plain explanation.
- If something here seems wrong or outdated, tell me rather than silently working around it.
