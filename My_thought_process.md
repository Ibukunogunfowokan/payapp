# My thought process: PayApp v1

How each feature in version one works, why it was built that way, and what is deliberately left for later.
Read it alongside the code; every section names the files involved.

## The big picture

```
Browser / Swagger ──► PayApp.Api ──► Postgres (users, payments, email rows)
                          │
                          ├──► Paystack (initialize + verify)        ◄── Paystack webhook
                          │
                          └──► RabbitMQ: email-queue ──► PayApp.Worker ──► Mailpit (SMTP)
```

Two rules shape almost everything:

1. **The API never sends email.** It saves an `email_messages` row and publishes the row's id to RabbitMQ. The Worker sends it. A slow or broken mail server can therefore never make a user's request slow or fail.
2. **Paystack, not the browser, decides whether a payment succeeded.** The browser can be closed, faked or replayed; the webhook and the server-to-server verify call cannot.

## 0. Configuration (`.env`)

**Files:** `PayApp.Core/Configuration/DotEnv.cs`, `Settings.cs`

- `dotnet run` doesn't read `.env` on its own, so `DotEnv.Load()` runs first thing in both `Program.cs` files. It walks up from the current folder until it finds `.env` and copies each `KEY=value` into the process's environment variables.
- It never overwrites a variable that is already set. That makes real environment variables (Docker, the VM, or a one-off `RABBITMQ_USER=guest dotnet run`) win over the file.
- ASP.NET Core already reads environment variables into `IConfiguration`, so the code just asks for `config["POSTGRES_USER"]`.
- `Settings.Required(...)` throws a clear "Missing setting X, add it to your .env" message instead of a confusing null error deep inside a library.
- `Settings` builds the Postgres connection string and the RabbitMQ `ConnectionFactory` in one place, so the API and Worker can't drift apart. Setting `RABBITMQ_URL` (CloudAMQP) overrides host/user/password.

## 1. Database

**Files:** `PayApp.Core/Entities/*`, `PayApp.Core/Data/AppDbContext.cs`, `PayApp.Core/Data/Migrations/*`

- `AppDbContext` lives in Core because both the API and the Worker need it.
- It extends `IdentityDbContext<AppUser, IdentityRole<Guid>, Guid>`. The `Guid` makes Identity's user ids Postgres `uuid` instead of strings, matching the brief.
- `AppUser` adds `FullName`, `MarketingOptIn` and `CreatedAt` to Identity's `AspNetUsers` table.
- Our own tables use Postgres naming (`payments`, `amount_kobo`). A small loop in `OnModelCreating` converts property names to snake_case for our three entities. Identity's tables keep their default names.
- **Status values are stored as readable text** (`pending`, `password_reset`) rather than numbers, so a `select` in psql is self-explanatory.
- **Money:** `AmountKobo` is a `long` (`bigint`). ₦100 is `10000`. No floats or decimals anywhere near money.
- **Times:** everything is `DateTimeOffset` in UTC, which Npgsql maps to `timestamptz`.
- **`payment_webhook_events.payload`** is `jsonb`, so you can query inside it later (`payload->>'event'`).

**Changes from the brief's first draft:**
- `email_messages` gained `subject` and `body_html`. The API renders the email when it knows the context (the token, the payment), and the Worker just delivers it.
- Two extra email kinds were added: `password_reset` and `payment_failed`.
- `email_campaigns` is not created yet. It belongs to the promo feature in Phase 5. `campaign_id` exists as a nullable column, ready for it.

**Migrations** are versioned schema changes stored as C# files. The schema is created with:

```
dotnet ef migrations add <Name> --project PayApp.Core --startup-project PayApp.Api --output-dir Data/Migrations
dotnet ef database update     --project PayApp.Core --startup-project PayApp.Api
```

## 2. Auth: register, confirm email, log in/out

**Files:** `PayApp.Api/Auth/AuthEndpoints.cs`, `PayApp.Api/Program.cs`

- **ASP.NET Core Identity** does the hard parts: password hashing, token generation, lockout. Nothing security-related is hand-rolled.
- **Register** creates the user through `UserManager.CreateAsync`, which enforces the password rules, then queues a verification email. A failing rule returns a 400 that lists which rules failed.
- **Confirm email:**
  - Identity generates a signed, time-limited token. Its default lifetime is one day.
  - Raw tokens contain `+` and `/`, which break inside URLs, so they are **base64url-encoded** before going into the link and decoded on the way back.
  - The link points at `GET /api/auth/confirm-email`, so clicking it in Mailpit confirms the account directly.
