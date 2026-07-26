using System.Security.Claims;
using System.Text;
using System.Text.Encodings.Web;
using Mersal.Identity.Domain;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;

namespace Mersal.Identity.Api.Auth;

/// <summary>
/// The in-app login + 2FA surface the issuer serves (17.3). Accessible, bilingual (EN + Arabic RTL) HTML
/// pages: password sign-in, TOTP challenge, authenticator enrolment, and recovery codes. On a completed
/// factor the application cookie is stamped with the <c>amr</c> claims (pwd, otp) the token then carries, so
/// <c>MfaEvaluator</c> lets an MFA session reach protected scopes (closes the 16.3 MFA gap on the new issuer).
/// See docs/security/token-contract.md §3.
/// </summary>
public static class AccountPages
{
    public const string AmrClaim = "amr";

    public static void MapAccount(this WebApplication app)
    {
        // ---- TOTP challenge (after a password sign-in that RequiresTwoFactor) ------------------------------
        app.MapGet("/connect/2fa", (HttpContext http, string? returnUrl) =>
            Results.Content(TwoFactorPage(Lang(http), returnUrl), "text/html"));

        app.MapPost("/connect/2fa", async (
            HttpContext http, [FromForm] string code, [FromForm] bool? recovery, [FromForm] string? returnUrl,
            SignInManager<ApplicationUser> signIn, UserManager<ApplicationUser> users) =>
        {
            var user = await signIn.GetTwoFactorAuthenticationUserAsync();
            if (user is null) return Results.Redirect("/connect/login");

            var stripped = code.Replace(" ", "").Replace("-", "");
            var ok = recovery is true
                ? (await signIn.TwoFactorRecoveryCodeSignInAsync(stripped)).Succeeded
                : (await signIn.TwoFactorAuthenticatorSignInAsync(stripped, isPersistent: false, rememberClient: false)).Succeeded;
            if (!ok)
                return Results.Content(TwoFactorPage(Lang(http), returnUrl, error: true), "text/html");

            await StampSignIn(http, signIn, user, ["pwd", "otp"]);
            return Results.Redirect(SafeReturn(returnUrl));
        }).DisableAntiforgery();

        // ---- Authenticator enrolment (signed-in user) ------------------------------------------------------
        app.MapGet("/connect/enroll-2fa", async (HttpContext http, UserManager<ApplicationUser> users) =>
        {
            var user = await users.GetUserAsync(http.User);
            if (user is null) return Results.Redirect("/connect/login");
            var key = await EnsureAuthenticatorKey(users, user);
            return Results.Content(EnrollPage(Lang(http), user.UserName ?? "", key), "text/html");
        }).RequireAuthorization();

        app.MapPost("/connect/enroll-2fa", async (
            HttpContext http, [FromForm] string code, SignInManager<ApplicationUser> signIn, UserManager<ApplicationUser> users) =>
        {
            var user = await users.GetUserAsync(http.User);
            if (user is null) return Results.Redirect("/connect/login");

            var stripped = code.Replace(" ", "").Replace("-", "");
            var valid = await users.VerifyTwoFactorTokenAsync(
                user, users.Options.Tokens.AuthenticatorTokenProvider, stripped);
            if (!valid)
            {
                var key = await EnsureAuthenticatorKey(users, user);
                return Results.Content(EnrollPage(Lang(http), user.UserName ?? "", key, error: true), "text/html");
            }

            await users.SetTwoFactorEnabledAsync(user, true);
            var codes = await users.GenerateNewTwoFactorRecoveryCodesAsync(user, 10);
            // The enrolling session has now proven a second factor.
            await StampSignIn(http, signIn, user, ["pwd", "otp"]);
            return Results.Content(RecoveryCodesPage(Lang(http), codes?.ToArray() ?? []), "text/html");
        }).RequireAuthorization().DisableAntiforgery();
    }

