using System.Diagnostics;
using System.Text.RegularExpressions;
using Polly.CircuitBreaker;
using Polly.Timeout;
using SdGenerarSolicitudTarjetaPdf;

var builder = WebApplication.CreateBuilder(args);

// La URL del proxy se valida al arrancar. Si falta o no es una URL http(s) absoluta, el proceso
// termina con código 1 (Environment.Exit: como PID 1 en un contenedor, una excepción no
// controlada dejaría el proceso colgado en lugar de terminar).
var urlProxy = builder.Configuration["PROXY_GUARDAR_PDF_URL"];
if (!Uri.TryCreate(urlProxy, UriKind.Absolute, out var baseProxy) || baseProxy.Scheme is not ("http" or "https"))
{
    Console.Error.WriteLine(string.IsNullOrEmpty(urlProxy)
        ? "Falta la variable de entorno PROXY_GUARDAR_PDF_URL"
        : $"PROXY_GUARDAR_PDF_URL no es una URL http(s) absoluta: {urlProxy}");
    Environment.Exit(1);
}
// Con barra final, "v1/pdfs/{id}" se agrega a la ruta base en lugar de reemplazar su último segmento.
if (!baseProxy.AbsolutePath.EndsWith('/'))
    baseProxy = new Uri(baseProxy + "/");

builder.Services.AddProblemDetails();
builder.Services.AddHttpClient<ClienteProxyPdf>(c => c.BaseAddress = baseProxy)
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
    var errores = datos.Validar();
    if (errores.Count > 0)
        return Results.Problem(statusCode: 400, title: "Datos inválidos", detail: string.Join(" ", errores));

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
    catch (Exception ex) when (ex is HttpRequestException or TimeoutRejectedException or BrokenCircuitException)
    {
        // Sin conexión, sin respuesta a tiempo tras los reintentos, o circuito abierto por fallas seguidas.
        log.LogError(ex, "Proxy no disponible para {Id}", id);
        return Results.Problem(statusCode: 503, title: "proxy-guardar-pdf no disponible", detail: ex.Message);
    }
});

app.MapGet("/salud", () => Results.Ok(new { estado = "ok" }));
app.MapGet("/metricas", () => Results.Ok(new { proceso = MetricasProceso.Capturar() }));

app.Run();

public sealed record SolicitudTarjetaAdicional(
    string? TitularPrincipal,
    string? UltimosDigitosTarjetaPrincipal,
    string? NombreAdicional,
    string? IdentificacionAdicional,
    string? Parentesco,
    decimal CupoAsignado,
    string Id = "")
{
    public List<string> Validar()
    {
        var errores = new List<string>();
        void Requerido(string? valor, string campo)
        {
            if (string.IsNullOrWhiteSpace(valor)) errores.Add($"{campo} es obligatorio.");
            else if (valor.Length > 100) errores.Add($"{campo} admite hasta 100 caracteres.");
        }
        Requerido(TitularPrincipal, "titularPrincipal");
        Requerido(NombreAdicional, "nombreAdicional");
        Requerido(IdentificacionAdicional, "identificacionAdicional");
        Requerido(Parentesco, "parentesco");
        if (!Regex.IsMatch(UltimosDigitosTarjetaPrincipal ?? "", "^[0-9]{4}$"))
            errores.Add("ultimosDigitosTarjetaPrincipal debe tener 4 dígitos.");
        if (CupoAsignado <= 0)
            errores.Add("cupoAsignado debe ser mayor a 0.");
        return errores;
    }
}
