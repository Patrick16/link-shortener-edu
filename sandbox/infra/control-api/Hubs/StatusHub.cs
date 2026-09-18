using Microsoft.AspNetCore.SignalR;

namespace ControlApi.Hubs;

// No client-callable methods yet - clients just listen for the "containersUpdated" broadcast that
// StatusPollerService sends. A per-service subscription model can replace this later if pushing
// the full container list on every change turns out to be too much.
public class StatusHub : Hub;