    /// <summary>Re-issue the application cookie with explicit <c>amr</c> claims recording the factors performed
    /// (the SignInManager helpers don't carry amr). The authorize endpoint reads these onto the token.</summary>
    public static async Task StampSignIn(
        HttpContext http, SignInManager<ApplicationUser> signIn, ApplicationUser user, string[] amr)
    {
        var principal = await signIn.CreateUserPrincipalAsync(user);
        if (principal.Identity is ClaimsIdentity identity)
            foreach (var a in amr.Distinct(StringComparer.OrdinalIgnoreCase))
                identity.AddClaim(new Claim(AmrClaim, a));
        await http.SignInAsync(IdentityConstants.ApplicationScheme, principal,
            new AuthenticationProperties { IsPersistent = false });
    }

    private static async Task<string> EnsureAuthenticatorKey(UserManager<ApplicationUser> users, ApplicationUser user)
    {
        var key = await users.GetAuthenticatorKeyAsync(user);
        if (string.IsNullOrEmpty(key))
        {
            await users.ResetAuthenticatorKeyAsync(user);
            key = await users.GetAuthenticatorKeyAsync(user);
        }
        return key ?? "";
    }

    // ---- rendering ----------------------------------------------------------------------------------------

    private static string Lang(HttpContext http) =>
        http.Request.Query["lang"] == "ar" ? "ar" : "en";

    private static readonly HtmlEncoder Enc = HtmlEncoder.Default;

    private static string Layout(string lang, string title, string body)
    {
        var dir = lang == "ar" ? "rtl" : "ltr";
        return $$"""
        <!doctype html><html lang="{{lang}}" dir="{{dir}}"><head>
        <meta charset="utf-8"><meta name="viewport" content="width=device-width, initial-scale=1">
        <title>{{Enc.Encode(title)}}</title>
        <style>
          :root{color-scheme:light dark}
          body{font-family:system-ui,Segoe UI,Roboto,'Noto Naskh Arabic',sans-serif;max-width:26rem;margin:3rem auto;padding:0 1rem;line-height:1.5}
          h1{font-size:1.4rem} label{display:block;margin:.75rem 0 .25rem;font-weight:600}
          input{width:100%;min-height:44px;padding:.5rem;box-sizing:border-box;font-size:1rem}
          button{min-height:44px;padding:0 1.25rem;font-weight:600;margin-top:1rem;cursor:pointer}
          .err{color:#b91c1c} .muted{opacity:.75} code{word-break:break-all;background:#8882;padding:.15rem .35rem}
          a{color:inherit} :focus-visible{outline:3px solid #2563eb;outline-offset:2px}
        </style></head><body>{{body}}</body></html>
        """;
    }

    public static string LoginPage(string lang, string? returnUrl, bool error = false)
    {
        var s = Strings(lang);
        var ret = Enc.Encode(SafeReturn(returnUrl));
        var err = error ? $"<p class=\"err\" role=\"alert\">{s["loginError"]}</p>" : "";
        var body = $"""
        <h1>{s["signIn"]}</h1>{err}
        <form method="post" action="/connect/login">
          <input type="hidden" name="returnUrl" value="{ret}" />
          <label for="u">{s["username"]}</label>
          <input id="u" name="username" autocomplete="username" required autofocus>
          <label for="p">{s["password"]}</label>
          <input id="p" name="password" type="password" autocomplete="current-password" required>
          <button type="submit">{s["signIn"]}</button>
        </form>
        """;
        return Layout(lang, s["signIn"], body);
    }

    private static string TwoFactorPage(string lang, string? returnUrl, bool error = false)
    {
        var s = Strings(lang);
        var ret = Enc.Encode(SafeReturn(returnUrl));
        var err = error ? $"<p class=\"err\" role=\"alert\">{s["codeError"]}</p>" : "";
        var body = $"""
        <h1>{s["twoFactor"]}</h1><p class="muted">{s["twoFactorHint"]}</p>{err}
        <form method="post" action="/connect/2fa">
          <input type="hidden" name="returnUrl" value="{ret}" />
          <label for="c">{s["code"]}</label>
          <input id="c" name="code" inputmode="numeric" autocomplete="one-time-code" required autofocus>
          <label><input type="checkbox" name="recovery" value="true" style="width:auto;min-height:auto"> {s["useRecovery"]}</label>
          <button type="submit">{s["verify"]}</button>
        </form>
        """;
        return Layout(lang, s["twoFactor"], body);
    }

