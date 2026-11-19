using Waymark.Host.Notifications;

namespace Waymark.Tests.Notifications;

public sealed class ApprovalNoticeTextTests
{
    [Fact]
    public void NothingWaitingGivesNoNotice()
    {
        Assert.Null(ApprovalNoticeText.For(new PendingDecisions(0, 0), 47100));
    }

    [Fact]
    public void OneCommandUsesTheSingularAndOpensTheConsole()
    {
        var notice = ApprovalNoticeText.For(new PendingDecisions(1, 0), 47100)!;

        Assert.Equal(ApprovalNoticeText.Title, notice.Title);
        Assert.StartsWith("1 command is waiting.", notice.Message, StringComparison.Ordinal);
        Assert.Contains("localhost:47100", notice.Message, StringComparison.Ordinal);
        Assert.Equal("http://localhost:47100/console", notice.OpenUri.AbsoluteUri);
    }

    [Fact]
    public void OnlyPairingsOpenTheApprovalsPage()
    {
        var notice = ApprovalNoticeText.For(new PendingDecisions(0, 2), 5000)!;

        Assert.StartsWith("2 app connections are waiting.", notice.Message, StringComparison.Ordinal);
        Assert.Contains("Approvals page", notice.Message, StringComparison.Ordinal);
        Assert.Equal("http://localhost:5000/approvals", notice.OpenUri.AbsoluteUri);
    }

    [Fact]
    public void TheTestNoticeOpensTheSetupPage()
    {
        var notice = ApprovalNoticeText.Test(47100);

        Assert.Equal("http://localhost:47100/setup", notice.OpenUri.AbsoluteUri);
    }
}
