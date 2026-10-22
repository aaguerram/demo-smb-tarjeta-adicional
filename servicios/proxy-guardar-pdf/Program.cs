using System.Diagnostics;
using System.Text.RegularExpressions;
using Microsoft.Net.Http.Headers;
using ProxyGuardarPdf;

const long TamanoMaximo = 20 * 1024 * 1024;

var builder = WebApplication.CreateBuilder(args);
builder.WebHost.ConfigureKestrel(k => k.Limits.MaxRequestBodySize = TamanoMaximo);
builder.Services.AddProblemDetails();
// Las variables obligatorias se validan al arrancar. Si falta alguna, el proceso termina con
// código 1 y un mensaje claro. Se usa Environment.Exit y no una excepción: en un contenedor el
// proceso es PID 1 y el abort() de una excepción no controlada lo deja colgado en lugar de terminar.
var configuracion = builder.Configuration;
var errores = new[] { "SMB_SERVIDOR", "SMB_RECURSO", "SMB_USUARIO", "SMB_CLAVE" }
    .Where(nombre => string.IsNullOrEmpty(configuracion[nombre]))
    .Select(nombre => $"Falta la variable de entorno {nombre}")
    .ToList();
var maxConexiones = configuracion.GetValue("SMB_MAX_CONEXIONES", 8);
if (maxConexiones < 1)
    errores.Add("SMB_MAX_CONEXIONES debe ser 1 o más");
if (errores.Count > 0)
{
    foreach (var error in errores) Console.Error.WriteLine(error);
    Environment.Exit(1);
}
string Requerida(string nombre) => configuracion[nombre]!;

builder.Services.AddSingleton(new OpcionesSmb(
    Servidor: Requerida("SMB_SERVIDOR"),
    Recurso: Requerida("SMB_RECURSO"),
    Dominio: configuracion["SMB_DOMINIO"] ?? "",
    Usuario: Requerida("SMB_USUARIO"),
    Clave: Requerida("SMB_CLAVE"),
    MaxConexiones: maxConexiones,
    Inactividad: TimeSpan.FromSeconds(builder.Configuration.GetValue("SMB_INACTIVIDAD_SEGUNDOS", 30)),
    VidaMaxima: TimeSpan.FromMinutes(builder.Configuration.GetValue("SMB_VIDA_MAXIMA_MINUTOS", 10))));
// Registrados por tipo: el contenedor de DI llama Dispose al apagar y el pool cierra sus conexiones.
builder.Services.AddSingleton<PoolConexionesSmb>();
builder.Services.AddSingleton<CarpetaPdf>();

var app = builder.Build();
app.UseExceptionHandler();
app.UseStatusCodePages();

// Contadores de peticiones de la API para la prueba de carga (cuántas recibió esta réplica).
var peticiones = new ContadorPeticiones();
app.Use(async (http, siguiente) =>
{
    if (!http.Request.Path.StartsWithSegments("/v1")) { await siguiente(http); return; }
    peticiones.Iniciar();
    try { await siguiente(http); }
    finally { peticiones.Terminar(http.Response.StatusCode); }
});

// Guarda o reemplaza el PDF {id}. PUT es idempotente: el cliente puede reintentar sin duplicar.
// El cuerpo es el PDF en binario (application/pdf), sin Base64 ni multipart.
app.MapPut("/v1/pdfs/{id}", async (string id, HttpRequest request, CarpetaPdf carpeta,
    ILogger<Program> log, CancellationToken ct) =>
{
    if (!IdValido(id))
        return Problema(400, "Identificador inválido", "Use de 1 a 64 letras, números o guiones.");
    if (!MediaTypeHeaderValue.TryParse(request.ContentType, out var tipo) || tipo.MediaType != "application/pdf")
        return Problema(415, "Tipo de contenido no soportado", "El cuerpo debe enviarse como application/pdf.");
    if (request.ContentLength is null)
        return Problema(411, "Falta Content-Length", "Envíe el tamaño del PDF en Content-Length.");
    if (request.ContentLength > TamanoMaximo)
        return Problema(413, "PDF demasiado grande", $"El máximo es {TamanoMaximo / 1024 / 1024} MB.");

    byte[]? sha256Esperado = null;
    if (request.Headers.TryGetValue("Content-Digest", out var digest))
    {
        sha256Esperado = LeerSha256(digest.ToString());
        if (sha256Esperado is null)
            return Problema(400, "Content-Digest inválido", "Use el formato sha-256=:<base64>: (RFC 9530).");
    }

    var reloj = Stopwatch.StartNew();
    try
    {
        var resultado = await carpeta.GuardarAsync($"{id}.pdf", request.Body, sha256Esperado, ct);
        log.LogInformation("PDF {Id} guardado: {Bytes} bytes en {Ms} ms", id, resultado.Bytes, reloj.ElapsedMilliseconds);

        var cuerpo = new
        {
            id,
            bytes = resultado.Bytes,
            sha256 = Convert.ToHexStringLower(resultado.Sha256),
            milisegundos = reloj.ElapsedMilliseconds,
        };
        var etag = $"\"{Convert.ToHexStringLower(resultado.Sha256)}\"";
        return resultado.Creado
            ? Results.Created($"/v1/pdfs/{id}", cuerpo).ConEtag(etag)
            : Results.Ok(cuerpo).ConEtag(etag);
    }
    catch (ContenidoInvalidoException ex)
    {
        return Problema(422, "El contenido no es un PDF", ex.Message);
    }
    catch (DigestNoCoincideException ex)
    {
        return Problema(400, "Content-Digest no coincide", ex.Message);
    }
    catch (BadHttpRequestException ex)
    {
        // El cliente cortó el envío o mandó menos bytes que Content-Length: no es una falla de SMB.
        log.LogWarning("Cuerpo incompleto al guardar {Id}: {Mensaje}", id, ex.Message);
        return Problema(ex.StatusCode, "Cuerpo incompleto", ex.Message);
    }
    catch (IOException ex)
    {
        log.LogError(ex, "Fallo SMB al guardar {Id}", id);
        return Problema(502, "No se pudo guardar en la carpeta compartida", ex.Message);
    }
});

