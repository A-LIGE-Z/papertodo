using System.Net;
using System.Net.Http;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace PaperTodo.Tests;

[TestClass]
public class CustomConnectionTests
{
    private sealed class Handler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => Task.FromResult(respond(request));
    }

    [TestMethod]
    public async Task PublicPingDoesNotValidateAnIncorrectToken()
    {
        var paths = new List<string>();
        using var provider = new CustomHttpSyncProvider("http://test.invalid", "wrong", new Handler(request =>
        {
            paths.Add(request.RequestUri!.AbsolutePath);
            return request.RequestUri.AbsolutePath.EndsWith("/ping")
                ? new(HttpStatusCode.OK) { Content = new StringContent("{\"app\":\"PaperTodo.SyncServer\"}") }
                : new(HttpStatusCode.Unauthorized);
        }));
        Assert.IsFalse((await provider.TestConnectionAsync()).Success);
        CollectionAssert.Contains(paths, "/api/v1/sync");
    }

    [TestMethod]
    public async Task HtmlSuccessResponseIsRejected()
    {
        using var provider = new CustomHttpSyncProvider("http://test.invalid", "token",
            new Handler(_ => new(HttpStatusCode.OK) { Content = new StringContent("<html>Login</html>") }));
        Assert.IsFalse((await provider.TestConnectionAsync()).Success);
    }
}