- **Log in:**
  - `SignIn.RequireConfirmedEmail = true` means an unconfirmed user gets a 403 telling them to confirm.
  - `lockoutOnFailure: true` locks the account for a few minutes after repeated wrong passwords. This blocks password guessing.
  - On success Identity sets an **auth cookie** (`payapp.auth`). Swagger and browsers send it automatically, so there's no token handling for you to do.
- **Cookie redirects:** Identity's cookie normally redirects to a login page. Since this is an API, `Program.cs` changes those redirects into plain `401` and `403` responses.
- **Resend confirmation:** for when the first link expired.

## 3. Forgot / reset password

**Files:** `AuthEndpoints.cs` (`ForgotPassword`, `ResetPassword`)

- `forgot-password` **always returns the same message**, whether the email exists or not. Otherwise attackers could use it to discover who has an account.
- If the account exists and is confirmed, Identity generates a reset token, which is base64url-encoded and emailed as a **code**. There's no UI yet, so the user pastes the code into `POST /api/auth/reset-password`. Once we build a frontend, this becomes a link to a reset form.
- Reset tokens are tied to the user's security stamp. Once the password changes, the same code stops working; the test above showed "Invalid token".

## 4. Payments: Pay ₦100

**Files:** `PayApp.Api/Payments/*`

### Starting a payment (`POST /api/payments`)

1. Create a `payments` row with status `pending` and a unique reference (`payapp_<guid>`). The row exists **before** we talk to Paystack, so every Paystack transaction maps to a record we own.
2. Call Paystack **Initialize Transaction** with the amount in kobo, the user's email, our reference and a callback URL.
3. Return Paystack's `checkoutUrl`. The user opens it and pays with a test card.

**Test mode only:** `Program.cs` refuses to start if `PAYSTACK_SECRET_KEY` doesn't begin with `sk_test_`. That makes a real charge impossible by accident.

### Finding out what happened

There are two ways, and **both funnel into one method**, `PaymentService.ApplyAsync`:

| Path | Who calls it | Trustworthy because... |
|---|---|---|
| **Webhook** `POST /api/paystack/webhook` | Paystack's servers | We verify the HMAC signature |
| **Callback** `GET /api/payments/callback` | The user's browser after checkout | We ignore what the browser says and call Paystack's **Verify** API ourselves |

Why have both?
- The webhook is the **source of truth for success**.
- Paystack doesn't reliably send webhooks for failed or abandoned checkouts. Asking Verify when the user comes back is how we learn about **failures**, and that's needed for the failure email.

### Webhook signature

**File:** `PaystackSignature.cs`

- Paystack computes `HMAC-SHA512(secret key, raw request body)` and sends it in the `x-paystack-signature` header.
- We recompute it over the **raw bytes**, before any JSON parsing, and compare with `CryptographicOperations.FixedTimeEquals`. That comparison takes the same time whether the first or the last character differs, so an attacker can't guess the signature byte by byte from response times.
- An invalid signature, or a body changed by even one character, gets a 401. Both were tested.
- Every valid event is saved raw to `payment_webhook_events` **before** processing, so there's always an audit trail of exactly what Paystack sent.

### Idempotency: the same event twice changes nothing

Paystack retries webhooks, and the webhook and callback can arrive at the same moment. `ApplyAsync` handles this with **one conditional `UPDATE`**:

```
UPDATE payments SET status = 'success' ... WHERE id = @id AND status <> 'success'
```

- The database applies this atomically. If two requests race, only one sees "1 row updated", and only that one queues the email.
- The second webhook updates 0 rows and does nothing, so the user gets exactly one receipt. This was tested by sending the same webhook twice.
- Transition rules:
  - `success` can replace `pending`, `failed` or `abandoned`, because the user may retry on the same checkout.
  - `failed` and `abandoned` can only replace `pending`. A late failure must never undo a success.
- **Amount check:** before marking success, we confirm Paystack's amount and currency match our row (10000 NGN). A mismatch is logged and **not** marked paid.