// Devuelve el PDF {id} leyéndolo por bloques desde la carpeta compartida.
app.MapGet("/v1/pdfs/{id}", async (string id, HttpContext http, CarpetaPdf carpeta, CancellationToken ct) =>
{
    if (!IdValido(id))
        return Problema(400, "Identificador inválido", "Use de 1 a 64 letras, números o guiones.");
    try
    {
        var existe = await carpeta.LeerAsync($"{id}.pdf", tamano =>
        {
            http.Response.ContentType = "application/pdf";
            http.Response.ContentLength = tamano;
            return http.Response.Body;
        }, ct);
        return existe ? Results.Empty : Problema(404, "PDF no encontrado", $"No existe {id}.pdf.");
    }
    catch (IOException ex)
    {
        return Problema(502, "No se pudo leer la carpeta compartida", ex.Message);
    }
});

app.MapGet("/salud", () => Results.Ok(new { estado = "ok" }));
app.MapGet("/metricas", (PoolConexionesSmb pool) =>
    Results.Ok(new { proceso = MetricasProceso.Capturar(), smb = pool.Metricas(), http = peticiones.Metricas() }));

app.Run();

static bool IdValido(string id) => Regex.IsMatch(id, "^[A-Za-z0-9-]{1,64}$");

// RFC 9530: Content-Digest: sha-256=:<base64>:
static byte[]? LeerSha256(string cabecera)
{
    var coincidencia = Regex.Match(cabecera, @"(?:^|,)\s*sha-256=:([A-Za-z0-9+/=]+):");
    if (!coincidencia.Success) return null;
    try
    {
        var valor = Convert.FromBase64String(coincidencia.Groups[1].Value);
        return valor.Length == 32 ? valor : null;
    }
    catch (FormatException)
    {
        return null;
    }
}

// RFC 9457: errores como application/problem+json.
static IResult Problema(int estado, string titulo, string detalle) =>
    Results.Problem(statusCode: estado, title: titulo, detail: detalle);

static class ResultadosExtensiones
{
    public static IResult ConEtag(this IResult resultado, string etag) => new ConCabecera(resultado, etag);

    private sealed class ConCabecera(IResult interno, string etag) : IResult
    {
        public Task ExecuteAsync(HttpContext http)
        {
            http.Response.Headers.ETag = etag;
            return interno.ExecuteAsync(http);
        }
    }
}

sealed class ContadorPeticiones
{
    private long _total, _errores5xx, _enCurso, _maxEnCurso;

    public void Iniciar()
    {
        Interlocked.Increment(ref _total);
        var enCurso = Interlocked.Increment(ref _enCurso);
        for (var max = Interlocked.Read(ref _maxEnCurso); enCurso > max; max = Interlocked.Read(ref _maxEnCurso))
            Interlocked.CompareExchange(ref _maxEnCurso, enCurso, max);
    }

    public void Terminar(int estado)
    {
        Interlocked.Decrement(ref _enCurso);
        if (estado >= 500) Interlocked.Increment(ref _errores5xx);
    }

    public object Metricas() => new
    {
        total = Interlocked.Read(ref _total),
        errores5xx = Interlocked.Read(ref _errores5xx),
        enCurso = Interlocked.Read(ref _enCurso),
        maxEnCurso = Interlocked.Read(ref _maxEnCurso),
    };
}
