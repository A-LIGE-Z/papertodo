using System.Net;
using System.Net.Http;

namespace PaperTodo;

public sealed class SyncConflictException : Exception
{
    public SyncConflictException() : base(Strings.Get("SyncConflictRetry")) { }
}

internal sealed class SyncRevision
{
    private string? _etag;
    private bool _missing;
    public void Observe(HttpResponseMessage response)
    {
        _missing = response.StatusCode == HttpStatusCode.NotFound;
        _etag = response.Headers.ETag is { IsWeak: false } tag ? tag.Tag : null;
    }
    public void Apply(HttpRequestMessage request)
    {
        if (_missing) request.Headers.TryAddWithoutValidation("If-None-Match", "*");
        else if (_etag != null) request.Headers.TryAddWithoutValidation("If-Match", _etag);
    }
    public static void Check(HttpResponseMessage response)
    {
        if (response.StatusCode == HttpStatusCode.PreconditionFailed) throw new SyncConflictException();
        response.EnsureSuccessStatusCode();
    }
}
