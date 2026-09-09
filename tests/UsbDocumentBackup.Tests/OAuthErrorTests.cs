using UsbDocumentBackup.GoogleDrive;
using Xunit;

namespace UsbDocumentBackup.Tests;

/// <summary>
/// Every one of these is a Cloud console setting, not a bug in the app, so the message has to name
/// the page to go to. "403 access_denied" on its own sends the user nowhere.
/// </summary>
public sealed class OAuthErrorTests
{
    [Fact]
    public void Access_denied_points_at_the_test_user_list()
    {
        var message = GoogleConnection.ExplainAuthorizationFailure("access_denied", "ignored");

        Assert.Contains("테스트 사용자", message, StringComparison.Ordinal);
        Assert.Contains("대상", message, StringComparison.Ordinal);
        // The seven-day refresh-token expiry is the next thing that will bite them.
        Assert.Contains("7일", message, StringComparison.Ordinal);
        // Cancelling the consent screen produces the same code, so say so.
        Assert.Contains("취소", message, StringComparison.Ordinal);
    }

    [Fact]
    public void An_organisation_policy_block_says_it_is_the_administrator()
    {
        var message = GoogleConnection.ExplainAuthorizationFailure("admin_policy_enforced", "ignored");

        Assert.Contains("관리자", message, StringComparison.Ordinal);
        Assert.Contains("개인 Google 계정", message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_stale_client_tells_the_user_to_re_import_the_json()
    {
        var message = GoogleConnection.ExplainAuthorizationFailure("invalid_client", "ignored");
        Assert.Contains("다시 가져오", message, StringComparison.Ordinal);
    }

    [Fact]
    public void An_unrecognised_code_still_surfaces_the_original_text()
    {
        var message = GoogleConnection.ExplainAuthorizationFailure("something_new", "raw detail from Google");

        Assert.Contains("raw detail from Google", message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_missing_error_code_does_not_lose_the_detail()
    {
        var message = GoogleConnection.ExplainAuthorizationFailure(null, "raw detail");
        Assert.Contains("raw detail", message, StringComparison.Ordinal);
    }
}