    private static string EnrollPage(string lang, string username, string key, bool error = false)
    {
        var s = Strings(lang);
        var err = error ? $"<p class=\"err\" role=\"alert\">{s["codeError"]}</p>" : "";
        // otpauth URI so an authenticator app can be provisioned by paste (a QR is added SPA-side in 17.5).
        var issuer = Uri.EscapeDataString("Mersal HBMP");
        var label = Uri.EscapeDataString(username);
        var otpauth = $"otpauth://totp/{issuer}:{label}?secret={key}&issuer={issuer}";
        var body = $"""
        <h1>{s["enroll"]}</h1><p class="muted">{s["enrollHint"]}</p>{err}
        <p>{s["key"]}: <code>{Enc.Encode(FormatKey(key))}</code></p>
        <p class="muted"><code>{Enc.Encode(otpauth)}</code></p>
        <form method="post" action="/connect/enroll-2fa">
          <label for="c">{s["code"]}</label>
          <input id="c" name="code" inputmode="numeric" autocomplete="one-time-code" required autofocus>
          <button type="submit">{s["verifyEnable"]}</button>
        </form>
        """;
        return Layout(lang, s["enroll"], body);
    }

    private static string RecoveryCodesPage(string lang, string[] codes)
    {
        var s = Strings(lang);
        var list = string.Concat(codes.Select(c => $"<li><code>{Enc.Encode(c)}</code></li>"));
        var body = $"""
        <h1>{s["recovery"]}</h1><p class="err" role="alert">{s["recoveryIntro"]}</p>
        <ul>{list}</ul>
        <p><a href="/">{s["done"]}</a></p>
        """;
        return Layout(lang, s["recovery"], body);
    }

    private static string FormatKey(string key)
    {
        var sb = new StringBuilder();
        for (var i = 0; i < key.Length; i += 4)
            sb.Append(key.AsSpan(i, Math.Min(4, key.Length - i))).Append(' ');
        return sb.ToString().Trim().ToLowerInvariant();
    }

    public static string SafeReturn(string? returnUrl) =>
        !string.IsNullOrEmpty(returnUrl) && returnUrl.StartsWith('/') && !returnUrl.StartsWith("//", StringComparison.Ordinal)
            ? returnUrl : "/";

    private static Dictionary<string, string> Strings(string lang) => lang == "ar"
        ? new()
        {
            ["signIn"] = "تسجيل الدخول", ["username"] = "اسم المستخدم", ["password"] = "كلمة المرور",
            ["loginError"] = "بيانات الدخول غير صحيحة.", ["twoFactor"] = "التحقق بخطوتين",
            ["twoFactorHint"] = "أدخل الرمز من تطبيق المصادقة.", ["code"] = "الرمز", ["verify"] = "تحقّق",
            ["useRecovery"] = "استخدام رمز الاسترداد", ["codeError"] = "رمز غير صحيح.",
            ["enroll"] = "تفعيل التحقق بخطوتين", ["enrollHint"] = "أضف المفتاح إلى تطبيق المصادقة ثم أدخل الرمز.",
            ["key"] = "المفتاح", ["verifyEnable"] = "تحقّق وفعّل", ["recovery"] = "رموز الاسترداد",
            ["recoveryIntro"] = "احفظ هذه الرموز في مكان آمن — تظهر مرة واحدة.", ["done"] = "تم",
        }
        : new()
        {
            ["signIn"] = "Sign in", ["username"] = "Username", ["password"] = "Password",
            ["loginError"] = "Invalid credentials.", ["twoFactor"] = "Two-factor verification",
            ["twoFactorHint"] = "Enter the code from your authenticator app.", ["code"] = "Code", ["verify"] = "Verify",
            ["useRecovery"] = "Use a recovery code instead", ["codeError"] = "Invalid code.",
            ["enroll"] = "Enable two-factor", ["enrollHint"] = "Add the key to your authenticator app, then enter the code.",
            ["key"] = "Key", ["verifyEnable"] = "Verify & enable", ["recovery"] = "Recovery codes",
            ["recoveryIntro"] = "Save these codes somewhere safe — they are shown once.", ["done"] = "Done",
        };
}
