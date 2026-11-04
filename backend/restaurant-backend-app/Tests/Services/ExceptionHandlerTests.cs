using Amazon.Lambda.Core;
using APIHandler.Exceptions;
using Function.Services;
using Moq;
using System.Text.Json;

namespace APIHandler.Tests.Services;

public class ExceptionHandlerTests
{
    private readonly ILambdaContext _context;

    public ExceptionHandlerTests()
    {
        var context = new Mock<ILambdaContext>();
        context.SetupGet(value => value.Logger).Returns(Mock.Of<ILambdaLogger>());
        _context = context.Object;
    }

    [Fact]
    public void Handle_ReturnsReadableValidationErrorAsBadRequest()
    {
        var response = ExceptionHandler.Handle(new ValidationException("Images must be 4 MiB or smaller."), _context);

        Assert.Equal(400, response.StatusCode);
        using var body = JsonDocument.Parse(response.Body);
        Assert.Equal("Images must be 4 MiB or smaller.", body.RootElement.GetProperty("error").GetString());
    }

    [Fact]
    public void Handle_HidesUnexpectedErrorDetails()
    {
        var response = ExceptionHandler.Handle(new InvalidOperationException("Private service details"), _context);

        Assert.Equal(500, response.StatusCode);
        using var body = JsonDocument.Parse(response.Body);
        Assert.Equal("Internal Server Error", body.RootElement.GetProperty("error").GetString());
        Assert.DoesNotContain("Private service details", response.Body);
    }
}
