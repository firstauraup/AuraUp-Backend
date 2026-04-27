using System.Globalization;
using System.Net;
using System.Text;
using AuraUpBack.Domain.Entities;
using AuraUpBack.Infrastructure.Abstractions;

namespace AuraUpBack.Infrastructure.Services;

internal sealed class EmailTemplateRenderer : IEmailTemplateRenderer
{
    public RenderedEmail RenderApplicationFormSubmittedEmail(ApplicationFormSubmission submission)
    {
        var rows = new[]
        {
            BuildRow("Email", submission.Email),
            BuildRow("Telefono", submission.PhoneNumber),
            BuildRow("Nombre completo", submission.FullName),
            BuildRow("Empresa", submission.CompanyName),
            BuildRow("Red principal", submission.PrimaryNetwork),
            BuildRow("Fecha", FormatDate(submission.CreatedAtUtc))
        };

        var html = BuildShell(
            badge: "Nuevo formulario",
            title: "Llego una nueva solicitud desde la web",
            intro: "Este lead completo el formulario principal de AuraUp. Revisa los datos y da seguimiento cuanto antes.",
            body: BuildTable(rows));

        var text = $"""
        Nuevo formulario recibido en AuraUp

        Email: {submission.Email}
        Telefono: {submission.PhoneNumber}
        Nombre completo: {submission.FullName}
        Empresa: {submission.CompanyName}
        Red principal: {submission.PrimaryNetwork}
        Fecha: {FormatDate(submission.CreatedAtUtc)}
        """;

        return new RenderedEmail("Nuevo formulario recibido en AuraUp", html, text);
    }

    public RenderedEmail RenderInvitationEmail(string email, string roleLabel, string invitationUrl, DateTime expiresAtUtc)
    {
        var body = new StringBuilder();
        body.AppendLine("<p class=\"intro\">Te invitaron a AuraUp como <strong>" + Encode(roleLabel) + "</strong>. Usa el boton para completar tu registro y terminar tu perfil.</p>");
        body.AppendLine(BuildCallout("Acceso listo", "Este enlace te lleva directo al onboarding para completar tus datos y definir tu contrasena."));
        body.AppendLine(BuildButton(invitationUrl, "Completar registro"));
        body.AppendLine(BuildMetaList(new[]
        {
            ("Correo", email),
            ("Rol", roleLabel),
            ("Expira", FormatDate(expiresAtUtc))
        }));

        var html = BuildShell(
            badge: "Invitacion AuraUp",
            title: "Tu acceso ya esta listo",
            intro: null,
            body: body.ToString());

        var text = $"""
        Tu acceso a AuraUp ya esta listo

        Rol: {roleLabel}
        Correo: {email}
        Completa tu registro aqui: {invitationUrl}
        Expira: {FormatDate(expiresAtUtc)}
        """;

        return new RenderedEmail("Completa tu registro en AuraUp", html, text);
    }

