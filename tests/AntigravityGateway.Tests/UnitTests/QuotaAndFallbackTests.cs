using System.Net;
using AntigravityGateway.Configuration;
using AntigravityGateway.Services;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace AntigravityGateway.Tests.UnitTests;

public class QuotaAndFallbackTests
{
    [Fact]
    public void MultiAccount_Rotates_WhenAccountExhausted()
    {
        var auth = new AuthOptions
        {
            RefreshTokens = new List<string> { "token_1_alpha", "token_2_beta" }
        };

        var service = new AntigravityAccountService(auth, NullLogger<AntigravityAccountService>.Instance);

        var status1 = service.GetStatus();
        Assert.Equal(2, status1.TotalAccounts);
        Assert.Equal("env-account-1", status1.Email);

        // 模拟账号 1 配额耗尽
        service.MarkAccountQuotaExhausted(TimeSpan.FromMinutes(5), "429 RESOURCE_EXHAUSTED");

        var status2 = service.GetStatus();
        Assert.Equal("env-account-2", status2.Email);
    }

    [Fact]
    public void GatewaySettings_Parses_MultiTokens_Correctly()
    {
        var config = new Microsoft.Extensions.Configuration.ConfigurationManager();
        config["Gateway:DefaultModel"] = "gemini-3.8-flash-high";
        config["Gateway:EnableModelFallback"] = "true";

        Environment.SetEnvironmentVariable("ANTIGRAVITY_REFRESH_TOKENS", "tokenA, tokenB, tokenC");
        try
        {
            var settings = GatewaySettings.Load(config);
            Assert.True(settings.EnableModelFallback);
            Assert.Equal(3, settings.Auth.RefreshTokens.Count);
            Assert.Equal("tokenA", settings.Auth.RefreshTokens[0]);
            Assert.Equal("tokenB", settings.Auth.RefreshTokens[1]);
            Assert.Equal("tokenC", settings.Auth.RefreshTokens[2]);
        }
        finally
        {
            Environment.SetEnvironmentVariable("ANTIGRAVITY_REFRESH_TOKENS", null);
        }
    }

    [Fact]
    public void Provider_Initialized_WithFallbackEnabled()
    {
        var opts = new ProviderOptions
        {
            Type = "Antigravity",
            Project = "aicode-consumers"
        };

        var auth = new AuthOptions { RefreshToken = "test_token" };
        var accountService = new AntigravityAccountService(auth, NullLogger<AntigravityAccountService>.Instance);
        var gatewayOpts = new GatewayOptions { EnableModelFallback = true };

        var provider = new AntigravityProvider(
            opts,
            accountService,
            NullLogger<AntigravityProvider>.Instance,
            gatewayOpts);

        Assert.Equal("Antigravity", provider.Name);
        Assert.Contains("gemini-3.8-flash-high", provider.Models);
    }
}
