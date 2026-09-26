using System.Threading.RateLimiting;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;

namespace FieldReport.Api.Infrastructure;

/// <summary>Limity żądań na adres IP (okno stałe 1 min). Globalny + ostrzejsze dla logowania i dyspozytora.</summary>
public static class RateLimiting
{
    public const string AuthPolicy = "auth";
    public const string DispatcherPolicy = "dispatcher";

    public static IServiceCollection AddFieldReportRateLimiting(this IServiceCollection services)
    {
        services.AddRateLimiter(o =>
        {
            o.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
            o.OnRejected = async (context, ct) =>
            {
                context.HttpContext.Response.Headers.RetryAfter = "60";
                await context.HttpContext.Response.WriteAsJsonAsync(new ProblemDetails
                {
                    Title = "Za dużo żądań",
                    Detail = "Przekroczono limit żądań z tego adresu. Spróbuj ponownie za minutę.",
                    Status = StatusCodes.Status429TooManyRequests
                }, options: null, contentType: "application/problem+json", cancellationToken: ct);
            };

            o.GlobalLimiter = PartitionedRateLimiter.Create<HttpContext, string>(ctx => Partition(ctx, "global", x => x.PermitsPerMinute));
            o.AddPolicy(AuthPolicy, ctx => Partition(ctx, AuthPolicy, x => x.AuthPermitsPerMinute));
            o.AddPolicy(DispatcherPolicy, ctx => Partition(ctx, DispatcherPolicy, x => x.DispatcherPermitsPerMinute));
        });
        return services;
    }

    private static RateLimitPartition<string> Partition(HttpContext ctx, string name, Func<RateLimitOptions, int> limit)
    {
        var options = ctx.RequestServices.GetRequiredService<IOptionsMonitor<RateLimitOptions>>().CurrentValue;
        if (!options.Enabled) return RateLimitPartition.GetNoLimiter("off");
        var permits = Math.Max(1, limit(options));
        var ip = ctx.Connection.RemoteIpAddress?.ToString() ?? "unknown";
        return RateLimitPartition.GetFixedWindowLimiter($"{name}|{ip}|{permits}", _ => new FixedWindowRateLimiterOptions
        {
            PermitLimit = permits,
            Window = TimeSpan.FromMinutes(1),
            QueueLimit = 0
        });
    }
}