    public RenderedEmail RenderViralAlertEmail(TrackedAccount account, TrackedPost post, AlertSignal alert)
    {
        var body = new StringBuilder();
        body.AppendLine("<p class=\"intro\">Se detecto un reel con comportamiento fuera de rango en una cuenta monitoreada. Tienes el resumen listo para revisar y actuar rapido.</p>");
        body.AppendLine(BuildCallout("Cuenta", "@" + Encode(account.Handle)));
        body.AppendLine(BuildMetricsGrid(new[]
        {
            ("Multiplicador", post.PerformanceLabel),
            ("Views", post.Views.ToString("N0", CultureInfo.InvariantCulture)),
            ("Likes", post.Likes.ToString("N0", CultureInfo.InvariantCulture)),
            ("Comments", post.Comments.ToString("N0", CultureInfo.InvariantCulture)),
            ("Shares", post.Shares.ToString("N0", CultureInfo.InvariantCulture)),
            ("Severidad", alert.Severity)
        }));
        body.AppendLine(BuildMetaList(new[]
        {
            ("Post ID", post.ExternalId),
            ("Publicado", FormatDate(post.PublishedAtUtc)),
            ("Topic", string.IsNullOrWhiteSpace(post.Topic) ? "Sin clasificar" : post.Topic),
            ("Mensaje", alert.Message)
        }));

        if (!string.IsNullOrWhiteSpace(post.Caption))
        {
            body.AppendLine("<div class=\"note\"><span class=\"note-label\">Caption</span><p>" + Encode(post.Caption) + "</p></div>");
        }

        if (!string.IsNullOrWhiteSpace(post.Url))
        {
            body.AppendLine(BuildButton(post.Url, "Abrir reel"));
        }

        var html = BuildShell(
            badge: "Alerta viral",
            title: alert.Title,
            intro: null,
            body: body.ToString());

        var text = $"""
        Alerta viral en AuraUp

        Cuenta: @{account.Handle}
        Titulo: {alert.Title}
        Mensaje: {alert.Message}
        Multiplicador: {post.PerformanceLabel}
        Views: {post.Views.ToString("N0", CultureInfo.InvariantCulture)}
        Likes: {post.Likes.ToString("N0", CultureInfo.InvariantCulture)}
        Comments: {post.Comments.ToString("N0", CultureInfo.InvariantCulture)}
        Shares: {post.Shares.ToString("N0", CultureInfo.InvariantCulture)}
        Topic: {(string.IsNullOrWhiteSpace(post.Topic) ? "Sin clasificar" : post.Topic)}
        Reel: {post.Url}
        """;

        return new RenderedEmail($"Alerta viral: @{account.Handle}", html, text);
    }

    private static string BuildShell(string badge, string title, string? intro, string body)
    {
        var introHtml = string.IsNullOrWhiteSpace(intro) ? string.Empty : $"<p class=\"intro\">{Encode(intro)}</p>";

        return $$"""
        <!doctype html>
        <html lang="es">
          <head>
            <meta charset="UTF-8" />
            <meta name="viewport" content="width=device-width, initial-scale=1.0" />
            <title>{{Encode(title)}}</title>
            <style>
              body { margin: 0; padding: 0; background: #f3f5f8; font-family: "Segoe UI", Arial, sans-serif; color: #111827; }
              .shell { width: 100%; padding: 28px 12px; background:
                radial-gradient(circle at top left, rgba(196, 181, 253, 0.14), transparent 28%),
                radial-gradient(circle at top right, rgba(59, 130, 246, 0.12), transparent 24%),
                #f3f5f8; }
              .card { max-width: 680px; margin: 0 auto; background: #ffffff; border: 1px solid #dbe2ea; border-radius: 18px; overflow: hidden; }
              .head { padding: 28px 28px 20px; background: linear-gradient(135deg, #0f172a 0%, #1e293b 100%); color: #f8fafc; }
              .badge { display: inline-block; padding: 6px 10px; border-radius: 999px; background: #f59e0b; color: #111827; font-size: 12px; font-weight: 700; letter-spacing: 0.02em; }
              .title { margin: 14px 0 0; font-size: 26px; line-height: 1.25; }
              .body { padding: 24px 28px 28px; }
              .intro { margin: 0 0 18px; color: #475569; font-size: 14px; line-height: 1.6; }
              .table { width: 100%; border-collapse: collapse; border: 1px solid #e2e8f0; border-radius: 14px; overflow: hidden; }
              .table td { padding: 12px 14px; border-bottom: 1px solid #e2e8f0; font-size: 14px; line-height: 1.5; vertical-align: top; }
              .table tr:last-child td { border-bottom: 0; }
              .label { width: 220px; font-weight: 700; color: #334155; background: #f8fafc; }
              .value { color: #0f172a; }
              .button-wrap { margin: 24px 0 8px; }
              .button { display: inline-block; padding: 14px 20px; border-radius: 12px; background: #0f172a; color: #ffffff !important; text-decoration: none; font-weight: 700; }
              .callout { margin: 0 0 18px; padding: 16px 18px; border: 1px solid #dbeafe; border-radius: 14px; background: #eff6ff; }
              .callout-title { margin: 0 0 6px; font-size: 13px; font-weight: 700; color: #1d4ed8; text-transform: uppercase; letter-spacing: 0.04em; }
              .callout-text { margin: 0; font-size: 14px; color: #1e293b; }
              .meta { margin: 18px 0 0; padding: 0; list-style: none; }
              .meta li { margin: 0 0 10px; font-size: 14px; line-height: 1.5; color: #334155; }
              .meta strong { color: #0f172a; }
              .metrics { width: 100%; margin: 0 0 18px; border-spacing: 10px; }
              .metric { width: 50%; padding: 14px; border-radius: 14px; background: #f8fafc; border: 1px solid #e2e8f0; }
              .metric-label { display: block; margin-bottom: 6px; font-size: 12px; font-weight: 700; color: #64748b; text-transform: uppercase; letter-spacing: 0.04em; }
              .metric-value { display: block; font-size: 20px; font-weight: 800; color: #0f172a; }
              .note { margin-top: 18px; padding: 16px 18px; border-radius: 14px; background: #fff7ed; border: 1px solid #fed7aa; }
              .note-label { display: block; margin-bottom: 8px; font-size: 12px; font-weight: 700; color: #c2410c; text-transform: uppercase; letter-spacing: 0.04em; }
              .note p { margin: 0; color: #7c2d12; font-size: 14px; line-height: 1.6; }
              .footer { padding: 16px 28px 24px; border-top: 1px solid #e2e8f0; color: #64748b; font-size: 12px; }
              @media screen and (max-width: 640px) {
                .head, .body, .footer { padding-left: 18px; padding-right: 18px; }
                .title { font-size: 22px; }
                .label, .value, .metric { display: block; width: 100%; }
                .metrics { border-spacing: 0 10px; }
              }
            </style>
          </head>
          <body>
            <div class="shell">
              <div class="card">
                <div class="head">
                  <span class="badge">{{Encode(badge)}}</span>
                  <h1 class="title">{{Encode(title)}}</h1>
                </div>
                <div class="body">
                  {{introHtml}}
                  {{body}}
                </div>
                <div class="footer">AuraUp Email Service</div>
              </div>
            </div>
          </body>
        </html>
        """;
    }