## 5. Emails via RabbitMQ

**Files:**
- `PayApp.Core/Messaging/EmailQueue.cs`
- `PayApp.Api/Email/*`
- `PayApp.Worker/EmailConsumer.cs`, `OutboxSweeper.cs`, `SmtpEmailSender.cs`

### Which emails, when

| Trigger | Email |
|---|---|
| Register / resend confirmation | Confirm your email |
| Forgot password | Reset code |
| Payment becomes `success` | Receipt (amount, reference, date) |
| Payment becomes `failed` / `abandoned` | "Your payment did not go through" |

### API side (producer)

`EmailOutbox.QueueAsync`:
1. Saves the `email_messages` row with status `queued`.
2. Publishes **only the row's id** to `email-queue`. The queue carries a pointer, not the email, so the database stays the single source of truth.

`EmailQueuePublisher` keeps **one long-lived connection**, because opening a RabbitMQ connection per request is slow. It also uses **publisher confirms**: RabbitMQ acknowledges that it has stored the message, so a publish that silently disappears shows up as an error.

If RabbitMQ is down, the row is still saved and the user's request still succeeds, because the error is logged rather than thrown.

### Worker side (consumer)

`EmailConsumer` is a `BackgroundService`:

- **Prefetch 1:** RabbitMQ hands over one message at a time.
- **Manual ack:** a message is removed from the queue only after we call `BasicAck`. If the Worker crashes mid-send, the message is redelivered rather than lost.
- For each id, it loads the row:
  - If the row is already `sent`, it just acks. This handles duplicates.
  - Otherwise it sends over SMTP, marks the row `sent`, then acks.

### Retries and the dead-letter queue

A **dead-letter queue (DLQ)** is where messages go when they can't be processed, so they don't block the queue or vanish.

```
email-queue ──fail (attempt < 3)──► email-queue.retry ──(waits 10s, TTL)──► email-queue
     │
     └──fail (attempt 3)──► nack ──► email-queue.dlq
```

- `email-queue.retry` has no consumer. Messages sit there for 10 seconds (`x-message-ttl`). When they expire, RabbitMQ "dead-letters" them back into `email-queue`. That gives a delay between retries without any timer code.
- `email-queue` itself dead-letters into `email-queue.dlq`. On the 3rd failure the Worker nacks with `requeue: false`, so RabbitMQ moves the message to the DLQ, and the row is marked `failed` with `last_error`.
- This was tested by pointing the Worker at a dead SMTP port: attempts 1, 2, 3, then 1 message in the DLQ.

### Outbox sweeper

If RabbitMQ was down when the API saved an email, the id never reached the queue.
- `OutboxSweeper` runs every minute and re-publishes rows that are still `queued`, have never been attempted, and are more than 2 minutes old.
- A duplicate is harmless because the consumer skips rows already `sent`.
- This was tested for real: during testing RabbitMQ rejected the API's login, and the sweeper delivered that email once the connection worked.

### SMTP

- `SmtpEmailSender` uses .NET's built-in `SmtpClient`.
- Locally it talks to Mailpit (`localhost:1025`, no login).
- For Brevo or Resend in Phase 6, set `EMAIL_SMTP_HOST`, `EMAIL_SMTP_PORT=587`, `EMAIL_SMTP_USER`, `EMAIL_API_KEY` and `EMAIL_SMTP_ENABLE_SSL=true`. No code changes.
- Microsoft now recommends MailKit over `SmtpClient`. We avoided adding a library we hadn't agreed on, and `SmtpClient` works fine with both providers on port 587.

## Known limits of v1 (deliberate)

- **No UI yet.** Everything is tested through Swagger (`http://localhost:5146/swagger`) or `PayApp.Api.http`. The frontend decision (Razor Pages vs React) is still open.
- **A checkout that is simply closed is never resolved.** If the user never returns to the callback, the payment stays `pending`. A later job could verify old pending payments.
- **Reset tokens and confirmation links are stored inside `email_messages.body_html`.** They expire, but the next step is to have the Worker render emails from data rather than storing finished HTML.
- **Swagger needs no login and is Development only.**
- **The promo campaign is not built yet.** It is the remaining part of Phase 5: `email_campaigns`, opt-in filter, unsubscribe link.
