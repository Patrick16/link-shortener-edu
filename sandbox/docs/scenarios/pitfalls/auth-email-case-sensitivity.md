# Email uniqueness and login were case-sensitive

**Category:** logic-bug **Status:** fixed

The unique index on `Email`, the registration duplicate-check, and the login lookup all compared
with plain `==` — no `.Trim()`/`.ToLowerInvariant()` normalization. Postgres text comparison is
case-sensitive by default, so `Alice@Example.com` and `alice@example.com` were two distinct,
both-insertable accounts, defeating the "unique email" invariant, and a legitimate user who typed
their email in a different case than they registered with got "Invalid email or password" on login
even with the correct password.

🐛 **Bug** — no normalization anywhere email is compared or stored:

```csharp
var emailTaken = await _context.Users.AnyAsync(x => x.Email == request.Email, cancellationToken);
// ...
var user = new User(Guid.NewGuid(), request.Name, request.Email, passwordHash, "");
```

✅ **Fix** — a single `NormalizeEmail` (trim + lowercase) applied everywhere an email is written or
queried:

```csharp
var normalizedEmail = NormalizeEmail(request.Email);
var emailTaken = await _context.Users.AnyAsync(x => x.Email == normalizedEmail, cancellationToken);
// ...
var user = new User(Guid.NewGuid(), request.Name, normalizedEmail, passwordHash, "");
```

Existing rows written before this fix were not backfilled (learning-project scope) — only future
writes are normalized.

(see review-reports/2026-09-17-2003-85d9570-auth-jwt-register-login.md, F2)

## Relatives

### Nodes

- [AuthApi](node:auth-api)

### Patterns

None yet.
