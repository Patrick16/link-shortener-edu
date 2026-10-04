# Duplicate-email race could crash Register with an unhandled exception

**Category:** race-condition **Status:** fixed

`Register` did a check-then-act: `AnyAsync(x => x.Email == email)` followed later by
`SaveChangesAsync`. A unique index on `Email` exists at the database level, so two near-simultaneous
registrations with the same email (a double-click, a retried request) could both pass the
`AnyAsync` check before either had inserted — the second `SaveChangesAsync` then threw an unhandled
`DbUpdateException` (unique-constraint violation), surfacing as a raw 500 instead of the intended
409 Conflict.

🐛 **Bug** — no handling around the insert:

```csharp
var emailTaken = await _context.Users.AnyAsync(x => x.Email == normalizedEmail, cancellationToken);
if (emailTaken) return EmailAlreadyRegistered();

_context.Users.Add(user);
await _context.SaveChangesAsync(cancellationToken); // unique-index violation throws here, uncaught
```

✅ **Fix** — the database-level unique index is the real guarantee (the `AnyAsync` check is just an
optimization to avoid the round trip on the common case); catching the constraint violation and
returning the same 409 makes both paths converge on the same response:

```csharp
_context.Users.Add(user);
try
{
    await _context.SaveChangesAsync(cancellationToken);
}
catch (DbUpdateException)
{
    // The AnyAsync check above and this insert aren't atomic: two requests for the same email
    // can both pass the check before either commits. The unique index then rejects the loser
    // here instead of at the check, so translate that into the same 409 rather than letting it
    // surface as an unhandled 500.
    return EmailAlreadyRegistered();
}
```

## Relatives

### Nodes

- [AuthApi](node:auth-api)

### Patterns

None yet.
