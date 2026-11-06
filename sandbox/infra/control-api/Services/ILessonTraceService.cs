using ControlApi.Models;

namespace ControlApi.Services;

public interface ILessonTraceService
{
    Task<TracedRequestResult> RunAsync(TracedRequestInput input, CancellationToken ct);
}
