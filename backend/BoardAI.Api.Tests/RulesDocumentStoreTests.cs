using System.Text.Json;
using BoardAI.Api.Services;

namespace BoardAI.Api.Tests;

public sealed class RulesDocumentStoreTests : IDisposable
{
    private readonly string _tempDirectory;

    public RulesDocumentStoreTests()
    {
        _tempDirectory = Path.Combine(
            Path.GetTempPath(),
            "boardai-rules-store-tests",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDirectory);
    }

    [Fact]
    public void GetDocument_WhenFileUnchanged_ReturnsSameInstance()
    {
        using var store = new RulesDocumentStore();
        var path = WriteJson("rules.json", """{"value":1}""");

        var first = store.GetDocument(path);
        var second = store.GetDocument(path);

        Assert.Same(first, second);
    }

    [Fact]
    public void GetDocument_WhenFileLengthChanges_ReloadsDocument()
    {
        using var store = new RulesDocumentStore();
        var path = WriteJson("rules.json", """{"value":1}""");

        var first = store.GetDocument(path);
        WriteJson("rules.json", """{"value":100}""");

        var second = store.GetDocument(path);

        Assert.NotSame(first, second);
        Assert.Equal(100, second.RootElement.GetProperty("value").GetInt32());
    }

    [Fact]
    public void GetDocument_WhenLengthIsSameButWriteTimeChanges_ReloadsDocument()
    {
        using var store = new RulesDocumentStore();
        var path = WriteJson("rules.json", """{"value":1}""");

        var first = store.GetDocument(path);
        var originalWriteTimeUtc = File.GetLastWriteTimeUtc(path);
        WriteJson("rules.json", """{"value":2}""");
        File.SetLastWriteTimeUtc(path, originalWriteTimeUtc.AddMinutes(1));

        var second = store.GetDocument(path);

        Assert.NotSame(first, second);
        Assert.Equal(2, second.RootElement.GetProperty("value").GetInt32());
    }

    [Fact]
    public void GetDocument_OnFirstLoadAndCacheHit_DoesNotNotify_OnRealChange_NotifiesOnce()
    {
        var changes = 0;
        using var store = new RulesDocumentStore(() => changes++);
        var path = WriteJson("rules.json", """{"value":1}""");

        store.GetDocument(path);
        Assert.Equal(0, changes);

        store.GetDocument(path);
        Assert.Equal(0, changes);

        WriteJson("rules.json", """{"value":100}""");
        store.GetDocument(path);

        Assert.Equal(1, changes);
    }

    [Fact]
    public void GetDocument_WhenDocumentChanges_DoesNotDisposeOldDocumentImmediately()
    {
        using var store = new RulesDocumentStore();
        var path = WriteJson("rules.json", """{"value":1}""");

        var first = store.GetDocument(path);
        var oldRoot = first.RootElement;
        WriteJson("rules.json", """{"value":100}""");

        var second = store.GetDocument(path);

        Assert.NotSame(first, second);
        // 不 Clone；如果 JsonDocument 被立即释放，这里会抛 ObjectDisposedException。
        Assert.Equal(1, oldRoot.GetProperty("value").GetInt32());
    }

    [Fact]
    public void GetDocument_WhenFileDoesNotExist_ThrowsFileNotFoundException()
    {
        using var store = new RulesDocumentStore();
        var missingPath = Path.Combine(_tempDirectory, "missing.json");

        Assert.Throws<FileNotFoundException>(() => { store.GetDocument(missingPath); });
    }

    [Fact]
    public void GetDocument_AfterDispose_ThrowsObjectDisposedException()
    {
        var store = new RulesDocumentStore();
        var path = WriteJson("rules.json", """{"value":1}""");

        store.Dispose();

        Assert.Throws<ObjectDisposedException>(() => { store.GetDocument(path); });
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_tempDirectory))
                Directory.Delete(_tempDirectory, recursive: true);
        }
        catch
        {
            // 测试清理失败不应掩盖真实断言结果。
        }
    }

    private string WriteJson(string fileName, string json)
    {
        var path = Path.Combine(_tempDirectory, fileName);
        File.WriteAllText(path, json);
        return path;
    }
}
