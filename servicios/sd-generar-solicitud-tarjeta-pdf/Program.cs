using System.Diagnostics;
using System.Text.RegularExpressions;
using SdGenerarSolicitudTarjetaPdf;

var builder = WebApplication.CreateBuilder(args);
builder.Services.AddProblemDetails();
builder.Services.AddHttpClient<ClienteProxyPdf>(c =>
        c.BaseAddress = new Uri(builder.Configuration["PROXY_GUARDAR_PDF_URL"]!))
    // Reintentos con espera exponencial, timeout por intento y circuit breaker.
    // Es seguro reintentar porque el proxy usa PUT con el id de la solicitud (idempotente).
    .AddStandardResilienceHandler();

var app = builder.Build();
app.UseExceptionHandler();
app.UseStatusCodePages();

// Recibe los datos de la solicitud, genera el PDF y lo envía al proxy para guardarlo en Windows.
app.MapPost("/v1/solicitudes-tarjeta-adicional/{id}/pdf", async (string id, SolicitudTarjetaAdicional datos,
    ClienteProxyPdf proxy, ILogger<Program> log, CancellationToken ct) =>
{
    if (!Regex.IsMatch(id, "^[A-Za-z0-9-]{1,64}$"))
        return Results.Problem(statusCode: 400, title: "Identificador inválido",
            detail: "Use de 1 a 64 letras, números o guiones.");
    if (!Regex.IsMatch(datos.UltimosDigitosTarjetaPrincipal ?? "", "^[0-9]{4}$"))
        return Results.Problem(statusCode: 400, title: "Datos inválidos",
            detail: "ultimosDigitosTarjetaPrincipal debe tener 4 dígitos.");

    var reloj = Stopwatch.StartNew();
    var pdf = GeneradorPdf.Generar(datos with { Id = id });
    var msGenerar = reloj.ElapsedMilliseconds;
    try
    {
        var guardado = await proxy.GuardarAsync(id, pdf, ct);
        log.LogInformation("Solicitud {Id}: PDF de {Bytes} bytes guardado en {Ubicacion}", id, pdf.Length, guardado.Ubicacion);
        return Results.Created(guardado.Ubicacion, new
        {
            id,
            pdf = guardado,
            milisegundos = new { generar = msGenerar, total = reloj.ElapsedMilliseconds },
        });
    }
    catch (ProxyPdfException ex)
    {
        log.LogError(ex, "El proxy rechazó el PDF de {Id}", id);
        return Results.Problem(statusCode: 502, title: "No se pudo guardar el PDF", detail: ex.Message);
    }
    catch (HttpRequestException ex)
    {
        log.LogError(ex, "Proxy no disponible para {Id}", id);
        return Results.Problem(statusCode: 503, title: "proxy-guardar-pdf no disponible", detail: ex.Message);
    }
});

app.MapGet("/salud", () => Results.Ok(new { estado = "ok" }));
app.MapGet("/metricas", () => Results.Ok(new { proceso = MetricasProceso.Capturar() }));

app.Run();

public sealed record SolicitudTarjetaAdicional(
    string TitularPrincipal,
    string UltimosDigitosTarjetaPrincipal,
    string NombreAdicional,
    string IdentificacionAdicional,
    string Parentesco,
    decimal CupoAsignado,
    string Id = "");
