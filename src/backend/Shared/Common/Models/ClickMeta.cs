using MongoDB.Bson;
using MongoDB.Bson.Serialization.Attributes;

namespace Common.Models;

// Mongo document, one per click (Clicks collection in clicks_meta_db). Id matches the
// Postgres Clicks row's Id 1:1, so a click's core record and its metadata can be joined by that
// shared key. Written once by TrafficService.ClickTrackedConsumer alongside the Postgres write —
// see ClickTrackedEvent for the raw fields RedirectApi captures at request time.
//
// BsonGuidRepresentation is required explicitly - the driver refuses to serialize a Guid at all
// without it (no more implicit "legacy .NET" default as of MongoDB.Driver 3.x). Standard is the
// cross-driver-compatible encoding (what mongosh/Compass show natively), unlike the old
// .NET-specific legacy formats.
public record ClickMeta(
    [property: BsonId, BsonGuidRepresentation(GuidRepresentation.Standard)] Guid Id,
    string Hash,
    DateTime ClickedAt,
    string UserAgent,
    string Referrer,
    string? IpAddress,
    string? Browser,
    string? Os,
    string? DeviceType,
    string? Country,
    string? City,
    // The link's owner at click time, carried through from ClickTrackedEvent.UserId - null for an
    // anonymously-created link. Mirrors ClickFact.UserId's identical field/reasoning: lets
    // ReportingApi's Mongo-backed reads (GET /reports/{hash}/clicks) scope to the caller the same
    // way its ClickHouse-backed summary already does, without a separate ownership lookup.
    //
    // Needs its own BsonGuidRepresentation, same reasoning as Id above - found live: without it,
    // MongoClickMetaQueryService.GetRecentAsync's Filter.Eq(x => x.UserId, callerUserId) throws
    // BsonSerializationException ("GuidRepresentation is Unspecified") the moment the driver tries
    // to serialize callerUserId into the query, since this property has no attribute of its own to
    // fall back on.
    [property: BsonGuidRepresentation(GuidRepresentation.Standard)] Guid? UserId = null);
