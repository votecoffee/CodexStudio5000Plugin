using System.Net;
using System.Text.Json;
using Studio5000.Client;
using Xunit;

namespace Studio5000.Tests;

public sealed class ServiceErrorTests
{
    [Fact]
    public void Structured_errors_keep_the_useful_backend_code_message_and_HTTP_status_across_worker_wrappers()
    {
        var inner = JsonSerializer.Serialize(new { code = "validation_policy", message = "Protected payload normalization is available in legacy-review-readback only." });
        var outer = JsonSerializer.Serialize(new { code = "extension_operation_failed", message = inner });
        var error = ServiceError.Parse(HttpStatusCode.BadRequest, outer);
        Assert.Equal("validation_policy", error.Code); Assert.Equal(400, error.HttpStatus); Assert.Equal(["extension_operation_failed"], error.WrapperCodes); Assert.Contains("legacy-review-readback", error.Message);
        var exception = new ServiceErrorException(error); Assert.Equal(HttpStatusCode.BadRequest, exception.StatusCode);
        Assert.Equal("validation_policy", JsonDocument.Parse(exception.Message).RootElement.GetProperty("code").GetString());
        var next = ServiceError.Parse(HttpStatusCode.BadRequest, JsonSerializer.Serialize(new { code = "extension_operation_failed", message = exception.Message })); Assert.Equal(error.Code, next.Code); Assert.Equal(error.Message, next.Message);
    }
    [Theory]
    [InlineData("<html>secret transport body</html>")]
    [InlineData("{\"code\":42,\"message\":\"secret transport body\"}")]
    [InlineData("{\"code\":\"BAD CODE\",\"message\":\"secret transport body\"}")]
    [InlineData("{\"code\":\"failure\",\"message\":null}")]
    public void Unknown_or_malformed_HTTP_bodies_are_not_exposed_as_trusted_backend_messages(string body)
    { var error = ServiceError.Parse(HttpStatusCode.InternalServerError, body); Assert.Equal("service_http_error", error.Code); Assert.DoesNotContain("secret", error.ToJson()); Assert.Equal(500, error.HttpStatus); }
    [Fact]
    public void Error_envelopes_are_bounded_and_only_known_worker_wrappers_are_unwrapped()
    {
        Assert.Equal("service_http_error", ServiceError.Parse(HttpStatusCode.BadRequest, new string('x', 20000)).Code);
        var inner = "{\"code\":\"validation_policy\",\"message\":\"useful\"}";
        var error = ServiceError.Parse(HttpStatusCode.BadRequest, JsonSerializer.Serialize(new { code = "native_failure", message = inner }));
        Assert.Equal("native_failure", error.Code); Assert.Equal(inner, error.Message); Assert.Empty(error.WrapperCodes);
    }
}
