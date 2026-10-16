using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using Common.Models;
using Infrastructure;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Moq;
using ReportingApi.Controllers;

namespace ReportingApi.Tests;

public class ReportsControllerTests
{
    private static ReportsController NewSut(out Mock<IClickFactQueryService> queryService, out Guid userId)
    {
        queryService = new Mock<IClickFactQueryService>();
        userId = Guid.NewGuid();
        var identity = new ClaimsIdentity([new Claim(JwtRegisteredClaimNames.Sub, userId.ToString())], "TestAuth");
        return new ReportsController(queryService.Object)
        {
            ControllerContext = new ControllerContext
            {
                HttpContext = new DefaultHttpContext { User = new ClaimsPrincipal(identity) },
            },
        };
    }

    [Fact]
    public async Task GetSummary_ValidHash_ReturnsOkWithSummary()
    {
        var sut = NewSut(out var queryService, out var userId);
        var summary = new ClickSummary("abc12345", 3, [], [], [], []);
        queryService.Setup(x => x.GetSummaryAsync("abc12345", userId, It.IsAny<CancellationToken>())).ReturnsAsync(summary);

        var result = await sut.GetSummary("abc12345", CancellationToken.None);

        var ok = Assert.IsType<OkObjectResult>(result);
        Assert.Same(summary, ok.Value);
    }

    [Fact]
    public async Task GetSummary_HashOwnedBySomeoneElseOrUnknown_ReturnsOkWithZeroedSummary()
    {
        // Regression: a hash with no reporting data of this caller's own (not owner, or truly
        // unknown) must come back exactly like a hash with zero clicks - see ReportsController's own
        // comment on why distinguishing "owned by someone else" (a 404) from "doesn't exist" (a 200)
        // was itself the privacy leak, not the fix for one.
        var sut = NewSut(out var queryService, out var userId);
        var zeroedSummary = new ClickSummary("abc12345", 0, [], [], [], []);
        queryService.Setup(x => x.GetSummaryAsync("abc12345", userId, It.IsAny<CancellationToken>())).ReturnsAsync(zeroedSummary);

        var result = await sut.GetSummary("abc12345", CancellationToken.None);

        var ok = Assert.IsType<OkObjectResult>(result);
        Assert.Same(zeroedSummary, ok.Value);
    }

    [Theory]
    [InlineData("")]
    [InlineData("has spaces")]
    [InlineData("has/slash")]
    [InlineData("waytoolongofahashvaluetoeverbereal")]
    public async Task GetSummary_InvalidHash_ReturnsProblem400WithoutQuerying(string hash)
    {
        var sut = NewSut(out var queryService, out _);

        var result = await sut.GetSummary(hash, CancellationToken.None);

        var problem = Assert.IsType<ObjectResult>(result);
        Assert.Equal(StatusCodes.Status400BadRequest, problem.StatusCode);
        queryService.Verify(x => x.GetSummaryAsync(It.IsAny<string>(), It.IsAny<Guid>(), It.IsAny<CancellationToken>()), Times.Never);
    }
}
