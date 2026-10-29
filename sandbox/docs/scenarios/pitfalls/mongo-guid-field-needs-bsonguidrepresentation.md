# A `Guid` field in a Mongo document needs its own `BsonGuidRepresentation`

**Category:** db-query **Status:** fixed

Watch for this any time a new `Guid`/`Guid?` property is added to a type stored in MongoDB via
`MongoDB.Driver` 3.x: the driver dropped its old implicit "legacy .NET" GUID encoding, so a `Guid`
field with no explicit representation throws at the moment the driver actually tries to serialize
it — not at startup, not in a mocked unit test, and not under code review. A sibling field on the
*same document* already carrying the attribute doesn't help; the attribute is per-property, not
per-type, so each new `Guid` field needs its own. This is a strong case of a bug that's structurally
invisible to the usual safety nets — unit tests typically mock the Mongo layer entirely, and a code
reviewer can't execute a BSON serializer by inspection — so it only surfaces against a real Mongo
instance, under real traffic.

⚠️ **Mistake** — `src/backend/Shared/Common/Models/ClickMeta.cs` (before commit `361fa92`):

```csharp
public record ClickMeta(
    Guid Id,
    // ... other fields ...
    Guid? UserId = null); // no [property: BsonGuidRepresentation(...)] here
```

`Id` on this same record already carried `[property: BsonGuidRepresentation(GuidRepresentation.Standard)]`
— added when the type was first written — but `UserId` was added later, for owner-scoping, without
copying it. `MongoClickMetaQueryService.GetRecentAsync`'s filter —
`Builders<ClickMeta>.Filter.Eq(x => x.UserId, callerUserId)` — throws `BsonSerializationException`
("GuidRepresentation is Unspecified") the instant the driver tries to serialize `callerUserId` into
that filter, because `UserId` has no representation to fall back on. The endpoint passed every unit
test (they mock `IClickMetaQueryService`) and a clean `feature-review` pass, then failed on the very
first real browser click against a live Mongo instance.

✅ **Do this instead**:

```csharp
public record ClickMeta(
    Guid Id,
    // ... other fields ...
    [property: BsonGuidRepresentation(GuidRepresentation.Standard)] Guid? UserId = null);
```

`GuidRepresentation.Standard` is chosen to match `Id` — the cross-driver-compatible encoding (what
`mongosh`/Compass show natively), not a legacy .NET-specific format. A follow-up sweep (requested
specifically because this is the kind of mistake that tends to recur) checked every other
`Guid`/`Guid?` field touching `IMongoCollection<T>` in the codebase and confirmed `ClickMeta.UserId`
was the only gap — the lesson still applies to any *future* Mongo document type with a `Guid` field,
not just this one: add the attribute immediately when the field is added, since nothing except live
traffic against a real Mongo instance will catch a missing one.

Index *definitions* (the compound `(Hash, UserId, ClickedAt)` index on this same field) don't need
this attribute — only query/document *value* serialization does.

(see `.notes/review-reports/2026-10-29-2038-reporting-recent-clicks-bsonguid-fix.md` — fixed in
`361fa92`)

## Relatives

### Nodes

- [ReportingApi](node:reporting-api) — the endpoint that threw (`GET /reports/{hash}/clicks`)
- [Mongo](node:mongo1)
- [TrafficService](node:traffic-service) — writes `ClickMeta` documents, including this field

### Patterns

- [CQRS fan-out](pattern:cqrs-fanout)
