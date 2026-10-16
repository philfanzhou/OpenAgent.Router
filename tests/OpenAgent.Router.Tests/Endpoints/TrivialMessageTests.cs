using OpenAgent.Router.Endpoints;
using Xunit;

namespace OpenAgent.Router.Tests.Endpoints;

public class TrivialMessageTests
{
    [Theory]
    [InlineData("ok")]
    [InlineData("OK!")]
    [InlineData("好的")]
    [InlineData("好的，继续")]
    [InlineData("好的继续")]
    [InlineData("嗯嗯")]
    [InlineData("继续吧")]
    [InlineData("谢谢")]
    [InlineData("好的谢谢！")]
    [InlineData("没问题。")]
    [InlineData("thanks")]
    [InlineData("Thank you")]
    [InlineData("ok, continue")]
    [InlineData("go on")]
    [InlineData("是的")]
    [InlineData("收到，明白")]
    public void Acknowledgements_AreTrivial(string message) => Assert.True(TrivialMessage.IsTrivial(message));

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("帮我分析这份订单的退款原因")]
    [InlineData("对账差了三千块")]
    [InlineData("银行流水怎么导出")]
    [InlineData("好的，帮我查一下发票")]
    [InlineData("ok, show me the refund report")]
    [InlineData("继续上次的报销流程说明")]
    [InlineData("可以吗？")]
    [InlineData("ok ok ok ok ok")]
    public void RealRequests_AreNotTrivial(string message) => Assert.False(TrivialMessage.IsTrivial(message));
}
