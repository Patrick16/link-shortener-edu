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
    string? City);
