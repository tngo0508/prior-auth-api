using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;

namespace PriorAuth.Api.Tests;

public class SupportingDocumentTests(ApiFactory factory) : IClassFixture<ApiFactory>, IAsyncLifetime
{
    private HttpClient _provider = null!;
    private readonly HttpClient _staff = factory.CreateClient();

    public async Task InitializeAsync() =>
        _provider = await (await TestProvider.RegisterAsync(factory.CreateClient())).SignInAsync(factory);

    public Task DisposeAsync() => Task.CompletedTask;

    [Fact]
    public async Task A_document_sent_with_the_request_reaches_the_reviewer_unchanged()
    {
        var content = Encoding.UTF8.GetBytes("Synthetic assessment text.");
        var requestId = NewRequestId();

        var id = await Submit(requestId, Document("Current assessment", "text/plain", content));
        var summary = Assert.Single((await QueueEntry(id)).GetProperty("documents").EnumerateArray());
        var download = await _staff.GetAsync($"/internal/requests/{id}/documents/{summary.GetProperty("id").GetString()}");

        Assert.Equal("Current assessment", summary.GetProperty("title").GetString());
        Assert.Equal(content, await download.Content.ReadAsByteArrayAsync());
        Assert.Equal("text/plain", download.Content.Headers.ContentType?.MediaType);
        Assert.Equal("attachment", download.Content.Headers.ContentDisposition?.DispositionType);
        Assert.Equal("nosniff", download.Headers.GetValues("X-Content-Type-Options").Single());
    }

    [Theory]
    [InlineData("text/html")]
    [InlineData("application/x-msdownload")]
    [InlineData("image/svg+xml")]
    public async Task A_document_of_a_type_that_is_not_accepted_is_refused(string contentType)
    {
        var response = await Post(NewRequestId(), Document("Risky file", contentType, [1, 2, 3]));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains("Accepted types", await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task A_document_larger_than_five_megabytes_is_refused()
    {
        var response = await Post(NewRequestId(), Document("Huge scan", "application/pdf", new byte[5 * 1024 * 1024 + 1]));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains("larger than 5 MB", await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task A_document_given_as_a_link_is_refused()
    {
        var linked = """{ "resource": { "resourceType": "DocumentReference", "status": "current", "content": [{ "attachment": { "contentType": "application/pdf", "title": "Linked", "url": "https://example.org/file.pdf" } }] } }""";

        var response = await Post(NewRequestId(), linked);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Sending_the_missing_document_answers_an_information_request_and_returns_it_to_the_queue()
    {
        var requestId = NewRequestId();
        var id = await Submit(requestId);
        await _staff.PostAsJsonAsync($"/internal/requests/{id}/request-information", new { missing = "Client plan" });

        var resent = await Post(requestId, Document("Client plan", "application/pdf", [37, 80, 68, 70]));
        var entry = await QueueEntry(id);

        Assert.Equal(HttpStatusCode.OK, resent.StatusCode);
        Assert.Equal("Pended", entry.GetProperty("status").GetString());
        Assert.Single(entry.GetProperty("documents").EnumerateArray());
        Assert.Contains(entry.GetProperty("history").EnumerateArray(), h => h.GetProperty("text").GetString()!.StartsWith("Provider supplied more information"));
    }

    [Fact]
    public async Task Resending_without_a_document_does_not_answer_an_information_request()
    {
        var requestId = NewRequestId();
        var id = await Submit(requestId);
        await _staff.PostAsJsonAsync($"/internal/requests/{id}/request-information", new { missing = "Client plan" });

        await Post(requestId);

        Assert.Equal("InformationRequested", (await QueueEntry(id)).GetProperty("status").GetString());
    }

    [Fact]
    public async Task Resending_documents_for_a_request_that_is_not_waiting_for_information_adds_nothing()
    {
        var requestId = NewRequestId();
        var id = await Submit(requestId, Document("Assessment", "text/plain", [65]));

        await Post(requestId, Document("Assessment again", "text/plain", [66]));

        Assert.Single((await QueueEntry(id)).GetProperty("documents").EnumerateArray());
    }

    // ---------- Helpers ----------

    private static string NewRequestId() => $"REQ-{Guid.NewGuid():N}";

    private static string Document(string title, string contentType, byte[] data) => $$"""
        { "resource": { "resourceType": "DocumentReference", "status": "current",
            "content": [{ "attachment": { "contentType": "{{contentType}}", "title": "{{title}}", "data": "{{Convert.ToBase64String(data)}}" } }] } }
        """;

    private Task<HttpResponseMessage> Post(string requestId, params string[] documentEntries) =>
        _provider.PostAsync("/fhir/Claim/$submit", new StringContent(RequestBundle(requestId, documentEntries), Encoding.UTF8, "application/fhir+json"));

    private async Task<string> Submit(string requestId, params string[] documentEntries)
    {
        var response = await Post(requestId, documentEntries);
        var text = await response.Content.ReadAsStringAsync();
        Assert.True(response.IsSuccessStatusCode, text);
        return JsonDocument.Parse(text).RootElement.GetProperty("entry")[0].GetProperty("resource").GetProperty("id").GetString()!;
    }

    private async Task<JsonElement> QueueEntry(string id) =>
        (await _staff.GetFromJsonAsync<JsonElement>("/internal/requests?status=all&take=200")).EnumerateArray().Single(r => r.GetProperty("id").GetString() == id);

    private static string RequestBundle(string requestId, string[] documentEntries) =>
        PasFixtures.RequestBundle(requestId, extraEntries: documentEntries);
}
