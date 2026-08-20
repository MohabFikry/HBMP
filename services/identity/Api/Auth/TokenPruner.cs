using OpenIddict.Abstractions;

namespace Mersal.Identity.Api.Auth;

/// <summary>
/// Phase 28.11 — deletes spent OAuth tokens and authorizations on a timer.
///
/// <para>
/// ============================================================================================================
/// WHAT WAS WRONG
/// ============================================================================================================
/// OpenIddict persists a row for every artefact it mints — access token, id_token, authorization code,
/// refresh token — and nothing in this service ever deleted one. The framework ships pruning as an opt-in
/// Quartz job (<c>.UseQuartz()</c> on <c>AddCore</c>), and <see cref="IssuerSetup"/> never opted in. So the
/// table only ever grew.
/// </para>
///
/// <para>
/// It is not a slow leak. An access token lives five minutes and its ROW lives forever, so a signed-in
/// person mints roughly a hundred dead rows a working day, plus an id_token beside each one and a refresh
/// rotation on top. The development database had reached 55,590 rows and 51 MB — fourteen percent of the
/// whole database — in sixteen days, of which all but about four hundred had expired. Production accrues
/// continuously rather than in test bursts, so it is the faster case, not the slower one.
/// </para>
///
/// <para>
/// ============================================================================================================
/// WHY DELETING THEM COSTS NO ACCOUNTABILITY
/// ============================================================================================================
/// This table is operational state, not the audit record. "Did this person sign in on the third" is answered
/// by <c>identity.login_attempt</c> — which records FAILURES as well as successes, and which
/// <c>SessionService.RecordAttemptAsync</c> writes on every attempt — and by the hash-chained audit store,
/// both of which carry their own 6–7 year retention (20-compliance-checklist §6). Nothing here is the sole
/// evidence of anything.
/// </para>
///
/// <para>
/// 20-compliance-checklist line 30 names "purge jobs" as the storage-limitation control and line 110 lists
/// "retention schedule configured and enforced by purge jobs" as an unchecked box. §6 puts transient data at
/// 30–90 days. A token is transient by construction, so the default sits at the conservative end of that.
/// </para>
///
/// <para>
/// ============================================================================================================
/// IT CANNOT CUT A LIVE SESSION
/// ============================================================================================================
/// OpenIddict's own predicate decides what goes: a token is prunable only if it was created before the
/// threshold AND is already expired or no longer valid. A token still backing a session satisfies neither,
/// at any threshold. The retention window therefore governs how far back this table can answer a forensic
/// question — not whether anybody stays signed in — and the floor below exists to keep a mistyped
/// configuration from being interesting rather than to protect sessions.
/// </para>
///
/// <para>Idempotent and safe on every node: the work is defined by the data, so several instances pruning at
/// once simply means the first one does it. No leader election, for the same reason
/// <c>ReportAccessExpirySweeper</c> needs none.</para>
/// </summary>
public sealed class TokenPruner(
    IServiceScopeFactory scopeFactory,
    IConfiguration config,
    TimeProvider clock,
    ILogger<TokenPruner> logger) : BackgroundService
{
    /// <summary>
    /// Daily. The rows being removed are already dead — nothing observes them between passes — so the only
    /// thing a tighter interval buys is more churn, and the only thing a looser one costs is disk.
    /// </summary>
    private static readonly TimeSpan Interval = TimeSpan.FromDays(1);

    /// <summary>The 30–90 day "transient" class of 20-compliance-checklist §6, at its conservative end.</summary>
    private const int DefaultRetentionDays = 30;

    /// <summary>
    /// A day, and the reason is the refresh token rather than the access token.
    ///
    /// <para>A refresh token lives ten hours (<see cref="IssuerSetup"/>, frozen at the SSO maximum), so any
    /// window shorter than that describes a retention policy narrower than the credential it retains — which
    /// is not a policy, it is a typo. OpenIddict would still refuse to prune the live ones, so this floor
    /// changes no behaviour; it turns a nonsensical configuration into a logged correction instead of a
    /// setting somebody later has to work out the effect of.</para>
    /// </summary>
    private const int MinimumRetentionDays = 1;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try { await PruneAsync(stoppingToken); }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            catch (Exception ex)
            {
                // Never let a bad pass kill the loop: tomorrow's pass picks up everything this one missed,
                // because the work is defined by the data and not by a cursor.
                logger.LogError(ex, "token pruning pass failed; retrying next interval");
            }

            try { await Task.Delay(Interval, stoppingToken); }
            catch (OperationCanceledException) { break; }
        }
    }

    /// <summary>The configured window, floored and reported when the configuration is not usable as written.</summary>
    private int RetentionDays()
    {
        var configured = config.GetValue<int?>("Issuer:TokenRetentionDays") ?? DefaultRetentionDays;
        if (configured >= MinimumRetentionDays) return configured;

        logger.LogWarning(
            "Issuer:TokenRetentionDays is {Configured}, which is below the {Minimum}-day floor; using the floor. "
            + "A window shorter than the refresh-token lifetime is not a retention policy.",
            configured, MinimumRetentionDays);
        return MinimumRetentionDays;
    }

    private async Task PruneAsync(CancellationToken ct)
    {
        using var scope = scopeFactory.CreateScope();
        var tokens = scope.ServiceProvider.GetRequiredService<IOpenIddictTokenManager>();
        var authorizations = scope.ServiceProvider.GetRequiredService<IOpenIddictAuthorizationManager>();

        var days = RetentionDays();
        var threshold = clock.GetUtcNow() - TimeSpan.FromDays(days);

        // TOKENS FIRST, then authorizations, and the order is load-bearing rather than arbitrary: an
        // authorization is only prunable once it has no valid token hanging off it, so clearing the tokens in
        // the same pass is what makes its parent eligible. The other order still converges — it just takes
        // until tomorrow to remove what could have gone today.
        await tokens.PruneAsync(threshold, ct);
        await authorizations.PruneAsync(threshold, ct);

        // Logged at Information every pass, including the passes that remove nothing. A maintenance job that
        // only speaks when it acts is indistinguishable from one that has silently stopped running — which is
        // exactly how this service went a whole phase without anybody noticing there was no pruning at all.
        logger.LogInformation(
            "pruned OAuth tokens and authorizations created before {Threshold:o} (retention {Days} day(s))",
            threshold, days);
    }
}
