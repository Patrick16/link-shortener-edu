## What it is

An ASP.NET Core Web API — registration and login, the only service that issues JWTs. Owns
`users_db` (the only writer against it).

## What it solves

`LinkApi`/`RedirectApi` never require authentication (anonymous link creation is a deliberate
product decision — see [LinkApi](node:link-api)), but attaching a `userId` to a link so its
creator can later list their own links needs *some* identity system. AuthApi is that system,
deliberately kept separate from the services that consume its tokens.

## How it works

`POST /register`: hash the password (`PasswordHasher<T>`, PBKDF2, self-describing format — no
separately-stored salt needed), store the user, return a token (auto-login on signup, not a
separate required login step). `POST /login`: verify the hash, return a token. Both use the same
"Invalid email or password" message for "no such user" and "wrong password" — deliberate, avoids
leaking which emails are registered.

## How it's implemented here

```csharp
// src/backend/Services/AuthApi/Controllers/AuthController.cs:36
var normalizedEmail = NormalizeEmail(request.Email);
var emailTaken = await _context.Users.AnyAsync(x => x.Email == normalizedEmail, cancellationToken);
if (emailTaken) return EmailAlreadyRegistered();

var passwordHash = PasswordHasher.HashPassword(placeholder, request.Password);
var user = new User(Guid.NewGuid(), request.Name, normalizedEmail, passwordHash, string.Empty);

_context.Users.Add(user);
try { await _context.SaveChangesAsync(cancellationToken); }
catch (DbUpdateException) { return EmailAlreadyRegistered(); } // see this node's Pitfalls for why
```

Other services validate the tokens this issues without ever calling back into AuthApi: signing
key/issuer/audience live in `Common.Constants` so AuthApi (issuer) and `LinkApi` (validator) can't
drift onto different values, enforced in `docker-compose.yml` via one shared YAML anchor both
services reference.

## Pitfalls

- 🐛 [Duplicate-email race could crash Register with an unhandled exception](pitfall:auth-duplicate-email-race)
  (race-condition) — fixed
- 🐛 [Email uniqueness and login were case-sensitive](pitfall:auth-email-case-sensitivity)
  (logic-bug) — fixed

## Relatives

### Nodes

- [LinkApi](node:link-api) — optionally validates the tokens this node issues
- [users_db](node:users-db)

### Patterns

None yet.
