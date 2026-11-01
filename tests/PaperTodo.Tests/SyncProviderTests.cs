using System;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using PaperTodo;

namespace PaperTodo.Tests;

[TestClass]
public class SyncProviderTests
{
    [TestMethod]
    public void TestSyncProviderFactory_CreateWebDav()
    {
        var state = new AppState
        {
            SyncProvider = SyncProviders.WebDav,
            SyncWebDavUrl = "https://dav.jianguoyun.com/dav/",
            SyncWebDavUsername = "user@test.com",
            SyncWebDavPassword = "secret-app-password",
            SyncWebDavRemotePath = "/papertodo/data.json"
        };

        var provider = SyncProviderFactory.Create(state);
        Assert.IsInstanceOfType(provider, typeof(WebDavSyncProvider));
    }

    [TestMethod]
    public void TestSyncProviderFactory_CreateS3()
    {
        var state = new AppState
        {
            SyncProvider = SyncProviders.S3,
            SyncS3Endpoint = "https://s3.example.com",
            SyncS3Bucket = "my-bucket",
            SyncS3AccessKey = "AKIAEXAMPLE",
            SyncS3SecretKey = "secret123",
            SyncS3Region = "us-east-1"
        };

        var provider = SyncProviderFactory.Create(state);
        Assert.IsInstanceOfType(provider, typeof(S3SyncProvider));
    }

    [TestMethod]
    public void TestSyncProviderFactory_CreateCustom()
    {
        var state = new AppState
        {
            SyncProvider = SyncProviders.Custom,
            SyncCustomServerUrl = "https://sync.example.com",
            SyncCustomToken = "my-bearer-token"
        };

        var provider = SyncProviderFactory.Create(state);
        Assert.IsInstanceOfType(provider, typeof(CustomHttpSyncProvider));
    }

    [TestMethod]
    public async Task TestWebDav_EmptyUrlReturnsError()
    {
        var provider = new WebDavSyncProvider("", "", "", "");
        var res = await provider.TestConnectionAsync();
        Assert.IsFalse(res.Success);
    }

    [TestMethod]
    public async Task TestS3_EmptyEndpointReturnsError()
    {
        var provider = new S3SyncProvider("", "", "", "", "");
        var res = await provider.TestConnectionAsync();
        Assert.IsFalse(res.Success);
    }
}