    private static string BuildTable(IEnumerable<string> rows)
        => "<table role=\"presentation\" class=\"table\" cellspacing=\"0\" cellpadding=\"0\">" + string.Concat(rows) + "</table>";

    private static string BuildRow(string label, string value)
        => $"<tr><td class=\"label\">{Encode(label)}</td><td class=\"value\">{Encode(value)}</td></tr>";

    private static string BuildButton(string url, string label)
        => $"<div class=\"button-wrap\"><a class=\"button\" href=\"{EncodeAttribute(url)}\" target=\"_blank\" rel=\"noreferrer\">{Encode(label)}</a></div>";

    private static string BuildCallout(string title, string text)
        => $"<div class=\"callout\"><p class=\"callout-title\">{Encode(title)}</p><p class=\"callout-text\">{Encode(text)}</p></div>";

    private static string BuildMetaList(IEnumerable<(string Label, string Value)> items)
        => "<ul class=\"meta\">" + string.Concat(items.Select(item => $"<li><strong>{Encode(item.Label)}:</strong> {Encode(item.Value)}</li>")) + "</ul>";

    private static string BuildMetricsGrid(IEnumerable<(string Label, string Value)> items)
    {
        var list = items.ToList();
        var sb = new StringBuilder("<table role=\"presentation\" class=\"metrics\">");

        for (var index = 0; index < list.Count; index += 2)
        {
            sb.Append("<tr>");
            sb.Append(BuildMetricCell(list[index]));
            sb.Append(index + 1 < list.Count ? BuildMetricCell(list[index + 1]) : "<td class=\"metric\"></td>");
            sb.Append("</tr>");
        }

        sb.Append("</table>");
        return sb.ToString();
    }

    private static string BuildMetricCell((string Label, string Value) item)
        => $"<td class=\"metric\"><span class=\"metric-label\">{Encode(item.Label)}</span><span class=\"metric-value\">{Encode(item.Value)}</span></td>";

    private static string FormatDate(DateTime value)
        => value.ToUniversalTime().ToString("dd MMM yyyy HH:mm 'UTC'", CultureInfo.InvariantCulture);

    private static string Encode(string? value) => WebUtility.HtmlEncode(value ?? string.Empty);

    private static string EncodeAttribute(string? value) => WebUtility.HtmlEncode(value ?? string.Empty);
}
