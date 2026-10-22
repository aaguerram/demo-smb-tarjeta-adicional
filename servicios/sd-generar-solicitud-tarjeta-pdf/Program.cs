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

// Timeout, reintentos y circuit breaker contra el proxy, configurables por variables de entorno.
// Se validan al arrancar: un valor que no es número o está fuera de rango termina el proceso con
// código 1 y el motivo.
var errores = new List<string>();
T Numero<T>(string nombre, T defecto, T min, T max) where T : struct, IComparable<T>
{
    var texto = builder.Configuration[nombre];
    if (string.IsNullOrWhiteSpace(texto)) return defecto;
    try
    {
        var valor = builder.Configuration.GetValue<T>(nombre);
        if (valor.CompareTo(min) >= 0 && valor.CompareTo(max) <= 0) return valor;
    }
    catch (InvalidOperationException) { }
    errores.Add($"{nombre} debe ser un número entre {min} y {max} (se recibió \"{texto}\")");
    return defecto;
}
// Tiempo máximo de cada intento (conexión, envío del PDF y respuesta).
var segundosTimeout = Numero("PROXY_TIMEOUT_SEGUNDOS", 10, 1, 300);
// Reintentos: cuántos (0 los desactiva) y la espera base, que se duplica en cada reintento.
var reintentos = Numero("PROXY_REINTENTOS", 3, 0, 10);
var esperaReintentoMs = Numero("PROXY_REINTENTO_ESPERA_MS", 2000, 0, 60000);
// Circuit breaker: se abre si falla al menos la proporción indicada de los intentos, con un mínimo
// de intentos dentro de la ventana, y queda abierto la duración indicada.
var proporcionFallas = Numero("PROXY_CB_PROPORCION_FALLAS", 0.1, 0.01, 1.0);
var minimoIntentos = Numero("PROXY_CB_MINIMO_INTENTOS", 100, 2, 100000);
// La ventana por defecto es 30 s o el doble del timeout: el manejador exige al menos el doble.
var ventanaSegundos = Numero("PROXY_CB_VENTANA_SEGUNDOS", Math.Max(30, segundosTimeout * 2), 1, 3600);
var abiertoSegundos = Numero("PROXY_CB_DURACION_ABIERTO_SEGUNDOS", 5, 1, 3600);
if (errores.Count == 0 && ventanaSegundos < segundosTimeout * 2)
    errores.Add($"PROXY_CB_VENTANA_SEGUNDOS ({ventanaSegundos}) debe ser al menos el doble de PROXY_TIMEOUT_SEGUNDOS ({segundosTimeout})");
if (errores.Count > 0)
{
    foreach (var error in errores) Console.Error.WriteLine(error);
    Environment.Exit(1);
}
var timeoutIntento = TimeSpan.FromSeconds(segundosTimeout);
var esperaReintento = TimeSpan.FromMilliseconds(esperaReintentoMs);

builder.Services.AddProblemDetails();
builder.Services.AddHttpClient<ClienteProxyPdf>(c =>
    {
        c.BaseAddress = baseProxy;
        // Sin pool de conexiones: cada petición abre su conexión y el proxy la cierra al responder.
        c.DefaultRequestHeaders.ConnectionClose = true;
        // Los tiempos los controla el manejador de resiliencia, no HttpClient.Timeout.
        c.Timeout = Timeout.InfiniteTimeSpan;
    })
    // Reintentos con espera exponencial y circuit breaker. Es seguro reintentar porque el proxy
    // usa PUT con el id de la solicitud (idempotente).
    .AddStandardResilienceHandler(o =>
    {
        o.AttemptTimeout.Timeout = timeoutIntento;
        if (reintentos == 0)
            o.Retry.ShouldHandle = _ => ValueTask.FromResult(false); // Polly exige al menos 1: se apagan así
        else
            o.Retry.MaxRetryAttempts = reintentos;
        o.Retry.Delay = esperaReintento;
        // Total: todos los intentos más las esperas exponenciales entre ellos (espera × (2^n − 1)),
        // con 1 s de margen. Con los valores por defecto: 4 × 10 s + 14 s + 1 s = 55 s.
        o.TotalRequestTimeout.Timeout = timeoutIntento * (reintentos + 1)
            + esperaReintento * ((1 << reintentos) - 1) + TimeSpan.FromSeconds(1);
        o.CircuitBreaker.FailureRatio = proporcionFallas;
        o.CircuitBreaker.MinimumThroughput = minimoIntentos;
        o.CircuitBreaker.SamplingDuration = TimeSpan.FromSeconds(ventanaSegundos);
        o.CircuitBreaker.BreakDuration = TimeSpan.FromSeconds(abiertoSegundos);
    });

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
    catch (TimeoutRejectedException ex)
    {
        // El proxy no respondió dentro de PROXY_TIMEOUT_SEGUNDOS en ningún intento.
        log.LogError(ex, "Timeout del proxy para {Id}", id);
        return Results.Problem(statusCode: 504, title: "proxy-guardar-pdf no respondió a tiempo", detail: ex.Message);
    }
    catch (Exception ex) when (ex is HttpRequestException or BrokenCircuitException)
    {
        // Sin conexión tras los reintentos, o circuito abierto por fallas seguidas.
        log.LogError(ex, "Proxy no disponible para {Id}", id);
        return Results.Problem(statusCode: 503, title: "proxy-guardar-pdf no disponible", detail: ex.Message);
    }
});

app.MapGet("/salud", () => Results.Ok(new { estado = "ok" }));
app.MapGet("/metricas", () => Results.Ok(new { proceso = MetricasProceso.Capturar() }));

// Solo para pruebas de fuga de memoria y apagado por defecto: con DIAGNOSTICO_GC=true, fuerza un GC
// completo y compactante y devuelve la memoria que sigue viva. Comparar el valor tras dos cargas
// iguales dice si la memoria se estabiliza (sin fuga) o crece (fuga).
if (app.Configuration.GetValue("DIAGNOSTICO_GC", false))
    app.MapPost("/diagnostico/gc", () =>
    {
        GC.Collect(2, GCCollectionMode.Forced, blocking: true, compacting: true);
        GC.WaitForPendingFinalizers();
        GC.Collect(2, GCCollectionMode.Forced, blocking: true, compacting: true);
        return Results.Ok(new { vivoMB = GC.GetTotalMemory(false) / 1048576.0, proceso = MetricasProceso.Capturar() });
    });

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
