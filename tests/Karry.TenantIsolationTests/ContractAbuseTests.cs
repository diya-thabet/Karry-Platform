using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Karry.BuildingBlocks.Idempotency;
using Karry.Testing;

namespace Karry.TenantIsolationTests;

/// <summary>
/// Abuse of every operation of the contract (hardening plan H-A4–H-A6), by a real administrator of company B: broken
/// and oversized bodies, absurd page sizes and junk query values are refused with a 4xx problem, never a 500, and no
/// row of company A changes. A new operation is attacked automatically, like the isolation suite.
/// </summary>
[Trait("Story", "P1.18a")]
public sealed partial class TenantIsolationSuite
{
    /// <summary>Above every JSON limit of the API (sync batches take 8 MiB; everything else 1 MiB).</summary>
    private const int Oversized = (8 * 1024 * 1024) + 1024;

    public static TheoryData<string, string, string, string> BrokenBodies()
    {
        var data = new TheoryData<string, string, string, string>();
        foreach (var (id, method, path, operation) in ContractOperations())
        {
            if (operation.TryGetProperty("requestBody", out var body) && body.GetProperty("content").TryGetProperty("application/json", out _))
            {
                foreach (var kind in new[] { "cut short", "an array", "nested 5000 deep", "oversized" })
                {
                    data.Add(id, method, path, kind);
                }
            }
        }

        return data;
    }

    [Theory]
    [MemberData(nameof(BrokenBodies))]
    public async Task A_broken_body_is_a_4xx_problem_never_a_500(string operationId, string method, string pathTemplate, string kind)
    {
        var before = await TenantRowCountsAsync(host.A.Id);
        var json = kind switch
        {
            "cut short" => "{\"name\":",
            "an array" => "[1,2,3]",
            "nested 5000 deep" => "{\"a\":" + new string('[', 5000) + new string(']', 5000) + "}",
            _ => "{\"name\":\"" + new string('a', Oversized) + "\"}",
        };

        var response = await SendRawAsync(method, AbusePath(pathTemplate), new StringContent(json, Encoding.UTF8, "application/json"));

        ((int)response.StatusCode).Should().BeInRange(400, 499, $"{operationId} with a body {kind}");
        if (kind == "oversized")
        {
            response.StatusCode.Should().Be(HttpStatusCode.RequestEntityTooLarge, $"{operationId} must refuse a body above its limit before reading it");
        }

        (await ProblemCodeAsync(response)).Should().NotBeNullOrEmpty($"{operationId} answers {kind} with a problem");
        (await TenantRowCountsAsync(host.A.Id)).Should().Equal(before);
    }

    public static TheoryData<string, string> PagedLists()
    {
        var data = new TheoryData<string, string>();
        foreach (var (id, method, path, operation) in ContractOperations())
        {
            if (method == "GET" && QueryParameters(operation).Contains("limit"))
            {
                data.Add(id, path);
            }
        }

        return data;
    }

    [Theory]
    [MemberData(nameof(PagedLists))]
    public async Task An_absurd_page_size_is_refused_or_capped(string operationId, string pathTemplate)
    {
        foreach (var limit in new[] { "1000000", "-1", "0", "2147483648", "ten" })
        {
            var response = await SendRawAsync("GET", $"{AbusePath(pathTemplate)}?limit={limit}", null);

            ((int)response.StatusCode).Should().BeLessThan(500, $"{operationId}?limit={limit}");
            if (response.StatusCode == HttpStatusCode.OK)
            {
                var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken)).RootElement;
                if (body.ValueKind == JsonValueKind.Object && body.TryGetProperty("items", out var items))
                {
                    items.GetArrayLength().Should().BeLessThanOrEqualTo(200, $"{operationId}?limit={limit} returns at most a page");
                }
            }
        }
    }

    public static TheoryData<string, string, string, string> JunkQueries()
    {
        var data = new TheoryData<string, string, string, string>();
        foreach (var (id, method, path, operation) in ContractOperations())
        {
            if (method == "GET")
            {
                foreach (var parameter in QueryParameters(operation))
                {
                    data.Add(id, path, parameter, "' OR 1=1 --");
                    data.Add(id, path, parameter, new string('9', 10_000));
                    data.Add(id, path, parameter, "%00%FF‮");
                }
            }
        }

        return data;
    }

    [Theory]
    [MemberData(nameof(JunkQueries))]
    public async Task A_junk_query_value_never_causes_a_500(string operationId, string pathTemplate, string parameter, string value)
    {
        var response = await SendRawAsync("GET", $"{AbusePath(pathTemplate)}?{parameter}={Uri.EscapeDataString(value)}", null);

        ((int)response.StatusCode).Should().BeLessThan(500, $"{operationId}?{parameter}={value[..Math.Min(value.Length, 20)]}");
    }

    /// <summary>Tenant A's resources where the isolation suite has them, any ID otherwise (abuse needs no real resource).</summary>
    private string AbusePath(string template) =>
        System.Text.RegularExpressions.Regex.Replace(template, "{([^}:]+)[^}]*}", match =>
            Uri.EscapeDataString(AttackValue(match.Groups[1].Value, template) ?? Guid.CreateVersion7().ToString()));

    private static IEnumerable<(string Id, string Method, string Path, JsonElement Operation)> ContractOperations()
    {
        foreach (var path in Contract.GetProperty("paths").EnumerateObject())
        {
            foreach (var operation in path.Value.EnumerateObject())
            {
                yield return (operation.Value.GetProperty("operationId").GetString()!, operation.Name.ToUpperInvariant(), path.Name, operation.Value);
            }
        }
    }

    private static HashSet<string> QueryParameters(JsonElement operation) =>
        operation.TryGetProperty("parameters", out var parameters)
            ? parameters.EnumerateArray().Where(p => p.GetProperty("in").GetString() == "query").Select(p => p.GetProperty("name").GetString()!).ToHashSet(StringComparer.Ordinal)
            : [];

    /// <summary>As company B's administrator, with a fresh idempotency key.</summary>
    private async Task<HttpResponseMessage> SendRawAsync(string method, string path, HttpContent? content)
    {
        using var request = new HttpRequestMessage(new HttpMethod(method), path) { Content = content };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", TestTokens.ForTenant(host.B.Id, IsolationHost.AttackerSubject));
        request.Headers.Add(IdempotencyOptions.HeaderName, Guid.NewGuid().ToString());
        return await host.Client.SendAsync(request, TestContext.Current.CancellationToken);
    }

    private static async Task<string?> ProblemCodeAsync(HttpResponseMessage response)
    {
        try
        {
            var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken)).RootElement;
            return body.TryGetProperty("code", out var code) ? code.GetString() : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
