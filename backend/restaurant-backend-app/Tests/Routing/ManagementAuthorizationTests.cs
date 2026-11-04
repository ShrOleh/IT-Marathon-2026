using Amazon.Lambda.APIGatewayEvents;
using Amazon.Lambda.Core;
using APIHandler.Handlers;
using APIHandler.Models.Users;
using APIHandler.Records;
using APIHandler.Routing;
using Moq;

namespace APIHandler.Tests.Routing;

public class ManagementAuthorizationTests
{
    private readonly ILambdaContext _context;

    public ManagementAuthorizationTests()
    {
        var context = new Mock<ILambdaContext>();
        context.Setup(c => c.Logger).Returns(Mock.Of<ILambdaLogger>());
        _context = context.Object;
    }

    public static IEnumerable<object[]> AdminRoutes() =>
    [
        ["POST", "/dishes", "/dishes"],
        ["PUT", "/dishes/{id}", "/dishes/dish-1"],
        ["DELETE", "/dishes/{id}", "/dishes/dish-1"],
        ["POST", "/locations", "/locations"],
        ["PUT", "/locations/{id}", "/locations/location-1"],
        ["DELETE", "/locations/{id}", "/locations/location-1"],
        ["POST", "/waiters", "/waiters"],
        ["DELETE", "/waiters", "/waiters"],
        ["POST", "/tables", "/tables"]
    ];

    [Theory]
    [MemberData(nameof(AdminRoutes))]
    public async Task AnonymousRequest_DoesNotInvokeMutation(
        string method, string template, string path)
    {
        var handler = new Mock<IApiRequestHandler>(MockBehavior.Strict);
        var function = CreateAdminFunction(method, template, handler);
        var request = CreateRequest(method, path);
        request.RequestContext = null;
        request.Headers = new Dictionary<string, string> { ["Authorization"] = "Bearer unverified-header" };

        var response = await function.FunctionHandler(request, _context);

        Assert.Equal(401, response.StatusCode);
        handler.VerifyNoOtherCalls();
    }

    [Theory]
    [MemberData(nameof(AdminRoutes))]
    public async Task CustomerRequest_DoesNotInvokeMutation(
        string method, string template, string path)
    {
        var handler = new Mock<IApiRequestHandler>(MockBehavior.Strict);
        var function = CreateAdminFunction(method, template, handler);
        var request = CreateRequest(method, path, new Dictionary<string, string>
        {
            ["sub"] = "customer-1",
            ["cognito:groups"] = "Customer"
        });

        var response = await function.FunctionHandler(request, _context);

        Assert.Equal(403, response.StatusCode);
        handler.VerifyNoOtherCalls();
    }

    [Theory]
    [MemberData(nameof(AdminRoutes))]
    public async Task AdminRequest_InvokesMutationOnce(
        string method, string template, string path)
    {
        var handler = new Mock<IApiRequestHandler>(MockBehavior.Strict);
        var function = CreateAdminFunction(method, template, handler);
        var request = CreateRequest(method, path, new Dictionary<string, string>
        {
            ["sub"] = "admin-1",
            ["cognito:groups"] = "[\"Customer\",\"Admin\"]"
        });
        var expected = new APIGatewayProxyResponse { StatusCode = 201 };
        handler.Setup(h => h.HandleRequest(request, _context)).ReturnsAsync(expected);

        var response = await function.FunctionHandler(request, _context);

        Assert.Same(expected, response);
        if (template.Contains("{id}"))
            Assert.Equal(path.Split('/')[2], request.PathParameters["id"]);
        handler.Verify(h => h.HandleRequest(request, _context), Times.Once);
        handler.VerifyNoOtherCalls();
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("Waiter")]
    [InlineData("SuperAdmin")]
    [InlineData("Customer,NotAdmin")]
    [InlineData("admin")]
    [InlineData("[\"SuperAdmin\"]")]
    [InlineData("[\"Admin\",]")]
    public async Task MissingOrNonExactAdminGroup_DoesNotInvokeMutation(string? groups)
    {
        var handler = new Mock<IApiRequestHandler>(MockBehavior.Strict);
        var function = CreateAdminFunction("POST", "/dishes", handler);
        var claims = new Dictionary<string, string> { ["sub"] = "user-1" };
        if (groups is not null)
            claims["cognito:groups"] = groups;

        var response = await function.FunctionHandler(CreateRequest("POST", "/dishes", claims), _context);

        Assert.Equal(403, response.StatusCode);
        handler.VerifyNoOtherCalls();
    }

    [Theory]
    [InlineData("Admin")]
    [InlineData("Customer, Admin")]
    [InlineData("[\"Admin\"]")]
    public async Task ExactAdminGroup_AllowsMutation(string groups)
    {
        var handler = new Mock<IApiRequestHandler>(MockBehavior.Strict);
        var function = CreateAdminFunction("POST", "/dishes", handler);
        var request = CreateRequest("POST", "/dishes", new Dictionary<string, string>
        {
            ["sub"] = "admin-1",
            ["cognito:groups"] = groups
        });
        handler.Setup(h => h.HandleRequest(request, _context))
            .ReturnsAsync(new APIGatewayProxyResponse { StatusCode = 201 });

        var response = await function.FunctionHandler(request, _context);

        Assert.Equal(201, response.StatusCode);
        handler.Verify(h => h.HandleRequest(request, _context), Times.Once);
        handler.VerifyNoOtherCalls();
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData(" ")]
    public async Task AdminGroupWithoutAuthenticatedSubject_DoesNotInvokeMutation(string? subject)
    {
        var handler = new Mock<IApiRequestHandler>(MockBehavior.Strict);
        var function = CreateAdminFunction("POST", "/dishes", handler);
        var claims = new Dictionary<string, string> { ["cognito:groups"] = "Admin" };
        if (subject is not null)
            claims["sub"] = subject;

        var response = await function.FunctionHandler(CreateRequest("POST", "/dishes", claims), _context);

        Assert.Equal(401, response.StatusCode);
        handler.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task PublicGet_StillInvokesHandlerWithoutAuthentication()
    {
        var handler = new Mock<IApiRequestHandler>(MockBehavior.Strict);
        var function = new global::APIHandler.Function(
            [new RouteHandler(new Route("GET", "/dishes"), handler.Object)]);
        var request = CreateRequest("GET", "/dishes");
        var expected = new APIGatewayProxyResponse { StatusCode = 200 };
        handler.Setup(h => h.HandleRequest(request, _context)).ReturnsAsync(expected);

        var response = await function.FunctionHandler(request, _context);

        Assert.Same(expected, response);
        handler.Verify(h => h.HandleRequest(request, _context), Times.Once);
        handler.VerifyNoOtherCalls();
    }

    private static global::APIHandler.Function CreateAdminFunction(
        string method, string template, Mock<IApiRequestHandler> handler) =>
        new([new RouteHandler(new Route(method, template, Roles.Admin), handler.Object)]);

    private static APIGatewayProxyRequest CreateRequest(
        string method, string path, Dictionary<string, string>? claims = null) => new()
    {
        HttpMethod = method,
        Path = path,
        RequestContext = new()
        {
            Authorizer = claims is null ? null : new() { Claims = claims }
        }
    };
}
