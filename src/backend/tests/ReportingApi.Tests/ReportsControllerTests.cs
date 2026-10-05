using Common.Models;
using Infrastructure;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Moq;
using ReportingApi.Controllers;

namespace ReportingApi.Tests;

public class ReportsControllerTests
{
    private static ReportsController NewSut(out Mock<IClickFactQueryService> queryService)
    {
        queryService = new Mock<IClickFactQueryService>();
        return new ReportsController(queryService.Object);
    }

    [Fact]
    public async Task GetSummary_ValidHash_ReturnsOkWithSummary()
    {
        var sut = NewSut(out var queryService);
        var summary = new ClickSummary("abc12345", 3, [], [], [], []);
        queryService.Setup(x => x.GetSummaryAsync("abc12345", It.IsAny<CancellationToken>())).ReturnsAsync(summary);

        var result = await sut.GetSummary("abc12345", CancellationToken.None);

        var ok = Assert.IsType<OkObjectResult>(result);
        Assert.Same(summary, ok.Value);
    }

    [Theory]
    [InlineData("")]
    [InlineData("has spaces")]
    [InlineData("has/slash")]
    [InlineData("waytoolongofahashvaluetoeverbereal")]
    public async Task GetSummary_InvalidHash_ReturnsProblem400WithoutQuerying(string hash)
    {
        var sut = NewSut(out var queryService);

        var result = await sut.GetSummary(hash, CancellationToken.None);

        var problem = Assert.IsType<ObjectResult>(result);
        Assert.Equal(StatusCodes.Status400BadRequest, problem.StatusCode);
        queryService.Verify(x => x.GetSummaryAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
    }
}
