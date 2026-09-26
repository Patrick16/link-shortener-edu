# Adding a controllable component to the architecture map

The graph, its generic controls (start/stop/restart/degrade/heal/scale) and its
component-specific controls (pgcat pool size, Sentinel failover config, Mongo read
preference, ...) are two different things:

- **Generic controls** work off Docker compose labels alone - any container in the stack gets
  them for free, nothing to wire up.
- **Component-specific controls** ("capabilities") are declared per-node in
  [`architecture.json`](../frontend/architecture-map/src/data/architecture.json) and picked up
  automatically on both ends: `CapabilityFactory` (backend) and `controlRegistry` (frontend) read
  the node's `capabilities` array and instantiate whatever matches - neither `Program.cs` nor
  `NodePanel.tsx` hardcodes which node gets which control.

This is the recipe for adding a new one.

## 1. Pick a capability name and add it to the node

In `architecture.json`, add a `capabilities` array to the node (or extend an existing one):

```json
{
  "id": "my-new-service",
  "capabilities": ["my-capability"]
}
```

A node can list several. The name is a plain string - no schema beyond that, and it's shared
between backend and frontend, so keep it identical on both ends.

## 2. Backend: one capability class (only if the control needs its own route)

If the control just reads/writes something through an endpoint that doesn't exist yet, add a
class in `sandbox/infra/control-api/Services/Capabilities/`:

```csharp
public sealed class MyCapability(IDockerService docker) : IComponentCapability
{
    public void MapEndpoints(WebApplication app)
    {
        app.MapGet("/api/infra/my-thing", () => Results.Ok(docker.GetMyThing()));
        app.MapPost("/api/infra/my-thing", (MyThingRequest request) =>
        {
            // validate, then:
            return Results.Ok(docker.SetMyThing(request));
        });
    }
}
```

Register it in `CapabilityFactory`'s `_factories` dictionary under the exact name from step 1:

```csharp
["my-capability"] = (docker, loggerFactory) => new MyCapability(docker),
```

That's the only place that knows the mapping - `Program.cs` never changes. If the underlying
Docker/exec logic doesn't exist yet either, add it to `IDockerService`/`DockerService` first, the
same way `SetPgcatPoolSettingsAsync` etc. already work.

Skip this step entirely if the control is purely a frontend display over an existing generic
endpoint (like the connections panels) - not every capability needs a backend class.

## 3. Frontend: one control component

Add a component in `sandbox/frontend/architecture-map/src/components/` that accepts
`CapabilityControlProps` (`{ component, serviceId, instances }` - use only what you need):

```tsx
export function MyCapabilityControl({ serviceId }: CapabilityControlProps) {
  // fetch/mutate through controlApi.ts, render whatever UI it needs
}
```

If it needs standing state (a toggle, a poll loop), keep it self-contained inside this one
component rather than lifting it into `NodePanel` - see `PgcatToggleControl.tsx` or
`PgcatConnectionsPanel.tsx` for the pattern. Nothing else in the app needs to know this control
exists.

Register it in `controlRegistry.tsx`'s `CONTROL_COMPONENTS` map under the same name from step 1:

```tsx
'my-capability': MyCapabilityControl,
```

## 4. Done

`NodePanel.tsx` renders whatever `component.capabilities` lists via that one registry lookup - no
node-specific code to add there. Reload the app; the node from step 1 now shows the new control.
