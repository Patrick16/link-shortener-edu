# Concurrent refresh-token rotations could fork the token family instead of detecting reuse

**Category:** race-condition **Status:** fixed

`RotateAsync` is supposed to implement standard refresh-token reuse detection: presenting an
already-rotated token should burn every live session for that user, since it means either a stolen
token is being replayed or a client retried a request that already succeeded. It read the token
row, checked `RevokedAt` in memory, and only afterwards wrote `RevokedAt = DateTime.UtcNow` and
issued a new token — a classic check-then-act race. Two requests presenting the identical raw
refresh token concurrently (each with its own `DbContext`/DB connection) could both read
`RevokedAt == null` before either had saved, so both would proceed to revoke-and-reissue. Depending
on timing, this either forked the token family (two independent new refresh tokens live for the
same lineage, silently defeating reuse detection — exactly the attacker-races-the-legitimate-client
scenario the mechanism exists to catch) or produced a false-positive full logout (whichever save
landed second re-read the now-already-revoked row and burned every session for that user).

This had a real, non-adversarial trigger too: the frontend's session-restore effect could itself
fire two concurrent `/refresh` calls with the same cookie under React StrictMode's dev-mode
double-invoke, hitting this exact backend race on every page reload.

🐛 **Bug** — `src/backend/Services/AuthApi/RefreshTokenService.cs`, `RotateAsync`: read, check, then
write, with no atomicity between the check and the write:

```csharp
var existing = await _context.RefreshTokens
    .FirstOrDefaultAsync(x => x.TokenHash == tokenHash, cancellationToken);

if (existing is null || existing.ExpiresAt <= DateTime.UtcNow)
{
    return null;
}

if (existing.RevokedAt is not null)
{
    // This exact token was already rotated away or revoked once before. Seeing it again
    // means either a stolen copy is being replayed, or a client retried a request that
    // already succeeded - either way, don't hand out a new token from it. Burn every other
    // live token for this user too, so a leaked token can't keep refreshing indefinitely.
    await RevokeAllForUserAsync(existing.UserId, cancellationToken);
    return null;
}

existing.RevokedAt = DateTime.UtcNow;

// IssueAsync's SaveChangesAsync flushes both this revocation and the new token insert.
var (newRawToken, newExpiresAt) = await IssueAsync(existing.UserId, cancellationToken);

return new RefreshRotationResult(existing.UserId, newRawToken, newExpiresAt);
```

✅ **Fix** — claim the token atomically with a single conditional `ExecuteUpdateAsync`; the affected-
row count (not a separately-read flag) tells the caller definitively whether it won the race:

```csharp
var existing = await _context.RefreshTokens
    .AsNoTracking()
    .FirstOrDefaultAsync(x => x.TokenHash == tokenHash, cancellationToken);

if (existing is null || existing.ExpiresAt <= DateTime.UtcNow)
{
    return null;
}

// Claim this token atomically: only the caller whose UPDATE actually flips RevokedAt from null to
// non-null wins the race. Two concurrent calls with the same raw token (a React StrictMode
// double-invoke, two tabs refreshing at the same instant, or a genuine stolen-token replay racing
// the real client) used to both read RevokedAt == null before either saved, so both could "win" and
// the token family would silently fork instead of reuse detection ever firing. ExecuteUpdateAsync's
// row count tells us definitively which case this is - a plain SELECT-then-write can't.
var claimed = await _context.RefreshTokens
    .Where(x => x.TokenHash == tokenHash && x.RevokedAt == null)
    .ExecuteUpdateAsync(setters => setters.SetProperty(x => x.RevokedAt, DateTime.UtcNow), cancellationToken);

if (claimed == 0)
{
    await RevokeAllForUserAsync(existing.UserId, cancellationToken);
    return null;
}

var (newRawToken, newExpiresAt) = await IssueAsync(existing.UserId, cancellationToken);
return new RefreshRotationResult(existing.UserId, newRawToken, newExpiresAt);
```

The frontend's contribution to the race was fixed alongside it — `AuthContext.tsx`'s session-restore
effect flips its guard ref to `false` *before* the async call goes out (not in a cleanup callback),
so React StrictMode's synchronous mount→cleanup→mount-again no longer sends the request twice:

```tsx
// before: `cancelled` guard set in cleanup, ref never flipped - StrictMode's second invocation
// still passed the `if (!needsSessionRestoreRef.current) return` check and fired a second /refresh
useEffect(() => {
  if (!needsSessionRestoreRef.current) return
  let cancelled = false
  authApi.refresh().then((response) => { if (!cancelled) applyToken(response.token) }).catch(() => {})
  return () => { cancelled = true }
}, [applyToken])
```

```tsx
// after: ref flipped synchronously, before the request goes out
useEffect(() => {
  if (!needsSessionRestoreRef.current) return
  needsSessionRestoreRef.current = false
  authApi.refresh().then((response) => applyToken(response.token)).catch(() => {})
}, [applyToken])
```

(see review-reports/2026-09-22-1228-dd23e59-refresh-token-rotation.md, F1/F2 — fixed in `dd6ac13`)

## Relatives

### Nodes

- [AuthApi](node:auth-api)

### Patterns

None yet.
