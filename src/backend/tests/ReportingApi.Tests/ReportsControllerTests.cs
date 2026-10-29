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
    private static ReportsController NewSut(out Mock<IClickFactQueryService> queryService, out Guid userId) =>
        NewSut(out queryService, out _, out userId);

    private static ReportsController NewSut(
        out Mock<IClickFactQueryService> queryService, out Mock<IClickMetaQueryService> clickMetaQueryService, out Guid userId)
    {
        queryService = new Mock<IClickFactQueryService>();
        clickMetaQueryService = new Mock<IClickMetaQueryService>();
        userId = Guid.NewGuid();
        var identity = new ClaimsIdentity([new Claim(JwtRegisteredClaimNames.Sub, userId.ToString())], "TestAuth");
        return new ReportsController(queryService.Object, clickMetaQueryService.Object)
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

    [Fact]
    public async Task GetRecentClicks_ValidHash_ReturnsOkWithClicks()
    {
        var sut = NewSut(out _, out var clickMetaQueryService, out var userId);
        var clicks = new[] { new ClickDetail(Guid.NewGuid(), DateTime.UtcNow, "ua", "ref", "Chrome", "Windows", "Desktop", "US", "Seattle") };
        clickMetaQueryService.Setup(x => x.GetRecentAsync("abc12345", userId, 20, It.IsAny<CancellationToken>())).ReturnsAsync(clicks);

        var result = await sut.GetRecentClicks("abc12345", 0, CancellationToken.None);

        var ok = Assert.IsType<OkObjectResult>(result);
        Assert.Same(clicks, ok.Value);
    }

    [Theory]
    [InlineData(0, 20)]
    [InlineData(-5, 20)]
    [InlineData(50, 50)]
    [InlineData(500, 100)]
    public async Task GetRecentClicks_ClampsLimitIntoValidRange(int requestedLimit, int expectedLimit)
    {
        var sut = NewSut(out _, out var clickMetaQueryService, out var userId);
        clickMetaQueryService
            .Setup(x => x.GetRecentAsync("abc12345", userId, expectedLimit, It.IsAny<CancellationToken>()))
            .ReturnsAsync([]);

        var result = await sut.GetRecentClicks("abc12345", requestedLimit, CancellationToken.None);

        Assert.IsType<OkObjectResult>(result);
        clickMetaQueryService.Verify(x => x.GetRecentAsync("abc12345", userId, expectedLimit, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Theory]
    [InlineData("")]
    [InlineData("has spaces")]
    [InlineData("has/slash")]
    [InlineData("waytoolongofahashvaluetoeverbereal")]
    public async Task GetRecentClicks_InvalidHash_ReturnsProblem400WithoutQuerying(string hash)
    {
        var sut = NewSut(out _, out var clickMetaQueryService, out _);

        var result = await sut.GetRecentClicks(hash, 20, CancellationToken.None);

        var problem = Assert.IsType<ObjectResult>(result);
        Assert.Equal(StatusCodes.Status400BadRequest, problem.StatusCode);
        clickMetaQueryService.Verify(
            x => x.GetRecentAsync(It.IsAny<string>(), It.IsAny<Guid>(), It.IsAny<int>(), It.IsAny<CancellationToken>()), Times.Never);
    }
}
